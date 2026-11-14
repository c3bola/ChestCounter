using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ChestCounter;

public sealed record ApiSyncSummary(
    int Synchronized,
    int AlreadySynchronized,
    int Pending,
    int Failed,
    string Message);

public sealed record ApiSyncState(
    bool IsSyncing,
    int Pending,
    int Failed,
    string Status,
    DateTimeOffset? LastSynchronizedAtUtc);

public sealed class ApiSyncService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ApiIntegrationConfigStore _configStore;
    private readonly string _queuePath;
    private readonly string _diagnosticsLogPath;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly object _queueGate = new();
    private readonly object _requestGate = new();
    private readonly object _diagnosticsGate = new();
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly SemaphoreSlim _manualOperationGate = new(1, 1);
    private readonly ApiAuthService _auth;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<QueueEntry> _queue;
    private readonly string? _queueLoadError;
    private TaskCompletionSource<bool> _requestsIdle = CompletedSource();
    private int _activeRequests;
    private Task? _worker;
    private volatile bool _isSyncing;
    private volatile string _status = "";
    private ApiOperationProgress? _progress;
    private int _processed;
    private int _succeeded;
    private int _failed;
    private bool _started;
    private bool _disposed;

    public ApiSyncService(
        ApiIntegrationConfigStore configStore,
        string queuePath,
        HttpClient? httpClient = null,
        bool mockAuthEndpointAuthorized = ApiAuthService.MockEndpointAuthorizedForProduction)
    {
        _configStore = configStore;
        _queuePath = queuePath;
        _diagnosticsLogPath = Path.Combine(Path.GetDirectoryName(queuePath)!, "api-sync.log");
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsHttpClient = httpClient == null;
        _auth = new ApiAuthService(
            (request, cancellationToken) => SendRequestAsync(request, ApiAuthService.MockEndpoint, replay: null, cancellationToken),
            mockAuthEndpointAuthorized);
        _auth.StatusChanged += status => AuthStatusChanged?.Invoke(status);

        try
        {
            _queue = LoadQueue();
            var recovered = false;
            foreach (var item in _queue.Where(item => item.State == "processing"))
            {
                item.State = "pending";
                item.NextAttemptUtc = DateTimeOffset.UtcNow;
                recovered = true;
            }
            if (recovered)
            {
                SaveQueue();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            _queue = new List<QueueEntry>();
            _queueLoadError = ex.Message;
            ErrorLog.Write("Load API synchronization queue", ex);
        }
    }

    public string ConfigurationPath => _configStore.Path;
    public ApiIntegrationConfig Configuration => _configStore.Load();
    public bool ConfigurationIsValid => _configStore.LoadError == null;
    public ApiAuthStatus AuthStatus => _auth.Status;
    public event Action<ApiAuthStatus>? AuthStatusChanged;
    public event Action<ApiSyncState>? StateChanged;
    public event Action<ApiOperationProgress>? ProgressChanged;
    public event Action<ApiOperationLogEntry>? DiagnosticReported;

    public ApiOperationProgress? Progress => _progress;

    public ApiSyncState State
    {
        get
        {
            lock (_queueGate)
            {
                return new ApiSyncState(
                    _isSyncing,
                    _queue.Count(item => item.State is "pending" or "processing"),
                    _queue.Count(item => item.State == "failed"),
                    _status,
                    _queue.Where(item => item.State == "synchronized")
                        .Select(item => item.CompletedAtUtc)
                        .Max());
            }
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        if (_queueLoadError != null)
        {
            SetStatus("The local synchronization queue could not be read; synchronization is paused.");
            return;
        }
        _worker = Task.Run(() => WorkerAsync(_stop.Token));
        SignalWorker();
    }

    public void QueueReplay(MatchRecord match, FileInfo replayFile)
    {
        if (match.ReplayFileSha256 is { Length: 64 } fileSha256)
        {
            try
            {
                AddToQueue(CreateSnapshot(match, replayFile.Name, fileSha256, match.ReplayFileSizeBytes,
                    match.ReplayFileCreatedAtUtc is { } createdAt
                        ? new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc))
                        : null));
            }
            catch (Exception ex)
            {
                ErrorLog.Write($"Queue replay for API synchronization ({replayFile.Name})", ex);
                SetStatus("A replay was saved locally but could not be added to the API synchronization queue.");
            }
            return;
        }

        _ = EnqueueSafelyAsync(match, replayFile);
    }

    public Task QueueReplayAsync(
        MatchRecord match,
        FileInfo replayFile,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(match, replayFile, cancellationToken);

    public void QueueHistorical(TrackerData data, IEnumerable<FileInfo> replayFiles)
    {
        if (_queueLoadError != null)
        {
            return;
        }

        var filesByKey = replayFiles.ToDictionary(Tracker.FileKey, StringComparer.OrdinalIgnoreCase);
        var knownSessions = data.Matches.Select(match => match.SessionId).ToHashSet(StringComparer.Ordinal);
        var latestFileBySession = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
        foreach (var (fileKey, sessionId) in data.ProcessedFiles)
        {
            if (!filesByKey.TryGetValue(fileKey, out var file) || !knownSessions.Contains(sessionId))
            {
                continue;
            }
            if (!latestFileBySession.TryGetValue(sessionId, out var previous) || file.LastWriteTimeUtc > previous.LastWriteTimeUtc)
            {
                latestFileBySession[sessionId] = file;
            }
        }

        var snapshots = new List<ReplayIngestionSnapshot>();
        var filesToHash = new List<(MatchRecord Match, FileInfo File)>();
        foreach (var match in data.Matches)
        {
            latestFileBySession.TryGetValue(match.SessionId, out var file);
            if (match.ReplayFileSha256 is { Length: 64 })
            {
                try
                {
                    DateTimeOffset? createdAt = match.ReplayFileCreatedAtUtc is { } created
                        ? new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc))
                        : null;
                    snapshots.Add(CreateSnapshot(
                        match,
                        match.ReplayFile,
                        match.ReplayFileSha256,
                        match.ReplayFileSizeBytes,
                        createdAt));
                }
                catch (Exception ex)
                {
                    ErrorLog.Write($"Queue historical replay for API synchronization ({match.ReplayFile})", ex);
                }
            }
            else if (file != null)
            {
                filesToHash.Add((match, file));
            }
        }
        try
        {
            AddToQueue(snapshots);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Queue historical replays for API synchronization", ex);
            SetStatus("Historical replay data remains local but could not be added to the API synchronization queue.");
            return;
        }
        if (filesToHash.Count > 0)
        {
            _ = QueueHistoricalFilesSafelyAsync(filesToHash);
        }
    }

    public async Task<ApiSyncSummary> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        BeginExternalRequest();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var operationAcquired = false;
        var syncAcquired = false;
        try
        {
            await _manualOperationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            operationAcquired = true;
            await _syncGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            syncAcquired = true;
            BeginOperation("Manual synchronization", "Validating API configuration", "Waiting to start synchronization.");
            ReportDiagnostic(ApiDiagnosticSeverity.Information, "Manual synchronization requested.");
            return await SyncNowCoreAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportDiagnostic(ApiDiagnosticSeverity.Error, "Manual synchronization stopped unexpectedly.", errorDetails: ex.Message);
            CompleteOperation("Manual synchronization was interrupted.", ApiDiagnosticSeverity.Error);
            throw;
        }
        finally
        {
            if (syncAcquired)
            {
                _syncGate.Release();
            }
            if (operationAcquired)
            {
                _manualOperationGate.Release();
            }
            EndExternalRequest();
        }
    }

    private async Task<ApiSyncSummary> SyncNowCoreAsync(CancellationToken cancellationToken)
    {
        if (_queueLoadError != null)
        {
            CompleteOperation("Synchronization not started: the local queue could not be read.", ApiDiagnosticSeverity.Error);
            return SetSummary(0, 0, "The local synchronization queue could not be read.");
        }

        UpdateProgress("Validating API configuration", "Loading API configuration.");
        ApiIntegrationConfig config;
        try
        {
            config = _configStore.Load();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Load API configuration before manual synchronization", ex);
            ReportDiagnostic(ApiDiagnosticSeverity.Error, "Could not load API configuration.", errorDetails: ex.Message);
            CompleteOperation("Synchronization not started: the API configuration could not be read.", ApiDiagnosticSeverity.Error);
            return SetSummary(0, 0, "The API configuration could not be read.");
        }

        if (_configStore.LoadError != null)
        {
            ReportDiagnostic(ApiDiagnosticSeverity.Error, "The API configuration file is invalid.", errorDetails: _configStore.LoadError);
            CompleteOperation("Synchronization not started: the API configuration file is invalid.", ApiDiagnosticSeverity.Error);
            return SetSummary(0, 0, "The API configuration file is invalid.");
        }
        if (!config.Enabled)
        {
            ReportDiagnostic(ApiDiagnosticSeverity.Warning, "API synchronization is disabled in api-config.json.");
            CompleteOperation("Synchronization not started: API synchronization is disabled.", ApiDiagnosticSeverity.Warning);
            return SetSummary(0, 0, "API synchronization is disabled in api-config.json.");
        }
        var authError = CountByState("pending") + CountByState("failed") > 0
            ? await TryAuthenticateAsync(config, cancellationToken).ConfigureAwait(false)
            : null;
        if (authError != null)
        {
            CompleteOperation("Synchronization not started: " + authError, ApiDiagnosticSeverity.Error);
            return SetSummary(0, 0, authError);
        }

        UpdateProgress("Consulting synchronization queue", "Reading pending and failed operations.");
        var alreadySynchronized = CountByState("synchronized");
        lock (_queueGate)
        {
            foreach (var item in _queue.Where(item => item.State == "failed"))
            {
                item.State = "pending";
                item.NextAttemptUtc = DateTimeOffset.UtcNow;
            }
            SaveQueue();
        }

        var total = CountByState("pending");
        ResetProgressCounters();
        UpdateProgress("Preparing synchronization", $"{total} operation(s) ready to process.", total: total, processed: 0, succeeded: 0, failed: 0);
        var synchronized = await ProcessDueAsync(manual: true, cancellationToken, gateAlreadyAcquired: true).ConfigureAwait(false);
        var state = State;
        var totalOperations = alreadySynchronized + total;
        var elapsed = DateTimeOffset.UtcNow - (_progress?.StartedAtUtc ?? DateTimeOffset.UtcNow);
        var message = state.Pending == 0 && state.Failed == 0
            ? totalOperations == 0
                ? "There are no pending records to synchronize."
                : $"Synchronization completed successfully: {totalOperations} total, {synchronized + alreadySynchronized} synchronized, 0 failed, 0 pending. Duration: {elapsed:hh\\:mm\\:ss}."
            : $"Synchronization finished with issues: {totalOperations} total, {synchronized + alreadySynchronized} synchronized, {state.Failed} failed, {state.Pending} pending. Duration: {elapsed:hh\\:mm\\:ss}.";
        SetStatus(message);
        CompleteOperation(message, state.Pending == 0 && state.Failed == 0
            ? ApiDiagnosticSeverity.Success
            : ApiDiagnosticSeverity.Warning);
        return new ApiSyncSummary(synchronized, alreadySynchronized, state.Pending, state.Failed, message);
    }

    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        BeginExternalRequest();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var operationAcquired = false;
        var syncAcquired = false;
        try
        {
            await _manualOperationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            operationAcquired = true;
            await _syncGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            syncAcquired = true;
            return await TestConnectionCoreAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportDiagnostic(ApiDiagnosticSeverity.Error, "Connection test stopped unexpectedly.", errorDetails: ex.Message);
            CompleteOperation("Connection test was interrupted.", ApiDiagnosticSeverity.Error);
            throw;
        }
        finally
        {
            if (syncAcquired)
            {
                _syncGate.Release();
            }
            if (operationAcquired)
            {
                _manualOperationGate.Release();
            }
            EndExternalRequest();
        }
    }

    private async Task<string> TestConnectionCoreAsync(CancellationToken cancellationToken)
    {
        BeginOperation("Connection test", "Validating API configuration", "Starting a read-only API connection test.");
        ReportDiagnostic(ApiDiagnosticSeverity.Information, "Connection test requested.");
        var config = _configStore.Load();
        if (_configStore.LoadError != null)
        {
            ReportDiagnostic(ApiDiagnosticSeverity.Error, "The API configuration file is invalid.", errorDetails: _configStore.LoadError);
            CompleteOperation("Connection test not performed: the API configuration file is invalid.", ApiDiagnosticSeverity.Error);
            return "The API configuration file is invalid.";
        }
        if (!config.Enabled)
        {
            ReportDiagnostic(ApiDiagnosticSeverity.Warning, "API synchronization is disabled in api-config.json.");
            CompleteOperation("Connection test not performed: API synchronization is disabled.", ApiDiagnosticSeverity.Warning);
            return "API synchronization is disabled in api-config.json.";
        }
        var authError = await TryAuthenticateAsync(config, cancellationToken).ConfigureAwait(false);
        if (authError != null)
        {
            CompleteOperation("Connection test not performed: " + authError, ApiDiagnosticSeverity.Error);
            return authError;
        }

        try
        {
            var endpoint = "replay-files?sha256=" + new string('0', 64);
            UpdateProgress("Connecting to API", "Sending a documented, authenticated, read-only request.");
            using var response = await SendAuthorizedAsync(
                HttpMethod.Get, config, endpoint, null, replay: null, cancellationToken).ConfigureAwait(false);
            var message = response.StatusCode == HttpStatusCode.OK
                ? $"API responded successfully. HTTP {(int)response.StatusCode}."
                : response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "The API rejected the Bearer token. HTTP 401.",
                    HttpStatusCode.Forbidden => "The token is valid but does not have permission for this operation. HTTP 403.",
                    HttpStatusCode.NotFound => "The documented connection-test operation was not found. HTTP 404.",
                    HttpStatusCode.RequestTimeout => "The API timed out while processing the connection test. HTTP 408.",
                    HttpStatusCode.TooManyRequests => "The API is rate limiting requests. HTTP 429.",
                    _ when (int)response.StatusCode >= 500 => $"The API server returned an error. HTTP {(int)response.StatusCode}.",
                    _ => $"The API test returned HTTP {(int)response.StatusCode}.",
                };
            SetStatus(message);
            CompleteOperation(message, response.StatusCode == HttpStatusCode.OK
                ? ApiDiagnosticSeverity.Success
                : ApiDiagnosticSeverity.Error,
                response.StatusCode == HttpStatusCode.OK ? "API responded successfully." : $"The API returned HTTP {(int)response.StatusCode}.");
            return message;
        }
        catch (ApiRequestException ex)
        {
            SetStatus(ex.Message);
            ReportDiagnostic(ApiDiagnosticSeverity.Error, ex.Message, retryable: ex.IsTransient);
            CompleteOperation(ex.Message, ApiDiagnosticSeverity.Error);
            return ex.Message;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            const string message = "API test timed out.";
            SetStatus(message);
            ReportDiagnostic(ApiDiagnosticSeverity.Error, message, errorDetails: "The HTTP request exceeded the configured timeout.", retryable: true);
            CompleteOperation(message, ApiDiagnosticSeverity.Error);
            return message;
        }
        catch (HttpRequestException ex)
        {
            ErrorLog.Write("Test API connection GET replay-files", ex);
            const string message = "Could not connect to the API. Check the URL and network.";
            SetStatus(message);
            ReportDiagnostic(ApiDiagnosticSeverity.Error, message, errorDetails: ex.Message, retryable: true);
            CompleteOperation(message, ApiDiagnosticSeverity.Error);
            return message;
        }
    }

    public void UpdateBaseUrl(string baseUrl)
    {
        _configStore.UpdateBaseUrl(baseUrl);
        SetStatus("API URL saved.");
    }

    private async Task EnqueueSafelyAsync(MatchRecord match, FileInfo replayFile)
    {
        try
        {
            await EnqueueAsync(match, replayFile, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Queue replay for API synchronization ({replayFile.Name})", ex);
            SetStatus("A replay was saved locally but could not be added to the API synchronization queue.");
        }
    }

    private async Task EnqueueAsync(MatchRecord match, FileInfo replayFile, CancellationToken cancellationToken)
    {
        if (_queueLoadError != null)
        {
            SetStatus("The local synchronization queue could not be read; replay data remains local.");
            return;
        }

        await using var stream = new FileStream(
            replayFile.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        var parserVersion = typeof(FortniteReplayReader.ReplayReader).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        DateTimeOffset? fileCreatedAt = replayFile.CreationTimeUtc == DateTime.MinValue
            ? null
            : new DateTimeOffset(replayFile.CreationTimeUtc, TimeSpan.Zero);
        var snapshot = ReplayIngestionMapper.Create(
            match,
            replayFile.Name,
            hash,
            replayFile.Length,
            fileCreatedAt,
            AppInfo.VersionText,
            parserVersion);

        AddToQueue(snapshot);
    }

    private async Task QueueHistoricalFilesSafelyAsync(
        IReadOnlyList<(MatchRecord Match, FileInfo File)> historicalFiles)
    {
        foreach (var (match, file) in historicalFiles)
        {
            try
            {
                await EnqueueAsync(match, file, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ErrorLog.Write($"Queue historical replay for API synchronization ({file.Name})", ex);
            }
        }
    }

    private ReplayIngestionSnapshot CreateSnapshot(
        MatchRecord match,
        string fileName,
        string hash,
        long? fileSizeBytes,
        DateTimeOffset? fileCreatedAt)
    {
        var parserVersion = typeof(FortniteReplayReader.ReplayReader).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        return ReplayIngestionMapper.Create(
            match,
            fileName,
            hash,
            fileSizeBytes,
            fileCreatedAt,
            AppInfo.VersionText,
            parserVersion);
    }

    private void AddToQueue(ReplayIngestionSnapshot snapshot) => AddToQueue([snapshot]);

    private void AddToQueue(IEnumerable<ReplayIngestionSnapshot> snapshots)
    {
        if (_queueLoadError != null)
        {
            SetStatus("The local synchronization queue could not be read; replay data remains local.");
            return;
        }

        var added = false;
        lock (_queueGate)
        {
            foreach (var snapshot in snapshots)
            {
                var hash = snapshot.ReplayFile.FileSha256;
                if (_queue.Any(item =>
                        item.ReplayFile.FileSha256 == hash
                        && item.Request.IdempotencyKey == snapshot.Request.IdempotencyKey))
                {
                    continue;
                }

                _queue.Add(new QueueEntry
                {
                    ReplayFile = snapshot.ReplayFile,
                    Request = snapshot.Request,
                    State = "pending",
                    NextAttemptUtc = DateTimeOffset.UtcNow,
                });
                added = true;
            }
            if (added)
            {
                SaveQueue();
            }
        }

        if (added)
        {
            PublishState();
            SignalWorker();
        }
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                await ProcessDueAsync(manual: false, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorLog.Write("API synchronization worker", ex);
            SetStatus("The API synchronization worker stopped unexpectedly.");
        }
    }

    private async Task<int> ProcessDueAsync(bool manual, CancellationToken cancellationToken, bool gateAlreadyAcquired = false)
    {
        var acquiredHere = false;
        if (manual && !gateAlreadyAcquired)
        {
            await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquiredHere = true;
        }
        else if (!manual)
        {
            if (!await _syncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }
            acquiredHere = true;
        }

        var synchronized = 0;
        try
        {
            var config = _configStore.Load();
            if (_configStore.LoadError != null || !config.Enabled)
            {
                bool hasPending;
                lock (_queueGate)
                {
                    hasPending = _queue.Any(item => item.State is "pending" or "failed");
                }
                if (hasPending)
                {
                    SetStatus(_configStore.LoadError != null
                        ? "The API configuration file is invalid."
                        : "API synchronization is disabled in api-config.json.");
                }
                return synchronized;
            }

            var now = DateTimeOffset.UtcNow;
            QueueEntry[] due;
            lock (_queueGate)
            {
                due = _queue
                    .Where(item => item.State == "pending" && (manual || item.NextAttemptUtc <= now))
                    .OrderBy(item => item.NextAttemptUtc)
                    .ToArray();
            }
            if (due.Length == 0)
            {
                if (manual)
                {
                    SetStatus("There are no pending records to synchronize.");
                    UpdateProgress("Synchronization complete", "No operations were pending.", total: 0, processed: 0, succeeded: 0, failed: 0);
                }
                return synchronized;
            }

            if (!manual && _auth.IsInFailureCooldown)
            {
                return synchronized;
            }
            var processAuthError = await TryAuthenticateAsync(config, cancellationToken).ConfigureAwait(false);
            if (processAuthError != null)
            {
                SetStatus(processAuthError);
                return synchronized;
            }

            _isSyncing = true;
            ResetProgressCounters();
            if (!manual)
            {
                BeginOperation("Automatic synchronization", "Preparing synchronization", $"Found {due.Length} due operation(s).");
                ReportDiagnostic(ApiDiagnosticSeverity.Information, $"Automatic synchronization found {due.Length} due operation(s).");
            }
            UpdateProgress("Sending replay data", $"Processing {due.Length} replay(s).", total: due.Length, processed: 0, succeeded: 0, failed: 0);
            SetStatus($"Synchronizing {due.Length} replay(s)...");
            foreach (var entry in due)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateProgress("Processing replay", $"Processing {entry.ReplayFile.FileName}.", entry.ReplayFile.FileName);
                ReportDiagnostic(
                    ApiDiagnosticSeverity.Progress,
                    $"Processing replay {entry.ReplayFile.FileName}.",
                    replayFileName: entry.ReplayFile.FileName,
                    replaySha256: entry.ReplayFile.FileSha256);
                var succeeded = await ProcessEntryAsync(entry, config, cancellationToken).ConfigureAwait(false);
                _processed++;
                if (succeeded)
                {
                    synchronized++;
                    _succeeded++;
                }
                else
                {
                    _failed++;
                }

                var state = State;
                UpdateProgress(
                    "Processing replay",
                    succeeded
                        ? $"Replay {entry.ReplayFile.FileName} synchronized."
                        : $"Replay {entry.ReplayFile.FileName} did not complete.",
                    total: due.Length,
                    processed: _processed,
                    succeeded: _succeeded,
                    failed: _failed,
                    pending: state.Pending,
                    currentReplay: entry.ReplayFile.FileName);
            }
            UpdateProgress("Synchronization complete", "All selected operations have been processed.", total: due.Length,
                processed: _processed, succeeded: _succeeded, failed: _failed, pending: State.Pending);
            return synchronized;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return synchronized;
        }
        finally
        {
            _isSyncing = false;
            if (acquiredHere)
            {
                _syncGate.Release();
            }
            PublishState();
        }
    }

    private async Task<bool> ProcessEntryAsync(QueueEntry entry, ApiIntegrationConfig config, CancellationToken cancellationToken)
    {
        lock (_queueGate)
        {
            var current = FindEntry(entry.ReplayFile.FileSha256, entry.Request.IdempotencyKey);
            if (current == null || current.State != "pending")
            {
                return false;
            }
            current.State = "processing";
            current.Attempts++;
            current.LastError = null;
            entry = current.Copy();
            SaveQueue();
        }
        PublishState();

        try
        {
            UpdateProgress("Registering replay file", $"Registering {entry.ReplayFile.FileName}.", entry.ReplayFile.FileName);
            await RegisterReplayFileAsync(entry, config, cancellationToken).ConfigureAwait(false);
            UpdateProgress("Sending ingestion data", $"Sending extracted match data for {entry.ReplayFile.FileName}.", entry.ReplayFile.FileName);
            await IngestReplayAsync(entry, config, cancellationToken).ConfigureAwait(false);
            lock (_queueGate)
            {
                var current = FindEntry(entry.ReplayFile.FileSha256, entry.Request.IdempotencyKey);
                if (current != null)
                {
                    current.State = "synchronized";
                    current.CompletedAtUtc = DateTimeOffset.UtcNow;
                    current.NextAttemptUtc = null;
                    current.LastError = null;
                    SaveQueue();
                }
            }
            SetStatus($"Synchronized replay {entry.ReplayFile.FileName}.");
            return true;
        }
        catch (ApiRequestException ex)
        {
            SetFailedOrPending(entry, ex.Message, ex.IsTransient, ex.RetryAfter, ex.StatusCode, ex.Endpoint);
            return false;
        }
        catch (HttpRequestException ex)
        {
            SetFailedOrPending(
                entry,
                "Network error.",
                transient: true,
                retryAfter: null,
                statusCode: null,
                "replay ingestion",
                technicalDetails: ex.Message);
            ErrorLog.Write($"API replay ingestion {entry.ReplayFile.FileSha256} attempt {entry.Attempts}", ex);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetFailedOrPending(
                entry,
                "Request timed out.",
                transient: true,
                retryAfter: null,
                statusCode: null,
                "replay ingestion",
                technicalDetails: "The HTTP request exceeded the configured timeout.");
            return false;
        }
        catch (OperationCanceledException)
        {
            lock (_queueGate)
            {
                var current = FindEntry(entry.ReplayFile.FileSha256, entry.Request.IdempotencyKey);
                if (current != null)
                {
                    current.State = "pending";
                    current.NextAttemptUtc = DateTimeOffset.UtcNow;
                    SaveQueue();
                }
            }
            throw;
        }
        catch (Exception ex)
        {
            SetFailedOrPending(
                entry,
                "Unexpected synchronization error.",
                transient: false,
                retryAfter: null,
                statusCode: null,
                "replay ingestion",
                technicalDetails: ex.Message);
            ErrorLog.Write($"API replay ingestion {entry.ReplayFile.FileSha256} attempt {entry.Attempts}", ex);
            return false;
        }
    }

    private async Task RegisterReplayFileAsync(QueueEntry entry, ApiIntegrationConfig config, CancellationToken cancellationToken)
    {
        var endpoint = "replay-files";
        using var response = await SendAuthorizedAsync(
            HttpMethod.Post, config, endpoint, entry.ReplayFile, entry, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            await VerifyReplayFileExistsAsync(entry, config, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw CreateRequestException(response, endpoint);
    }

    private async Task VerifyReplayFileExistsAsync(QueueEntry entry, ApiIntegrationConfig config, CancellationToken cancellationToken)
    {
        var endpoint = $"replay-files/{entry.ReplayFile.FileSha256}";
        using var response = await SendAuthorizedAsync(
            HttpMethod.Get, config, endpoint, null, entry, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw CreateRequestException(response, endpoint);
        }
    }

    private async Task IngestReplayAsync(QueueEntry entry, ApiIntegrationConfig config, CancellationToken cancellationToken)
    {
        var endpoint = $"replay-files/{entry.ReplayFile.FileSha256}/processing-attempts";
        using var response = await SendAuthorizedAsync(
            HttpMethod.Post, config, endpoint, entry.Request, entry, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            return;
        }

        throw CreateRequestException(response, endpoint);
    }

    private async Task<HttpResponseMessage> SendRequestAsync(
        HttpRequestMessage request,
        string endpoint,
        QueueEntry? replay,
        CancellationToken cancellationToken)
    {
        var requestBody = request.Content == null
            ? null
            : RedactSensitiveText(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var replayFileName = replay?.ReplayFile.FileName;
        var replaySha256 = replay?.ReplayFile.FileSha256;
        ReportDiagnostic(
            ApiDiagnosticSeverity.Progress,
            "Sending HTTP request.",
            request.Method.Method,
            endpoint,
            replayFileName,
            replaySha256,
            requestBody);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            stopwatch.Stop();
            ReportDiagnostic(
                ApiDiagnosticSeverity.Error,
                ex is OperationCanceledException ? "HTTP request was interrupted or timed out." : "HTTP request failed before receiving a response.",
                request.Method.Method,
                endpoint,
                replayFileName,
                replaySha256,
                requestBody,
                errorDetails: ex.ToString(),
                retryable: true,
                duration: stopwatch.Elapsed);
            throw;
        }

        string? responseBody;
        try
        {
            if (endpoint == ApiAuthService.MockEndpoint && response.IsSuccessStatusCode)
            {
                // The auth service reads this body after the diagnostic preview; keep it replayable.
                await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            }
            responseBody = await ReadResponseSummaryAsync(response.Content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException)
        {
            stopwatch.Stop();
            response.Dispose();
            ReportDiagnostic(
                ApiDiagnosticSeverity.Error,
                "Could not read the API response body.",
                request.Method.Method,
                endpoint,
                replayFileName,
                replaySha256,
                requestBody,
                (int)response.StatusCode,
                errorDetails: ex.ToString(),
                retryable: true,
                duration: stopwatch.Elapsed);
            throw;
        }
        stopwatch.Stop();
        var requestSucceeded = response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created;
        var isRetryable = response.StatusCode is HttpStatusCode.Accepted
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            || (int)response.StatusCode >= 500;
        ReportDiagnostic(
            requestSucceeded ? ApiDiagnosticSeverity.Success : isRetryable ? ApiDiagnosticSeverity.Warning : ApiDiagnosticSeverity.Error,
            requestSucceeded ? "HTTP request completed." : "HTTP request did not complete successfully.",
            request.Method.Method,
            endpoint,
            replayFileName,
            replaySha256,
            requestBody,
            (int)response.StatusCode,
            RedactSensitiveText(responseBody),
            retryable: isRetryable,
            duration: stopwatch.Elapsed);
        return response;
    }

    private static async Task<string?> ReadResponseSummaryAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content == null)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (buffer.Length <= 8192)
        {
            var remaining = (int)Math.Min(chunk.Length, 8193 - buffer.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, remaining), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        var isTruncated = bytes.Length > 8192;
        var summary = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 8192));
        return isTruncated ? summary + Environment.NewLine + "[Response preview truncated.]" : summary;
    }

    private void BeginOperation(string operation, string stage, string status)
    {
        var config = _configStore.Load();
        var pending = State.Pending;
        var progress = new ApiOperationProgress(
            operation,
            config.BaseUrl,
            DateTimeOffset.UtcNow,
            status,
            stage,
            null,
            0,
            0,
            0,
            pending,
            null);
        _progress = progress;
        PublishProgress(progress);
        ReportDiagnostic(ApiDiagnosticSeverity.Progress, status);
    }

    private void ResetProgressCounters()
    {
        _processed = 0;
        _succeeded = 0;
        _failed = 0;
    }

    private void UpdateProgress(
        string stage,
        string status,
        string? currentReplay = null,
        int? total = null,
        int? processed = null,
        int? succeeded = null,
        int? failed = null,
        int? pending = null)
    {
        var current = _progress;
        if (current == null)
        {
            BeginOperation("Synchronization", stage, status);
            current = _progress!;
        }

        var updated = current with
        {
            Stage = stage,
            Status = status,
            Total = total ?? current.Total,
            Processed = processed ?? current.Processed,
            Succeeded = succeeded ?? current.Succeeded,
            Failed = failed ?? current.Failed,
            Pending = pending ?? State.Pending,
            CurrentReplay = currentReplay,
        };
        _progress = updated;
        PublishProgress(updated);
    }

    private void CompleteOperation(
        string status,
        ApiDiagnosticSeverity severity,
        string? description = null)
    {
        UpdateProgress(severity == ApiDiagnosticSeverity.Error ? "Operation failed" : "Operation complete", status, pending: State.Pending);
        TimeSpan? duration = _progress == null ? null : DateTimeOffset.UtcNow - _progress.StartedAtUtc;
        ReportDiagnostic(severity, description ?? status, duration: duration);
    }

    private void PublishProgress(ApiOperationProgress progress)
    {
        try
        {
            ProgressChanged?.Invoke(progress);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Publish API synchronization progress", ex);
        }
    }

    private void ReportDiagnostic(
        ApiDiagnosticSeverity severity,
        string description,
        string? method = null,
        string? endpoint = null,
        string? replayFileName = null,
        string? replaySha256 = null,
        string? requestBody = null,
        int? httpStatusCode = null,
        string? responseSummary = null,
        string? errorDetails = null,
        bool? retryable = null,
        TimeSpan? duration = null)
    {
        var token = _auth.CurrentToken;
        var entry = new ApiOperationLogEntry(
            DateTimeOffset.UtcNow,
            severity,
            RedactSensitiveText(description, token),
            method,
            RedactSensitiveText(endpoint, token),
            RedactSensitiveText(replayFileName, token),
            replaySha256,
            RedactSensitiveText(requestBody, token),
            httpStatusCode,
            RedactSensitiveText(responseSummary, token),
            RedactSensitiveText(errorDetails, token),
            retryable,
            duration);

        WriteDiagnosticLog(entry);
        try
        {
            DiagnosticReported?.Invoke(entry);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Publish API synchronization diagnostic", ex);
        }
    }

    private void WriteDiagnosticLog(ApiOperationLogEntry entry)
    {
        try
        {
            lock (_diagnosticsGate)
            {
                const long maxBytes = 1024 * 1024;
                const int retainedFiles = 3;
                if (File.Exists(_diagnosticsLogPath) && new FileInfo(_diagnosticsLogPath).Length >= maxBytes)
                {
                    var oldest = _diagnosticsLogPath + "." + retainedFiles;
                    if (File.Exists(oldest))
                    {
                        File.Delete(oldest);
                    }
                    for (var index = retainedFiles - 1; index >= 1; index--)
                    {
                        var source = _diagnosticsLogPath + "." + index;
                        if (File.Exists(source))
                        {
                            File.Move(source, _diagnosticsLogPath + "." + (index + 1), overwrite: true);
                        }
                    }
                    File.Move(_diagnosticsLogPath, _diagnosticsLogPath + ".1", overwrite: true);
                }

                var line = string.Join(
                    " | ",
                    entry.TimestampUtc.ToString("O"),
                    _progress?.Operation ?? "-",
                    entry.Severity,
                    entry.Method ?? "-",
                    entry.Endpoint ?? "-",
                    entry.ReplayFileName ?? "-",
                    entry.ReplaySha256 ?? "-",
                    entry.HttpStatusCode?.ToString() ?? "-",
                    entry.Duration?.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                    entry.Description,
                    entry.ErrorDetails ?? "-");
                File.AppendAllText(_diagnosticsLogPath, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Write API synchronization diagnostic log", ex);
        }
    }

    private static string? RedactSensitiveText(string? value, string? bearerToken = null)
    {
        if (value == null)
        {
            return null;
        }

        var redacted = value;
        if (!string.IsNullOrEmpty(bearerToken))
        {
            redacted = redacted.Replace(bearerToken, "[REDACTED]", StringComparison.Ordinal);
        }
        redacted = Regex.Replace(
            redacted,
            "(?i)([\"']?(?:authorization|bearerToken|access_token|refresh_token|token|jwt|password|secret|credential|api.?key|client.?secret)[\"']?\\s*[:=]\\s*[\"']?)(?:bearer\\s+)?[^\"'\\s,}]+",
            "$1[REDACTED]");
        return redacted;
    }

    private void SetFailedOrPending(
        QueueEntry entry,
        string message,
        bool transient,
        TimeSpan? retryAfter,
        HttpStatusCode? statusCode,
        string endpoint,
        string? technicalDetails = null)
    {
        var retryDelay = retryAfter ?? Backoff(entry.Attempts);
        lock (_queueGate)
        {
            var current = FindEntry(entry.ReplayFile.FileSha256, entry.Request.IdempotencyKey);
            if (current != null)
            {
                current.State = transient ? "pending" : "failed";
                current.LastError = message;
                current.NextAttemptUtc = transient ? DateTimeOffset.UtcNow + retryDelay : null;
                SaveQueue();
            }
        }

        var code = statusCode is { } status ? $" HTTP {(int)status}." : "";
        var result = transient
            ? $"Temporary API error for {entry.ReplayFile.FileName}; retry scheduled."
            : $"API rejected {entry.ReplayFile.FileName}.{code}";
        ErrorLog.Write(
            $"API {endpoint} replay {entry.ReplayFile.FileSha256}, {DateTimeOffset.UtcNow:O}, attempt {entry.Attempts}, result: {result}",
            new InvalidOperationException(message));
        ReportDiagnostic(
            transient ? ApiDiagnosticSeverity.Warning : ApiDiagnosticSeverity.Error,
            result + " " + message,
            endpoint: endpoint,
            replayFileName: entry.ReplayFile.FileName,
            replaySha256: entry.ReplayFile.FileSha256,
            httpStatusCode: statusCode is { } httpStatus ? (int)httpStatus : null,
            errorDetails: technicalDetails ?? message,
            retryable: transient);
        SetStatus(result);
    }

    private static ApiRequestException CreateRequestException(HttpResponseMessage response, string endpoint)
    {
        var status = response.StatusCode;
        var transient = status is HttpStatusCode.Accepted
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            || (int)status >= 500;
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (response.Headers.RetryAfter?.Date is { } retryDate)
        {
            var until = retryDate - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero)
            {
                retryAfter = until;
            }
        }

        return new ApiRequestException(
            $"API returned HTTP {(int)status}.",
            endpoint,
            status,
            transient,
            retryAfter);
    }

    private async Task<string?> TryAuthenticateAsync(ApiIntegrationConfig config, CancellationToken cancellationToken)
    {
        UpdateProgress("Authorizing", "Checking API authorization.");
        var needsRequest = _auth.Status.State != ApiAuthState.Authenticated;
        if (needsRequest)
        {
            ReportDiagnostic(ApiDiagnosticSeverity.Information, "Requesting authentication token.");
        }
        try
        {
            await _auth.GetTokenAsync(new Uri(config.BaseUrl), cancellationToken).ConfigureAwait(false);
            ReportDiagnostic(ApiDiagnosticSeverity.Success, needsRequest ? "Authentication completed." : "Using the existing authentication token.");
            return null;
        }
        catch (ApiAuthException ex)
        {
            ReportDiagnostic(ApiDiagnosticSeverity.Error, "Authentication failed.", errorDetails: ex.ToString(), retryable: !ex.NotAuthorized);
            return ex.Message;
        }
    }

    // Protected endpoints only. A 401 invalidates the token and is retried once; every operation sent
    // here is a GET or carries an idempotency key, so the single replay is safe.
    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        ApiIntegrationConfig config,
        string endpoint,
        object? body,
        QueueEntry? replay,
        CancellationToken cancellationToken)
    {
        var baseUri = new Uri(config.BaseUrl);
        for (var attempt = 0; ; attempt++)
        {
            string token;
            try
            {
                token = await _auth.GetTokenAsync(baseUri, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiAuthException ex)
            {
                throw new ApiRequestException(ex.Message, endpoint, null, !ex.NotAuthorized, null);
            }

            using var request = CreateRequest(method, new Uri(baseUri, endpoint), token);
            if (body != null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: Json);
            }
            var response = await SendRequestAsync(request, endpoint, replay, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
            {
                return response;
            }

            response.Dispose();
            _auth.Invalidate(token);
            ReportDiagnostic(ApiDiagnosticSeverity.Warning, "The API rejected the token (HTTP 401); requesting a new one.", method.Method, endpoint, httpStatusCode: 401, retryable: true);
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static TimeSpan Backoff(int attempts)
    {
        var seconds = Math.Min(30 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 10)), 6 * 60 * 60);
        return TimeSpan.FromSeconds(seconds);
    }

    private List<QueueEntry> LoadQueue()
    {
        if (!File.Exists(_queuePath))
        {
            return new List<QueueEntry>();
        }

        var queue = JsonSerializer.Deserialize<List<QueueEntry>>(File.ReadAllText(_queuePath), Json);
        return queue ?? throw new InvalidDataException("The API synchronization queue file is empty.");
    }

    private void SaveQueue()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_queuePath)!);
        var temporaryPath = _queuePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_queue, Json));
        File.Move(temporaryPath, _queuePath, overwrite: true);
    }

    private QueueEntry? FindEntry(string fileSha256, string idempotencyKey) =>
        _queue.FirstOrDefault(item =>
            item.ReplayFile.FileSha256 == fileSha256
            && item.Request.IdempotencyKey == idempotencyKey);

    private int CountByState(string state)
    {
        lock (_queueGate)
        {
            return _queue.Count(item => item.State == state);
        }
    }

    private ApiSyncSummary SetSummary(int synchronized, int alreadySynchronized, string message)
    {
        SetStatus(message);
        var state = State;
        return new ApiSyncSummary(synchronized, alreadySynchronized, state.Pending, state.Failed, message);
    }

    private void SetStatus(string status)
    {
        _status = status;
        PublishState();
    }

    private void PublishState()
    {
        var handler = StateChanged;
        if (handler == null)
        {
            return;
        }

        try
        {
            handler(State);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Publish API synchronization status", ex);
        }
    }

    private void SignalWorker()
    {
        if (_disposed || !_started)
        {
            return;
        }
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public void Dispose()
    {
        Task requestsIdle;
        lock (_requestGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            requestsIdle = _requestsIdle.Task;
        }

        _stop.Cancel();
        SignalForShutdown();
        if (_ownsHttpClient)
        {
            _ = Task.WhenAll(_worker ?? Task.CompletedTask, requestsIdle).ContinueWith(
                _ =>
                {
                    _httpClient.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void BeginExternalRequest()
    {
        lock (_requestGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeRequests++ == 0)
            {
                _requestsIdle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void EndExternalRequest()
    {
        lock (_requestGate)
        {
            if (--_activeRequests == 0)
            {
                _requestsIdle.TrySetResult(true);
            }
        }
    }

    private static TaskCompletionSource<bool> CompletedSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(true);
        return source;
    }

    private void SignalForShutdown()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class QueueEntry
    {
        public QueueEntry()
        {
        }

        public ReplayFileRegistration ReplayFile { get; set; } = null!;
        public ReplayIngestionRequest Request { get; set; } = null!;
        public string State { get; set; } = "pending";
        public int Attempts { get; set; }
        public DateTimeOffset? NextAttemptUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public string? LastError { get; set; }

        public QueueEntry Copy() => new()
        {
            ReplayFile = ReplayFile,
            Request = Request,
            State = State,
            Attempts = Attempts,
            NextAttemptUtc = NextAttemptUtc,
            CompletedAtUtc = CompletedAtUtc,
            LastError = LastError,
        };
    }

    private sealed class ApiRequestException(
        string message,
        string endpoint,
        HttpStatusCode? statusCode,
        bool isTransient,
        TimeSpan? retryAfter) : Exception(message)
    {
        public string Endpoint { get; } = endpoint;
        public HttpStatusCode? StatusCode { get; } = statusCode;
        public bool IsTransient { get; } = isTransient;
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}
