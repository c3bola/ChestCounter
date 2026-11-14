using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FortniteReplayReader.Models;
using Unreal.Core.Models.Enums;

namespace ChestCounter;

internal sealed record ReplayFileReadResult(string Details, string HexPreview);

internal static class ReplayDiagnosticFileService
{
    private const int PreviewByteLimit = 512;
    private const uint ReplayMagic = 0x1CA2E27F;

    public static FileInfo Load(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".replay", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Selecione um arquivo com extensão .replay.");
        }

        var file = new FileInfo(Path.GetFullPath(path));
        file.Refresh();
        if (!file.Exists)
        {
            throw new FileNotFoundException("O arquivo replay não existe.", file.FullName);
        }

        using (new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
        }

        file.Refresh();
        return file;
    }

    public static ReplayFileReadResult Read(FileInfo selectedFile)
    {
        var file = Load(selectedFile.FullName);
        byte[] preview;
        using (var stream = new FileStream(
                   file.FullName,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete,
                   PreviewByteLimit,
                   FileOptions.SequentialScan))
        {
            preview = new byte[(int)Math.Min(stream.Length, PreviewByteLimit)];
            stream.ReadExactly(preview);
        }

        var liveState = ReplayHeaderProbe.IsLive(file.FullName) switch
        {
            true => "sim",
            false => "não",
            null => "desconhecido (cabeçalho inválido ou incompleto)",
        };
        var keyMissing = ReplayHeaderProbe.KeyMissing(file.FullName);
        var magic = preview.Length >= sizeof(uint)
            ? BinaryPrimitives.ReadUInt32LittleEndian(preview)
            : (uint?)null;

        var details = new StringBuilder()
            .AppendLine($"Arquivo: {file.Name}")
            .AppendLine($"Caminho: {file.FullName}")
            .AppendLine($"Tamanho: {file.Length:N0} bytes")
            .AppendLine($"Criado (UTC): {file.CreationTimeUtc:O}")
            .AppendLine($"Modificado (UTC): {file.LastWriteTimeUtc:O}")
            .AppendLine($"Bytes exibidos: {preview.Length:N0} de {file.Length:N0}")
            .AppendLine($"Assinatura: {FormatSignature(preview)}")
            .AppendLine($"Cabeçalho Fortnite reconhecido: {magic == ReplayMagic}")
            .AppendLine($"Replay marcado como ao vivo: {liveState}")
            .AppendLine($"ReplayHeaderProbe.KeyMissing: {keyMissing}")
            .AppendLine($"Assinatura de compactação comum: {DetectCompression(preview)}")
            .ToString();

        return new ReplayFileReadResult(details, FormatHex(preview));
    }

    private static string FormatSignature(byte[] bytes) =>
        bytes.Length == 0 ? "(arquivo vazio)" : Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 16)));

    private static string DetectCompression(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            return "GZIP";
        }
        if (bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4B)
        {
            return "ZIP";
        }
        if (bytes.Length >= 2 && bytes[0] == 0x78)
        {
            return "possível cabeçalho zlib";
        }
        return "nenhuma assinatura GZIP/ZIP/zlib reconhecida";
    }

    private static string FormatHex(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return "(arquivo vazio)";
        }

        var text = new StringBuilder();
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var count = Math.Min(16, bytes.Length - offset);
            var hex = string.Join(" ", bytes.AsSpan(offset, count).ToArray().Select(value => value.ToString("X2")));
            var ascii = new string(bytes
                .AsSpan(offset, count)
                .ToArray()
                .Select(value => value is >= 32 and <= 126 ? (char)value : '.')
                .ToArray());
            text.Append(offset.ToString("X8"))
                .Append("  ")
                .Append(hex.PadRight(47))
                .Append("  |")
                .Append(ascii)
                .AppendLine("|");
        }

        text.AppendLine()
            .Append($"Visualização limitada aos primeiros {bytes.Length:N0} bytes; o arquivo original não foi alterado.");
        return text.ToString();
    }
}

