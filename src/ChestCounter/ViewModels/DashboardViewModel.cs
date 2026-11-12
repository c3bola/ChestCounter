using System.Diagnostics;
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

    private readonly RuntimeContext _runtime;
    private MonitorState? _state;
    private ProfileOption? _selectedProfile;
    private bool _syncingProfile;

    public DashboardViewModel(RuntimeContext runtime)
    {
        _runtime = runtime;
        runtime.Monitor.Changed += OnMonitorChanged;
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
    public string ReportButtonText => L.T("main.report");
    public string SettingsButtonText => L.T("main.settings");
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
                RefreshMetrics();
                RefreshDays();
                RefreshProfiles();
                RefreshTeam();
                UpdateLastMatch();
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

    private void OnMonitorChanged(MonitorState state) =>
        Dispatcher.UIThread.Post(() => State = state);

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
    private void OpenReport()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_runtime.Store.ReportPath) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _runtime.Monitor.SetNotice(ex.Message);
        }
    }

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    [RelayCommand]
    private void KeepOpen() => _runtime.Monitor.KeepOpen();

    [RelayCommand]
    private void InstallUpdate() => _runtime.Monitor.InstallUpdate();

    public void Dispose() => _runtime.Monitor.Changed -= OnMonitorChanged;

    private static class Glyphs
    {
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
