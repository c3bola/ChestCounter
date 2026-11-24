using System.Threading;
using System.ComponentModel;
using System.Security;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChestCounter;

internal sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#F5F7FA"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#C4CAD1"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#28E66B"));
    private static readonly IBrush Cyan = new SolidColorBrush(Color.Parse("#00C8F5"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#FFB84D"));
    private static readonly IBrush Rose = new SolidColorBrush(Color.Parse("#FF6B7A"));
    private static readonly TimeSpan RemoteIdentityRefreshInterval = TimeSpan.FromDays(1);

    private readonly RuntimeContext _runtime;
    private readonly Action<string> _openExternalLink;
    private readonly DispatcherTimer _completionTimer;
    private readonly DispatcherTimer _remoteProfileRetryTimer;
    private readonly Dictionary<string, DateTimeOffset> _remoteProfileRetryAfter = new(StringComparer.OrdinalIgnoreCase);
    private MonitorState? _state;
    private ApiSyncState _apiState;
    private ApiOperationProgress? _apiProgress;
    private ApiAuthStatus _apiAuthStatus;
    private MonitorState? _pendingState;
    private int _uiRefreshQueued;
    private int _lastCompletedLocalBatch;
    private DateTimeOffset? _lastCompletedApiOperation;
    private bool _showCompletionStatus;
    private bool _isProcessingStatusExpanded;
    private bool _disposed;
    private ProfileOption? _selectedProfile;
    private bool _syncingProfile;
    private string? _remoteProfileIdentityKey;
    private long? _remoteProfilePlayerId;
    private bool _remoteProfileResolving;
    private CancellationTokenSource? _remoteProfileCancellation;
    private Task? _remoteProfileResolutionTask;
    private bool _startupProfileOpenPending;

    public DashboardViewModel(RuntimeContext runtime, Action<string>? openExternalLink = null)
    {
        _runtime = runtime;
        _openExternalLink = openExternalLink ?? ExternalLinkLauncher.Open;
        _apiState = runtime.ApiSync.State;
        _apiProgress = runtime.ApiSync.Progress;
        _apiAuthStatus = runtime.ApiSync.AuthStatus;
        _startupProfileOpenPending = runtime.Monitor.Config.OpenReportOnStart && !runtime.Monitor.AutoMode;
        _completionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _completionTimer.Tick += OnCompletionTimerTick;
        _remoteProfileRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _remoteProfileRetryTimer.Tick += OnRemoteProfileRetryTimerTick;
        runtime.Monitor.Changed += OnMonitorChanged;
        runtime.ApiSync.StateChanged += OnApiStateChanged;
        runtime.ApiSync.ProgressChanged += OnApiProgressChanged;
        runtime.ApiSync.AuthStatusChanged += OnApiAuthStatusChanged;
    }

    public event Action? SettingsRequested;

    public string WindowTitle => L.F("main.window_title", AppInfo.VersionText);
    public string BannerTitle => L.T("main.chest_counter_contador_de_cofres");
    public string TotalCaption => L.T("main.chests_in_total");
    public string TodayCaption => L.T("main.today");
    public string BestDayCaption => L.T("main.best_day");
    public string AverageCaption => L.T("main.average_per_match");
    public string BestMatchCaption => L.T("main.best_match");
    public string HistoryCaption => L.T("main.history");
    public string TreasuresCaption => L.T("main.treasures");
    public string LastMatchCaption => L.T("main.last_match_ultima_partida");
    public string HistoryTitle => L.T("main.last_7_days");
    public string TeamTitle => L.T("main.team");
    public string MinimizeCaption => L.T("main.minimize_window");
    public string MaximizeCaption => L.T("main.maximize_restore_window");
    public string CloseCaption => L.T("settings.close");
    public string ProfileCaption => L.T("main.profile") + ":";
    public string OverlayButtonText => L.T("main.overlay");
    public string ReportButtonText => L.T("main.open_online_report");
    public string SettingsButtonText => L.T("main.settings");
    public string RemoteProfileStatus => L.T("main.remote_profile_resolving");
    public bool ShowRemoteProfileStatus => _remoteProfileResolving;
    public string KeepOpenText => L.T("main.keep_open");
    public string InstallText => L.F("main.install_version_you_have", State?.Update?.Version.ToString(3) ?? "", AppInfo.VersionText);

    public MonitorState? State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(HasNotice));
                OnPropertyChanged(nameof(ShowPirateToken));
                OnPropertyChanged(nameof(PirateTokenMessage));
                OnPropertyChanged(nameof(ShowUpdate));
                OnPropertyChanged(nameof(ShowKeepOpen));
                OnPropertyChanged(nameof(HeroTotal));
                OnPropertyChanged(nameof(HeroCurrent));
                OnPropertyChanged(nameof(HeroCurrentCaption));
                OnPropertyChanged(nameof(SessionLine));
                OnPropertyChanged(nameof(LastMatchValue));
                OnPropertyChanged(nameof(LastMatchDetails));
                OnPropertyChanged(nameof(TeamSubtitle));
                OnPropertyChanged(nameof(TeamSummary));
                OnPropertyChanged(nameof(TeamMessage));
                OnPropertyChanged(nameof(Notice));
                OnPropertyChanged(nameof(OverlayStatus));
                OnPropertyChanged(nameof(OverlayIndicator));
                OnPropertyChanged(nameof(InstallText));
                RefreshProcessingProgress();
                RefreshMetrics();
                RefreshDays();
                RefreshProfiles();
                RefreshTeam();
                UpdateLastMatch();
                _ = ResolveRemoteProfileAsync();
            }
        }
    }

    public string HeroTotal => State?.Overlay.Total ?? "0";
    public string HeroCurrent => State?.Overlay.Current ?? "—";
    public string HeroCurrentCaption => State?.Overlay.CurrentLive == true
        ? State.Overlay.CurrentCaption
        : L.T("main.last_match");
    public string SessionLine => State?.Overlay.Rows.FirstOrDefault() is { } session
        ? L.F("main.session_line", $"{session.Value} {L.T(session.Value == L.N(1) ? "common.chest" : "common.chests")}")
        : L.F("main.session_line", "—");
    public bool HasNotice => !string.IsNullOrEmpty(State?.Notice);
    public bool ShowPirateToken => State?.PirateTokenLive == true || State?.Stats.LastMatch?.PirateToken == true;
    public string PirateTokenMessage => State?.PirateTokenLive == true
        ? L.T("main.pirate_token_current")
        : L.T("main.pirate_token_last");
    public bool ShowUpdate => State?.Update != null;
    public bool ShowKeepOpen => State?.AutoCloseOffer == true;
    public string Notice => State?.Notice ?? "";
    public string OverlayStatus => _runtime.Monitor.OverlayVisible ? L.T("settings.on") : L.T("settings.off");
    public IBrush OverlayIndicator => _runtime.Monitor.OverlayVisible ? Green : Muted;
    public string LastMatchValue { get; private set; } = "—";
    public string LastMatchDetails { get; private set; } = "";
    public string TeamSubtitle { get; private set; } = L.T("main.team");
    public string TeamSummary { get; private set; } = "";
    public bool HasTeamSummary => TeamSummary.Length > 0;
    public string TeamMessage { get; private set; } = "—";
    public bool HasTeamMessage => TeamMessage.Length > 0;
    public IReadOnlyList<MetricItem> Metrics { get; private set; } = Array.Empty<MetricItem>();
    public IReadOnlyList<DayItem> Days { get; private set; } = Array.Empty<DayItem>();
    public IReadOnlyList<TeamMemberItem> TeamMembers { get; private set; } = Array.Empty<TeamMemberItem>();
    public IReadOnlyList<ProfileOption> Profiles { get; private set; } = Array.Empty<ProfileOption>();

    public bool ShowProcessingStatus => HasProcessingActivity || _showCompletionStatus;
    public bool IsProcessingStatusExpanded
    {
        get => _isProcessingStatusExpanded;
        set
        {
            if (SetProperty(ref _isProcessingStatusExpanded, value))
            {
                OnPropertyChanged(nameof(ProcessingDetailsMaxHeight));
                OnPropertyChanged(nameof(ProcessingDetailsOpacity));
                OnPropertyChanged(nameof(ProcessingStatusChevron));
                OnPropertyChanged(nameof(ProcessingStatusToggleLabel));
            }
        }
    }
    public double ProcessingDetailsMaxHeight => IsProcessingStatusExpanded ? 420 : 0;
    public double ProcessingDetailsOpacity => IsProcessingStatusExpanded ? 1 : 0;
    public Geometry ProcessingStatusChevron => IsProcessingStatusExpanded ? Glyphs.ChevronUp : Glyphs.ChevronDown;
    public string ProcessingStatusToggleLabel => L.T(IsProcessingStatusExpanded ? "progress.collapse" : "progress.expand");

    public string CompactStatusText
    {
        get
        {
            if (State != null)
            {
                if (LocalProgress.Phase == LocalReplayPhase.Failed || LocalProgress.Failed > 0)
                {
                    return LocalProgressStatus;
                }
                if (LocalProgress.Phase is LocalReplayPhase.Identifying or LocalReplayPhase.Processing or LocalReplayPhase.Waiting)
                {
                    return LocalProgressStatus;
                }
            }
            if (_apiAuthStatus.State == ApiAuthState.Failed && HasApiWork)
            {
                return L.T("progress.compact_api_unavailable");
            }
            if (_apiState.IsSyncing)
            {
                return L.T("progress.compact_api_syncing");
            }
            if (ApiFailureCount > 0)
            {
                return L.T("progress.api_failed");
            }
            if (_apiState.Pending > 0)
            {
                return L.T(State?.LocalProgress.Phase == LocalReplayPhase.Completed
                    ? "progress.local_complete"
                    : "progress.api_waiting");
            }
            if (_showCompletionStatus)
            {
                return L.T("progress.compact_updated");
            }
            return "";
        }
    }
    public string CompactProgressText
    {
        get
        {
            if (IsCompactShowingApiProgress)
            {
                return ApiProgressCountText;
            }
            if (State != null && LocalProgress.Total > 0)
            {
                var text = L.F("progress.compact_local_count", L.N(LocalProgress.Processed), L.N(LocalProgress.Total));
                return LocalProgress.Waiting > 0
                    ? text + " · " + L.F("progress.local_waiting_count", L.N(LocalProgress.Waiting))
                    : text;
            }
            if (State != null && LocalProgress.Waiting > 0)
            {
                return L.F("progress.local_waiting_count", L.N(LocalProgress.Waiting));
            }
            return _apiState.Pending > 0 ? ApiProgressCountText : "";
        }
    }
    public bool ShowCompactProgressText => CompactProgressText.Length > 0;
    public string CompactProgressPercentText
    {
        get
        {
            if (IsCompactShowingApiProgress && _apiProgress?.Total is > 0)
            {
                return FormatPercent(Math.Clamp(_apiProgress.Processed * 100.0 / _apiProgress.Total.Value, 0, 100));
            }
            return State != null && LocalProgress.Total > 0 ? FormatPercent(LocalProgress.Percent) : "";
        }
    }
    public bool ShowCompactProgressPercent => CompactProgressPercentText.Length > 0;
    public double CompactProgressValue => IsCompactShowingApiProgress && _apiProgress?.Total is > 0
        ? Math.Clamp(_apiProgress.Processed * 100.0 / _apiProgress.Total.Value, 0, 100)
        : LocalProgress.Percent;
    public bool ShowCompactProgressBar => IsCompactShowingApiProgress
        || State != null && (LocalProgress.Total > 0
            || LocalProgress.Phase == LocalReplayPhase.Identifying
            || LocalProgress.Phase == LocalReplayPhase.Waiting && LocalProgress.Total == 0);
    public bool CompactProgressIsIndeterminate => IsCompactShowingApiProgress
        ? _apiProgress?.Total is not > 0
        : State != null && (LocalProgress.Phase == LocalReplayPhase.Identifying
            || LocalProgress.Phase == LocalReplayPhase.Waiting && LocalProgress.Total == 0);
    public IBrush CompactProgressAccent => IsCompactShowingApiProgress ? ApiProgressAccent : LocalProgressAccent;
    public string CompactSyncText
    {
        get
        {
            if (_apiAuthStatus.State == ApiAuthState.Failed && HasApiWork)
            {
                return L.T("progress.compact_sync_unavailable");
            }
            if (ApiFailureCount > 0)
            {
                return L.F("progress.compact_sync_failed", L.N(ApiFailureCount));
            }
            if (IsLocalPriorityActive)
            {
                return L.T(_apiState.IsSyncing
                    ? "progress.compact_sync_finishing"
                    : "progress.compact_sync_wait_local");
            }
            if (_apiState.IsSyncing)
            {
                return L.T("progress.compact_sync_sending");
            }
            if (_apiState.Pending > 0)
            {
                return L.F("progress.compact_sync_pending", L.N(_apiState.Pending));
            }
            return HasCompletedApiProgress
                ? L.T("progress.compact_sync_done")
                : L.T("progress.compact_sync_none");
        }
    }
    public IBrush CompactSyncAccent => _apiAuthStatus.State == ApiAuthState.Failed || ApiFailureCount > 0
        ? Amber
        : IsLocalPriorityActive || _apiState.Pending > 0
            ? Muted
            : _apiState.IsSyncing ? Cyan
            : HasCompletedApiProgress ? Green : Muted;

    public string LocalProgressTitle => L.T("progress.local_title");
    public string LocalProgressStatus => LocalProgress.Phase switch
    {
        LocalReplayPhase.Identifying => L.T("progress.local_identifying"),
        LocalReplayPhase.Processing => L.T("progress.local_processing"),
        LocalReplayPhase.Waiting => L.T("progress.local_waiting"),
        LocalReplayPhase.Failed => L.T("progress.local_failed"),
        LocalReplayPhase.Completed when LocalProgress.Failed > 0 => L.T("progress.local_complete_with_errors"),
        _ => L.T("progress.local_complete"),
    };
    public string LocalBatchText => LocalProgress.BatchNumber > 0
        ? L.F("progress.local_batch", L.N(LocalProgress.BatchNumber))
        : "";
    public bool ShowLocalBatch => LocalProgress.BatchNumber > 0
        && (LocalProgress.Phase is LocalReplayPhase.Processing or LocalReplayPhase.Completed or LocalReplayPhase.Failed
            || LocalProgress.Phase == LocalReplayPhase.Waiting && LocalProgress.Total > 0);
    public string LocalProgressPercentText => LocalProgress.Total > 0
        ? FormatPercent(LocalProgress.Percent)
        : "";
    public double LocalProgressValue => LocalProgress.Percent;
    public bool LocalProgressIsIndeterminate => LocalProgress.Phase == LocalReplayPhase.Identifying
        || LocalProgress.Phase == LocalReplayPhase.Waiting && LocalProgress.Total == 0;
    public bool ShowLocalProgressBar => LocalProgress.Phase == LocalReplayPhase.Failed
        ? LocalProgress.Total > 0
        : LocalProgress.Phase != LocalReplayPhase.Completed || LocalProgress.Total > 0 || LocalProgress.Waiting > 0;
    public string LocalProgressCountText => LocalProgress.Total > 0
        ? L.F("progress.local_count", L.N(LocalProgress.Processed), L.N(LocalProgress.Total), L.N(LocalProgress.Remaining))
        : LocalProgress.Waiting > 0
            ? L.F("progress.local_waiting_count", L.N(LocalProgress.Waiting))
            : L.T("progress.local_empty");
    public bool ShowLocalProgressCount => LocalProgress.Phase != LocalReplayPhase.Failed
        || LocalProgress.Total > 0
        || LocalProgress.Waiting > 0;
    public string LocalProgressDetailText
    {
        get
        {
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(LocalProgress.CurrentReplay))
            {
                details.Add(L.F("progress.current_replay", Path.GetFileName(LocalProgress.CurrentReplay)));
            }
            if (LocalProgress.Total > 0 && LocalProgress.Phase is LocalReplayPhase.Completed or LocalReplayPhase.Waiting)
            {
                details.Add(L.F(
                    "progress.local_summary",
                    L.N(LocalProgress.Processed),
                    L.N(LocalProgress.UpdatedRecords),
                    L.N(LocalProgress.Skipped),
                    L.N(LocalProgress.Failed)));
            }
            if ((LocalProgress.Phase is LocalReplayPhase.Processing or LocalReplayPhase.Waiting)
                && LocalProgress.Waiting > 0 && LocalProgress.Total > 0)
            {
                details.Add(L.F("progress.local_waiting_count", L.N(LocalProgress.Waiting)));
            }
            return string.Join(" · ", details);
        }
    }
    public bool ShowLocalProgressDetail => LocalProgressDetailText.Length > 0;
    public IBrush LocalProgressAccent => LocalProgress.Phase == LocalReplayPhase.Failed || LocalProgress.Failed > 0
        ? Rose
        : LocalProgress.Phase == LocalReplayPhase.Completed
            ? Green
            : LocalProgress.Phase == LocalReplayPhase.Waiting ? Amber : Cyan;

    public string ApiProgressTitle => L.T("progress.api_title");
    public string ApiProgressStatus
    {
        get
        {
            if (IsLocalPriorityActive)
            {
                return L.T(_apiState.IsSyncing ? "progress.api_wait_local_inflight" : "progress.api_wait_local");
            }
            if (_apiState.IsSyncing)
            {
                return L.T("progress.api_sending");
            }
            if (ApiFailureCount > 0 || _apiAuthStatus.State == ApiAuthState.Failed)
            {
                return L.T("progress.api_failed");
            }
            if (_apiState.Pending > 0)
            {
                return L.T("progress.api_waiting");
            }
            return HasCompletedApiProgress
                ? L.T("progress.api_complete")
                : L.T("progress.api_none");
        }
    }
    public string ApiProgressCountText
    {
        get
        {
            if ((HasCurrentApiProgress || _apiState.IsSyncing) && _apiProgress?.Total is int total)
            {
                return L.F("progress.api_count", L.N(_apiProgress.Succeeded), L.N(total));
            }
            if (_apiState.Pending > 0)
            {
                return L.F("progress.api_pending", L.N(_apiState.Pending));
            }
            if (ApiFailureCount > 0)
            {
                return L.F("progress.api_failures", L.N(ApiFailureCount));
            }
            return L.T("progress.api_none");
        }
    }
    public string ApiProgressDetailText
    {
        get
        {
            if (_apiAuthStatus.State == ApiAuthState.Failed)
            {
                return L.T("progress.api_unavailable");
            }
            var details = new List<string>();
            if (ApiFailureCount > 0 && (HasCurrentApiProgress || _apiState.Pending > 0))
            {
                details.Add(L.F("progress.api_failures", L.N(ApiFailureCount)));
            }
            if (_apiState.IsSyncing && !string.IsNullOrWhiteSpace(_apiProgress?.CurrentReplay))
            {
                details.Add(L.F("progress.api_current", Path.GetFileName(_apiProgress.CurrentReplay)));
            }
            return string.Join(" · ", details);
        }
    }
    public bool ShowApiProgressDetail => ApiProgressDetailText.Length > 0;
    public bool ShowApiProgressBar => _apiState.IsSyncing || HasCurrentApiProgress;
    public bool ApiProgressIsIndeterminate => _apiState.IsSyncing && !HasCurrentApiProgress;
    public double ApiProgressValue => HasCurrentApiProgress && _apiProgress?.Total is int total
        ? Math.Clamp(_apiProgress.Processed * 100.0 / total, 0, 100)
        : 0;
    public string ApiProgressPercentText => HasCurrentApiProgress && _apiProgress?.Total is int total
        ? FormatPercent(Math.Clamp(_apiProgress.Processed * 100.0 / total, 0, 100))
        : "";
    public IBrush ApiProgressAccent => ApiFailureCount > 0 || _apiAuthStatus.State == ApiAuthState.Failed
        ? Rose
        : HasCompletedApiProgress ? Green : Cyan;

    private LocalReplayProgress LocalProgress => State?.LocalProgress ?? LocalReplayProgress.Initial;
    private bool IsLocalPriorityActive => State?.LocalPriorityActive ?? true;
    private int ApiFailureCount => Math.Max(_apiState.Failed, _apiProgress?.Failed ?? 0);
    private bool HasCurrentApiProgress => _apiProgress?.Total is > 0
        && (_apiState.IsSyncing && !IsLocalPriorityActive
            || !_apiState.IsSyncing && !IsLocalPriorityActive
                && _apiAuthStatus.State != ApiAuthState.Failed
                && _apiState.Pending == 0 && _apiProgress.Processed >= _apiProgress.Total);
    private bool HasCompletedApiProgress => _apiProgress?.Total is > 0
        && !_apiState.IsSyncing
        && !IsLocalPriorityActive
        && _apiAuthStatus.State != ApiAuthState.Failed
        && _apiState.Pending == 0
        && _apiProgress.Processed >= _apiProgress.Total;
    private bool HasLocalProcessingActivity => State != null
        && (LocalProgress.Phase is LocalReplayPhase.Identifying
            or LocalReplayPhase.Processing
            or LocalReplayPhase.Waiting
            or LocalReplayPhase.Failed
            || LocalProgress.Failed > 0);
    private bool HasProcessingActivity => HasLocalProcessingActivity
        || _apiState.IsSyncing
        || _apiState.Pending > 0
        || ApiFailureCount > 0;
    private bool HasApiWork => _apiState.Pending > 0 || ApiFailureCount > 0 || _apiState.IsSyncing;
    private bool IsCompactShowingApiProgress => !HasLocalProcessingActivity
        && (_apiState.IsSyncing || HasCompletedApiProgress);

    public ProfileOption? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value) && !_syncingProfile && value != null)
            {
                _runtime.Monitor.SetProfile(value.Key);
            }
        }
    }

    private void OnMonitorChanged(MonitorState state)
    {
        Interlocked.Exchange(ref _pendingState, state);
        QueueUiRefresh();
    }

    private void OnApiStateChanged(ApiSyncState state) => QueueUiRefresh();
    private void OnApiProgressChanged(ApiOperationProgress progress) => QueueUiRefresh();
    private void OnApiAuthStatusChanged(ApiAuthStatus status) => QueueUiRefresh();

    private void QueueUiRefresh()
    {
        if (Interlocked.Exchange(ref _uiRefreshQueued, 1) != 0)
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _uiRefreshQueued, 0);
            if (_disposed)
            {
                return;
            }
            var state = Interlocked.Exchange(ref _pendingState, null);
            _apiState = _runtime.ApiSync.State;
            _apiProgress = _runtime.ApiSync.Progress;
            _apiAuthStatus = _runtime.ApiSync.AuthStatus;
            if (state != null)
            {
                State = state;
            }
            else
            {
                RefreshProcessingProgress();
            }
        });
    }

    private void RefreshProcessingProgress()
    {
        UpdateCompletionVisibility();
        OnPropertyChanged(nameof(ShowProcessingStatus));
        OnPropertyChanged(nameof(CompactStatusText));
        OnPropertyChanged(nameof(CompactProgressText));
        OnPropertyChanged(nameof(ShowCompactProgressText));
        OnPropertyChanged(nameof(CompactProgressPercentText));
        OnPropertyChanged(nameof(ShowCompactProgressPercent));
        OnPropertyChanged(nameof(CompactProgressValue));
        OnPropertyChanged(nameof(ShowCompactProgressBar));
        OnPropertyChanged(nameof(CompactProgressIsIndeterminate));
        OnPropertyChanged(nameof(CompactProgressAccent));
        OnPropertyChanged(nameof(CompactSyncText));
        OnPropertyChanged(nameof(CompactSyncAccent));
        OnPropertyChanged(nameof(LocalProgressTitle));
        OnPropertyChanged(nameof(LocalProgressStatus));
        OnPropertyChanged(nameof(LocalBatchText));
        OnPropertyChanged(nameof(ShowLocalBatch));
        OnPropertyChanged(nameof(LocalProgressPercentText));
        OnPropertyChanged(nameof(LocalProgressValue));
        OnPropertyChanged(nameof(LocalProgressIsIndeterminate));
        OnPropertyChanged(nameof(ShowLocalProgressBar));
        OnPropertyChanged(nameof(LocalProgressCountText));
        OnPropertyChanged(nameof(ShowLocalProgressCount));
        OnPropertyChanged(nameof(LocalProgressDetailText));
        OnPropertyChanged(nameof(ShowLocalProgressDetail));
        OnPropertyChanged(nameof(LocalProgressAccent));
        OnPropertyChanged(nameof(ApiProgressTitle));
        OnPropertyChanged(nameof(ApiProgressStatus));
        OnPropertyChanged(nameof(ApiProgressCountText));
        OnPropertyChanged(nameof(ApiProgressDetailText));
        OnPropertyChanged(nameof(ShowApiProgressDetail));
        OnPropertyChanged(nameof(ShowApiProgressBar));
        OnPropertyChanged(nameof(ApiProgressIsIndeterminate));
        OnPropertyChanged(nameof(ApiProgressValue));
        OnPropertyChanged(nameof(ApiProgressPercentText));
        OnPropertyChanged(nameof(ApiProgressAccent));
    }

    private void UpdateCompletionVisibility()
    {
        if (HasProcessingActivity)
        {
            _completionTimer.Stop();
            _showCompletionStatus = false;
            return;
        }

        var newCompletion = false;
        if (!IsLocalPriorityActive
            && State?.LocalProgress is { Phase: LocalReplayPhase.Completed, Total: > 0 } local
            && local.BatchNumber > 0
            && local.Processed >= local.Total
            && local.Failed == 0
            && local.BatchNumber != _lastCompletedLocalBatch)
        {
            _lastCompletedLocalBatch = local.BatchNumber;
            newCompletion = true;
        }
        if (HasCompletedApiProgress
            && _apiProgress is { } apiProgress
            && _lastCompletedApiOperation != apiProgress.StartedAtUtc)
        {
            _lastCompletedApiOperation = apiProgress.StartedAtUtc;
            newCompletion = true;
        }

        if (newCompletion)
        {
            _showCompletionStatus = true;
            _completionTimer.Stop();
            _completionTimer.Start();
        }
        else if (!_showCompletionStatus)
        {
            IsProcessingStatusExpanded = false;
        }
    }

    private void OnCompletionTimerTick(object? sender, EventArgs e)
    {
        _completionTimer.Stop();
        _showCompletionStatus = false;
        if (!HasProcessingActivity)
        {
            IsProcessingStatusExpanded = false;
        }
        RefreshProcessingProgress();
    }

    private static string FormatPercent(double value) => value.ToString("0.0", L.Culture) + "%";

    private void RefreshMetrics()
    {
        var stats = State?.Stats;
        if (stats == null)
        {
            Metrics = Array.Empty<MetricItem>();
        }
        else
        {
            Metrics = new[]
            {
                new MetricItem(TodayCaption, L.N(stats.Today), Glyphs.Calendar, Green, Ink),
                new MetricItem(stats.BestDay is { } date ? L.F("main.best_day_caption", date) : BestDayCaption,
                    stats.BestDay is null ? "—" : L.N(stats.BestDayChests), Glyphs.Trophy, Cyan, Cyan),
                new MetricItem(AverageCaption, stats.Matches > 0 ? stats.Average.ToString("0.0", L.Culture) : "—",
                    Glyphs.Controller, Ink, Ink),
                new MetricItem(BestMatchCaption, stats.Matches > 0 || stats.BestDay != null ? L.N(stats.BestMatch) : "—",
                    Glyphs.Medal, Ink, Cyan),
                new MetricItem(stats.HistorySince is { } since ? L.F("main.since", since) : HistoryCaption,
                    L.Count(stats.HistoryMatches, "common.match", "common.matches"), Glyphs.Calendar, Ink, Ink),
                new MetricItem(TreasuresCaption, L.N(stats.Treasures), Glyphs.Diamond, Ink, Cyan),
            };
        }
        OnPropertyChanged(nameof(Metrics));
    }

    private void RefreshDays()
    {
        var days = State?.Stats.LastDays ?? Array.Empty<DayChests>();
        var max = days.Count > 0 ? days.Max(day => day.Chests) : 0;
        Days = days.Select(day => new DayItem(
            day.Day.ToString("ddd", L.Culture)[..Math.Min(3, day.Day.ToString("ddd", L.Culture).Length)],
            L.N(day.Chests),
            max == 0 || day.Chests == 0 ? 0 : day.Chests / (double)max * 58,
            day.Chests == max && max > 0)).ToArray();
        OnPropertyChanged(nameof(Days));
    }

    private void RefreshProfiles()
    {
        var profiles = State?.Profiles ?? Array.Empty<Profile>();
        Profiles = profiles.Select(profile => new ProfileOption(profile.Key, ProfilesHelper.Describe(profile))).ToArray();
        _syncingProfile = true;
        OnPropertyChanged(nameof(Profiles));
        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Key == State?.CurrentKey);
        _syncingProfile = false;
    }

    private void RefreshTeam()
    {
        var team = State?.Team;
        if (team == null)
        {
            TeamMembers = Array.Empty<TeamMemberItem>();
            TeamSubtitle = TeamTitle;
            TeamSummary = "";
            TeamMessage = State == null
                ? "—"
                : L.T(_runtime.Monitor.Config.ShowTeammates ? "main.team_data_unavailable" : "main.team_disabled");
        }
        else
        {
            TeamSubtitle = team.Live ? L.T("main.team_current_match") : L.T("main.team_last_match");
            var noDetails = team.Unknown
                ? L.T("main.match_analyzed_before_teammates_were_saved")
                : team.Mates.Count == 0 ? L.T("main.solo_match_no_teammates") : "";
            TeamMessage = noDetails;
            TeamMembers = new[] { new TeamMemberItem(L.T("main.you"), L.N(team.Mine), true) }
                .Concat(team.Mates.Select(mate => new TeamMemberItem(mate.Name, "≥ " + L.N(mate.Chests), false)))
                .ToArray();
            TeamSummary = team.Mates.Count > 0 && team.Total > 0
                ? L.F("main.team_teammates", L.N(team.Total), (100.0 * team.MatesTotal / team.Total).ToString("0", L.Culture))
                : "";
        }
        OnPropertyChanged(nameof(TeamMembers));
        OnPropertyChanged(nameof(TeamSubtitle));
        OnPropertyChanged(nameof(TeamSummary));
        OnPropertyChanged(nameof(HasTeamSummary));
        OnPropertyChanged(nameof(TeamMessage));
        OnPropertyChanged(nameof(HasTeamMessage));
    }

    private void UpdateLastMatch()
    {
        var match = State?.Stats.LastMatch;
        LastMatchValue = match == null
            ? "—"
            : L.Count(match.Chests, "common.chest_opened", "common.chests_opened");
        var parts = new List<string>();
        if (match?.Placement is { } place)
        {
            parts.Add(match.Players is { } players ? $"{L.Ordinal(place)} {L.T("main.of")} {players}" : L.Ordinal(place));
        }
        else
        {
            parts.Add("—");
        }
        parts.Add(L.F("main.elim", match?.Eliminations is { } eliminations ? L.N(eliminations) : "—"));
        if (match is { Destroyed: > 0 }) parts.Add(L.F("main.destroyed", match.Destroyed));
        if (match is { Treasures: > 0 }) parts.Add(L.Count(match.Treasures, "common.treasure", "common.treasures"));
        LastMatchDetails = string.Join(" · ", parts);
        OnPropertyChanged(nameof(LastMatchValue));
        OnPropertyChanged(nameof(LastMatchDetails));
    }

    [RelayCommand]
    private void ToggleOverlay() => _runtime.Monitor.ToggleOverlay();

    [RelayCommand]
    private void ToggleProcessingStatus() => IsProcessingStatusExpanded = !IsProcessingStatusExpanded;

    [RelayCommand]
    private async Task OpenReport()
    {
        await ResolveRemoteProfileAsync();
        if (_remoteProfilePlayerId is not { } playerId)
        {
            OpenExternalUrl(RemoteProfileUrl.PlayersUrl);
            return;
        }

        OpenRemoteProfile(playerId);
    }

    private async Task ResolveRemoteProfileAsync()
    {
        var state = State;
        var identityKey = ProfileIdentityKey(state);
        if (identityKey == null)
        {
            _remoteProfileCancellation?.Cancel();
            _remoteProfileIdentityKey = null;
            _remoteProfilePlayerId = null;
            _remoteProfileResolving = false;
            _remoteProfileRetryTimer.Stop();
            OnPropertyChanged(nameof(RemoteProfileStatus));
            OnPropertyChanged(nameof(ShowRemoteProfileStatus));
            return;
        }

        if (_remoteProfileIdentityKey != identityKey)
        {
            _remoteProfileCancellation?.Cancel();
            _remoteProfileIdentityKey = identityKey;
            _remoteProfilePlayerId = null;
            _remoteProfileResolving = false;
        }

        var savedIdentity = _runtime.Monitor.GetFortCardsPlayerIdentity(identityKey);
        if (savedIdentity?.PlayerId > 0)
        {
            _remoteProfilePlayerId = savedIdentity.PlayerId;
            OpenStartupProfileIfRequested(savedIdentity.PlayerId);
            if (savedIdentity.LastResolvedAtUtc > DateTimeOffset.UtcNow - RemoteIdentityRefreshInterval)
            {
                _remoteProfileResolving = false;
                _remoteProfileRetryTimer.Stop();
            }
            else if (_remoteProfileResolutionTask != null && _remoteProfileResolving)
            {
                _remoteProfileRetryTimer.Stop();
            }
            else if (_remoteProfileRetryAfter.TryGetValue(identityKey, out var savedRetryAt)
                && savedRetryAt > DateTimeOffset.UtcNow)
            {
                _remoteProfileResolving = false;
                _remoteProfileRetryTimer.Start();
            }
            else
            {
                _ = StartRemoteProfileResolution(identityKey, state, hasCachedIdentity: true);
            }
            OnPropertyChanged(nameof(RemoteProfileStatus));
            OnPropertyChanged(nameof(ShowRemoteProfileStatus));
            return;
        }

        if (_remoteProfileResolutionTask != null && _remoteProfileResolving)
        {
            await _remoteProfileResolutionTask;
            return;
        }

        if (_remoteProfileRetryAfter.TryGetValue(identityKey, out var retryAt)
            && retryAt > DateTimeOffset.UtcNow)
        {
            OnPropertyChanged(nameof(RemoteProfileStatus));
            OnPropertyChanged(nameof(ShowRemoteProfileStatus));
            return;
        }

        await StartRemoteProfileResolution(identityKey, state, hasCachedIdentity: false);
    }

    private Task StartRemoteProfileResolution(string identityKey, MonitorState? state, bool hasCachedIdentity)
    {
        _remoteProfileCancellation?.Dispose();
        _remoteProfileCancellation = new CancellationTokenSource();
        var cancellationToken = _remoteProfileCancellation.Token;
        _remoteProfileResolving = true;
        OnPropertyChanged(nameof(RemoteProfileStatus));
        OnPropertyChanged(nameof(ShowRemoteProfileStatus));

        var task = ResolveAndStoreRemoteProfileAsync(
            identityKey,
            state?.CurrentEpicId,
            state?.CurrentPlayerName,
            hasCachedIdentity,
            cancellationToken);
        _remoteProfileResolutionTask = task;
        return task;
    }

    private async Task ResolveAndStoreRemoteProfileAsync(
        string identityKey,
        string? epicId,
        string? playerName,
        bool hasCachedIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await _runtime.ApiSync.ResolveFortCardsPlayerAsync(epicId, playerName, cancellationToken);
            if (resolution.Status == FortCardsPlayerResolutionStatus.Found && resolution.PlayerId is > 0)
            {
                _runtime.Monitor.SetFortCardsPlayerIdentity(identityKey, resolution.PlayerId.Value);
            }
            if (_remoteProfileIdentityKey != identityKey)
            {
                return;
            }

            _remoteProfileResolving = false;
            if (resolution.Status == FortCardsPlayerResolutionStatus.Found && resolution.PlayerId is > 0)
            {
                _remoteProfilePlayerId = resolution.PlayerId;
                _remoteProfileRetryAfter.Remove(identityKey);
                _remoteProfileRetryTimer.Stop();
                OpenStartupProfileIfRequested(resolution.PlayerId.Value);
            }
            else
            {
                if (!hasCachedIdentity)
                {
                    _remoteProfilePlayerId = null;
                }
                _remoteProfileRetryAfter[identityKey] = DateTimeOffset.UtcNow.AddMinutes(1);
                _remoteProfileRetryTimer.Start();
            }
            OnPropertyChanged(nameof(RemoteProfileStatus));
            OnPropertyChanged(nameof(ShowRemoteProfileStatus));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorLog.Write("Resolve and save FortCards player identity", ex);
            if (_remoteProfileIdentityKey == identityKey)
            {
                if (!hasCachedIdentity)
                {
                    _remoteProfilePlayerId = null;
                }
                _remoteProfileResolving = false;
                _remoteProfileRetryAfter[identityKey] = DateTimeOffset.UtcNow.AddMinutes(1);
                _remoteProfileRetryTimer.Start();
                OnPropertyChanged(nameof(RemoteProfileStatus));
                OnPropertyChanged(nameof(ShowRemoteProfileStatus));
            }
        }
        finally
        {
            if (_remoteProfileIdentityKey == identityKey)
            {
                _remoteProfileResolutionTask = null;
            }
        }
    }

    private void OpenStartupProfileIfRequested(long playerId)
    {
        if (!_startupProfileOpenPending)
        {
            return;
        }
        _startupProfileOpenPending = false;
        OpenRemoteProfile(playerId);
    }

    private void OpenRemoteProfile(long playerId)
    {
        if (!RemoteProfileUrl.TryBuild(playerId, out var uri, out var error))
        {
            _runtime.Monitor.SetNotice(error);
            return;
        }

        OpenExternalUrl(uri!.AbsoluteUri);
    }

    private void OpenExternalUrl(string url)
    {
        try
        {
            _openExternalLink(url);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException
            or PlatformNotSupportedException or UnauthorizedAccessException or SecurityException)
        {
            ErrorLog.Write("Open FortCards player profile", ex);
            _runtime.Monitor.SetNotice(L.T("main.remote_profile_open_failed"));
        }
    }

    private static string? ProfileIdentityKey(MonitorState? state)
    {
        if (!string.IsNullOrWhiteSpace(state?.CurrentEpicId))
        {
            return "epic:" + state.CurrentEpicId.Trim().ToUpperInvariant();
        }
        if (!string.IsNullOrWhiteSpace(state?.CurrentPlayerName))
        {
            return "name:" + state.CurrentPlayerName.Trim().ToUpperInvariant();
        }
        return null;
    }

    private void OnRemoteProfileRetryTimerTick(object? sender, EventArgs e)
    {
        if (!_remoteProfileResolving)
        {
            _ = ResolveRemoteProfileAsync();
        }
    }

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    [RelayCommand]
    private void KeepOpen() => _runtime.Monitor.KeepOpen();

    [RelayCommand]
    private void InstallUpdate() => _runtime.Monitor.InstallUpdate();

    public void Dispose()
    {
        _disposed = true;
        _completionTimer.Stop();
        _completionTimer.Tick -= OnCompletionTimerTick;
        _remoteProfileRetryTimer.Stop();
        _remoteProfileRetryTimer.Tick -= OnRemoteProfileRetryTimerTick;
        _remoteProfileCancellation?.Cancel();
        _remoteProfileCancellation?.Dispose();
        _runtime.Monitor.Changed -= OnMonitorChanged;
        _runtime.ApiSync.StateChanged -= OnApiStateChanged;
        _runtime.ApiSync.ProgressChanged -= OnApiProgressChanged;
        _runtime.ApiSync.AuthStatusChanged -= OnApiAuthStatusChanged;
    }

    private static class Glyphs
    {
        public static readonly Geometry ChevronDown = Geometry.Parse("M5 8 L12 15 L19 8");
        public static readonly Geometry ChevronUp = Geometry.Parse("M5 15 L12 8 L19 15");
        public static readonly Geometry Calendar = Geometry.Parse("M4 4 H20 V21 H4 Z M4 9 H20 M8 2 V6 M16 2 V6 M8 13 H9 M12 13 H13 M16 13 H17 M8 17 H9 M12 17 H13");
        public static readonly Geometry Trophy = Geometry.Parse("M7 4 H17 V11 C17 15 14 17 12 17 C10 17 7 15 7 11 Z M7 6 H3 V9 C3 12 5 13 8 13 M17 6 H21 V9 C21 12 19 13 16 13 M12 17 V20 M8 21 H16 M9 20 H15");
        public static readonly Geometry Controller = Geometry.Parse("M5 8 C7 6 9 6 11 7 H17 C20 7 21 9 21 12 L20 17 C19 20 17 20 15 17 H9 C7 20 5 20 4 17 L3 12 C3 10 4 8 5 8 Z M7 10 V14 M5 12 H9 M16 10 H16.1 M18 13 H18.1");
        public static readonly Geometry Medal = Geometry.Parse("M7 3 H11 L13 10 L9 13 Z M17 3 H13 L11 10 L15 13 Z M6 13 A6 6 0 1 0 18 13 A6 6 0 1 0 6 13 Z M12 11 V15");
        public static readonly Geometry Diamond = Geometry.Parse("M3 8 L7 3 H17 L21 8 L12 21 Z M3 8 H21 M7 3 L9 8 L12 21 M17 3 L15 8 L12 21");
    }
}

internal sealed record MetricItem(string Caption, string Value, Geometry Icon, IBrush Accent, IBrush IconAccent);
internal sealed record DayItem(string Name, string Value, double BarHeight, bool IsPeak);
internal sealed record TeamMemberItem(string Name, string Value, bool IsPlayer);
internal sealed record ProfileOption(string Key, string Text)
{
    public override string ToString() => Text;
}

internal static class ProfilesHelper
{
    public static string Describe(Profile profile) => Profiles.Describe(profile);
}