internal static class ReplayDiagnosticTranscoder
{
    public static Task<MatchRecord> TranscodeAsync(FileInfo selectedFile) =>
        Task.Run(() => Transcode(selectedFile));

    private static MatchRecord Transcode(FileInfo selectedFile)
    {
        var file = ReplayDiagnosticFileService.Load(selectedFile.FullName);
        file.Refresh();
        var fileSize = file.Length;
        var fileCreatedAtUtc = file.CreationTimeUtc;
        byte[] bytes;
        using (var stream = new FileStream(
                   file.FullName,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        var fileSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var replayStream = new MemoryStream(bytes, writable: false);
        var match = new ReplayAnalyzer().Analyze(replayStream, file.Name, "");

        match.ReplayFile = file.Name;
        match.ReplayFileSha256 = fileSha256;
        match.ReplayFileSizeBytes = fileSize;
        match.ReplayFileCreatedAtUtc = fileCreatedAtUtc;
        match.AnalysisApplicationVersion = AppInfo.VersionText;
        match.ParserVersion = typeof(FortniteReplayReader.ReplayReader).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        return match;
    }
}

internal sealed record ReplayDiagnosticParsingResult(
    ParseMode Mode,
    string FileName,
    DateTimeOffset ExecutedAt,
    TimeSpan Duration,
    string Json,
    int JsonSizeBytes,
    int EventCount);

internal enum ReplayDiagnosticParsingStage
{
    FileAccess,
    ParserInitialization,
    ReadDecodeParseAndBuild,
    JsonSerialization,
    ResultSummary,
}

internal sealed class ReplayDiagnosticParsingException(
    ParseMode mode,
    ReplayDiagnosticParsingStage stage,
    Exception innerException)
    : Exception($"Replay parsing mode {mode}; stage: {stage}.", innerException)
{
    public ReplayDiagnosticParsingStage Stage { get; } = stage;
}

internal static class ReplayDiagnosticParsingTranscoder
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static Task<ReplayDiagnosticParsingResult> TranscodeAsync(FileInfo selectedFile, ParseMode mode) =>
        Task.Run(() => Transcode(selectedFile, mode));

    private static ReplayDiagnosticParsingResult Transcode(FileInfo selectedFile, ParseMode mode)
    {
        var stage = ReplayDiagnosticParsingStage.FileAccess;
        try
        {
            var file = ReplayDiagnosticFileService.Load(selectedFile.FullName);
            var executedAt = DateTimeOffset.Now;
            var stopwatch = Stopwatch.StartNew();
            FortniteReplay replay;
            using (var stream = new FileStream(
                       file.FullName,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       81920,
                       FileOptions.SequentialScan))
            {
                stage = ReplayDiagnosticParsingStage.ParserInitialization;
                if (mode is not (ParseMode.EventsOnly or ParseMode.Minimal or ParseMode.Normal or ParseMode.Full))
                {
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported replay diagnostic parsing mode.");
                }
                var reader = new FortniteReplayReader.ReplayReader(parseMode: mode);

                stage = ReplayDiagnosticParsingStage.ReadDecodeParseAndBuild;
                replay = reader.ReadReplay(stream);
            }

            stage = ReplayDiagnosticParsingStage.JsonSerialization;
            var json = SerializeReplay(replay);
            stage = ReplayDiagnosticParsingStage.ResultSummary;
            stopwatch.Stop();
            var eventCount = replay.Eliminations.Count
                + (replay.Stats == null ? 0 : 1)
                + (replay.TeamStats == null ? 0 : 1);

            return new ReplayDiagnosticParsingResult(
                mode,
                file.Name,
                executedAt,
                stopwatch.Elapsed,
                json,
                Encoding.UTF8.GetByteCount(json),
                eventCount);
        }
        catch (Exception ex)
        {
            throw new ReplayDiagnosticParsingException(mode, stage, ex);
        }
    }

    internal static string SerializeReplay(FortniteReplay replay) => JsonSerializer.Serialize(replay, Json);
}

internal sealed record ReplayDiagnosticPreparedData(
    ReplayIngestionSnapshot Snapshot,
    string DisplayJson,
    string RequestJson);

internal static class ReplayDiagnosticPreSendService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static ReplayDiagnosticPreparedData Prepare(MatchRecord match, FileInfo replayFile)
    {
        var hash = match.ReplayFileSha256
            ?? throw new InvalidDataException("A transcodificação não produziu o SHA-256 do arquivo.");
        var parserVersion = typeof(FortniteReplayReader.ReplayReader).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var snapshot = ReplayIngestionMapper.Create(
            match,
            replayFile,
            hash,
            AppInfo.VersionText,
            parserVersion);

        var registrationJson = JsonSerializer.Serialize(snapshot.ReplayFile, Json);
        var requestJson = JsonSerializer.Serialize(snapshot.Request, Json);
        var displayJson = new StringBuilder()
            .AppendLine("POST replay-files — cadastro do arquivo")
            .AppendLine(registrationJson)
            .AppendLine()
            .AppendLine($"POST replay-files/{snapshot.ReplayFile.FileSha256}/processing-attempts — dados transcodificados")
            .Append(requestJson)
            .ToString();

        return new ReplayDiagnosticPreparedData(snapshot, displayJson, requestJson);
    }
}

internal sealed record ReplayDiagnosticSendResult(
    ApiSyncSummary Summary,
    IReadOnlyList<ApiOperationLogEntry> Diagnostics,
    IReadOnlyList<string> TransportExceptions,
    IReadOnlyList<ReplayDiagnosticHttpRequest> HttpRequests,
    IReadOnlyList<ReplayDiagnosticHttpResponse> HttpResponses,
    string? CleanupWarning,
    string PayloadComparison);

internal sealed record ReplayDiagnosticHttpRequest(string Method, string Url, string? Body);

internal sealed record ReplayDiagnosticHttpResponse(
    string Method,
    string Url,
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers);

internal sealed class ReplayDiagnosticApiSender
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<ReplayDiagnosticSendResult> SendAsync(
        RuntimeContext runtime,
        ReplayIngestionSnapshot snapshot,
        MatchRecord match,
        FileInfo replayFile,
        Action<ApiOperationLogEntry> diagnosticHandler,
        Action<ApiOperationProgress> progressHandler,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(
                match.ReplayFileSha256,
                snapshot.ReplayFile.FileSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("O hash do replay transcodificado não corresponde ao JSON preparado.");
        }

        await using (var stream = new FileStream(
                         replayFile.FullName,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var currentHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(currentHash, snapshot.ReplayFile.FileSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("O arquivo foi alterado depois da transcodificação; prepare o pré-envio novamente.");
            }
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ChestCounter",
            "ReplayDiagnostic",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var diagnostics = new ConcurrentQueue<ApiOperationLogEntry>();
        var transportExceptions = new ConcurrentQueue<string>();
        var httpRequests = new ConcurrentQueue<ReplayDiagnosticHttpRequest>();
        var httpResponses = new ConcurrentQueue<ReplayDiagnosticHttpResponse>();
        using var httpClient = new HttpClient(
            new ExceptionCapturingHandler(transportExceptions, httpRequests, httpResponses))
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        var service = new ApiSyncService(
            new ApiIntegrationConfigStore(runtime.ApiSync.ConfigurationPath),
            Path.Combine(tempDirectory, "api-sync-queue.json"),
            httpClient);
        Action<ApiOperationLogEntry> captureDiagnostic = entry =>
        {
            diagnostics.Enqueue(entry);
            diagnosticHandler(entry);
        };
        service.DiagnosticReported += captureDiagnostic;
        service.ProgressChanged += progressHandler;

        ApiSyncSummary? summary = null;
        string? cleanupWarning = null;
        var payloadComparison = "Não foi possível confirmar os corpos HTTP enviados.";
        try
        {
            await service.QueueReplayAsync(match, replayFile, cancellationToken);
            if (service.State.Pending == 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(service.State.Status)
                        ? "O replay não foi adicionado à fila temporária de diagnóstico."
                        : service.State.Status);
            }

            summary = await service.SyncNowAsync(cancellationToken);
            payloadComparison = ComparePayloads(snapshot, httpRequests.ToArray());
        }
        finally
        {
            service.DiagnosticReported -= captureDiagnostic;
            service.ProgressChanged -= progressHandler;
            service.Dispose();

            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cleanupWarning = ex.ToString();
                ErrorLog.Write("Remove temporary replay diagnostic queue", ex);
            }
        }

        return new ReplayDiagnosticSendResult(
            summary ?? throw new InvalidOperationException("A sincronização temporária não retornou um resultado."),
            diagnostics.ToArray(),
            transportExceptions.ToArray(),
            httpRequests.ToArray(),
            httpResponses.ToArray(),
            cleanupWarning,
            payloadComparison);
    }

    private static string ComparePayloads(
        ReplayIngestionSnapshot snapshot,
        IReadOnlyList<ReplayDiagnosticHttpRequest> requests)
    {
        var fileRegistrationBody = requests
            .LastOrDefault(request => Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                && uri.AbsolutePath.EndsWith("/replay-files", StringComparison.Ordinal)
                && request.Body != null)
            ?.Body;
        var ingestionBody = requests
            .LastOrDefault(request => Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                && uri.AbsolutePath.EndsWith("/processing-attempts", StringComparison.Ordinal)
                && request.Body != null)
            ?.Body;
        var expectedRegistrationBody = JsonSerializer.Serialize(snapshot.ReplayFile, RequestJsonOptions);
        var expectedIngestionBody = JsonSerializer.Serialize(snapshot.Request, RequestJsonOptions);

        if (fileRegistrationBody == null || ingestionBody == null)
        {
            return "Não foi possível comparar os dois corpos HTTP; consulte o status e os logs da operação.";
        }
        if (fileRegistrationBody != expectedRegistrationBody || ingestionBody != expectedIngestionBody)
        {
            return "ATENÇÃO: o corpo capturado pelo cliente HTTP difere do JSON preparado.";
        }

        return "Os corpos capturados pelo cliente HTTP correspondem exatamente aos JSONs preparados.";
    }

    private sealed class ExceptionCapturingHandler(
        ConcurrentQueue<string> exceptions,
        ConcurrentQueue<ReplayDiagnosticHttpRequest> requests,
        ConcurrentQueue<ReplayDiagnosticHttpResponse> responses)
        : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                var requestBody = request.Content == null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                requests.Enqueue(new ReplayDiagnosticHttpRequest(
                    request.Method.Method,
                    request.RequestUri?.ToString() ?? "(URL indisponível)",
                    requestBody));
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var headers = response.Headers
                    .Concat(response.Content.Headers)
                    .Where(header => IsRelevantResponseHeader(header.Key))
                    .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => string.Join(", ", group.SelectMany(header => header.Value)),
                        StringComparer.OrdinalIgnoreCase);
                responses.Enqueue(new ReplayDiagnosticHttpResponse(
                    request.Method.Method,
                    request.RequestUri?.ToString() ?? "(URL indisponível)",
                    (int)response.StatusCode,
                    headers));
                return response;
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex.ToString());
                throw;
            }
        }

        private static bool IsRelevantResponseHeader(string name) =>
            name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Date", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Request-Id", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-Request-Id", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Traceparent", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Server", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("RateLimit-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("X-RateLimit-", StringComparison.OrdinalIgnoreCase);
    }
}
