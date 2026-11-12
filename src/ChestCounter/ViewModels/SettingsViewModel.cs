using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChestCounter;

internal sealed partial class SettingsViewModel : ObservableObject
{
    private const int MaxInitial = 1_000_000;
    private const string ProjectRepositoryUrl = "https://github.com/c3bola/ChestCounter";
    private readonly RuntimeContext _runtime;
    private bool _startWithFortnite;
    private bool _openReportOnStart;
    private bool _showTeammates;
    private bool _countTeammates;
    private bool _countAmmoBoxes;
    private bool _countFoodBoxes;
    private bool _countLootCannons;
    private string _initialChests;
    private UpdateOption? _updateMode;
    private LanguageOption? _language;
    private string _demosFolder;
    private double _overlayScale;

    public SettingsViewModel(RuntimeContext runtime)
    {
        _runtime = runtime;
        var config = runtime.Monitor.Config;
        _startWithFortnite = config.StartWithFortnite;
        _openReportOnStart = config.OpenReportOnStart;
        _showTeammates = config.ShowTeammates;
        _countTeammates = config.CountTeammateChests;
        _countAmmoBoxes = config.CountAmmoBoxes;
        _countFoodBoxes = config.CountFoodBoxes;
        _countLootCannons = config.CountLootCannons;
        _initialChests = Math.Min(MaxInitial, runtime.Monitor.InitialChests).ToString(L.Culture);
        _demosFolder = config.DemosFolder;
        _overlayScale = OverlayScale.Clamp(config.OverlayScalePercent);
        UpdateModes = new[]
        {
            new UpdateOption("prompt", L.T("settings.ask")),
            new UpdateOption("auto", L.T("settings.automatic")),
            new UpdateOption("off", L.T("settings.never")),
        };
        _updateMode = UpdateModes.FirstOrDefault(option => option.Code == config.UpdateMode) ?? UpdateModes[0];
        var systemLanguage = L.Languages.First(language => language.Code == L.SystemCode);
        Languages = new[]
        {
            new LanguageOption(null, L.F("settings.system", systemLanguage.Name)),
        }.Concat(L.Languages.Select(language => new LanguageOption(language.Code, language.Name))).ToArray();
        _language = Languages.FirstOrDefault(language => language.Code == config.Language) ?? Languages[0];
    }

    public void ConfirmShowTeammates(bool confirmed)
    {
        if (confirmed)
        {
            ApplyShowTeammates(true);
        }
        else
        {
            OnPropertyChanged(nameof(ShowTeammates));
        }
    }

    public void ConfirmCountTeammates(bool confirmed)
    {
        if (confirmed)
        {
            ApplyCountTeammates(true);
        }
        else
        {
            OnPropertyChanged(nameof(CountTeammates));
        }
    }

    private void ApplyShowTeammates(bool value)
    {
        if (SetProperty(ref _showTeammates, value))
        {
            _runtime.Monitor.SetShowTeammates(value);
        }
    }

    private void ApplyCountTeammates(bool value)
    {
        if (SetProperty(ref _countTeammates, value))
        {
            _runtime.Monitor.SetCountTeammates(value);
        }
    }

    private int ParseInitialChests() =>
        int.TryParse(_initialChests, System.Globalization.NumberStyles.None, L.Culture, out var value)
            ? value
            : 0;

    private void SetInitialChests(int value) =>
        InitialChests = Math.Clamp(value, 0, MaxInitial).ToString(L.Culture);

    public string WindowTitle => L.T("main.settings");
    public string VersionText => L.F("settings.version", AppInfo.VersionText);
    public string OptionsTitle => L.T("settings.options");
    public string OpenWithFortniteLabel => L.T("settings.open_with_fortnite");
    public string OpenWithFortniteHint => L.T("settings.counts_your_matches_on_its_own");
    public string OpenReportLabel => L.T("settings.open_the_report_at_startup");
    public string TeammatesLabel => L.T("settings.teammates_in_the_report");
    public string CountTeammatesLabel => L.T("settings.add_their_chests_to_your_total");
    public string AmmoBoxesLabel => L.T("settings.ammo_boxes");
    public string FoodBoxesLabel => L.T("settings.food_boxes");
    public string LootCannonsLabel => L.T("settings.loot_cannons");
    public string CountOptionsTitle => L.T("settings.also_count_as_a_chest");
    public string UpdatesLabel => L.T("settings.updates");
    public string ReplaysFolderLabel => L.T("settings.replays_folder");
    public string ChangeLabel => L.T("settings.change");
    public string InitialChestsLabel => L.T("settings.chests_before_the_counter");
    public string ObsLabel => L.T("settings.overlay_for_obs");
    public string CopyLabel => L.T("settings.copy");
    public string ResetLabel => L.T("settings.reset_statistics");
    public string CloseLabel => L.T("settings.close");
    public string LanguageLabel => L.T("settings.language");
    public string DataFolderLabel => L.F("settings.data_folder", _runtime.Store.Folder);
    public string ObsUrl => ObsServer.Url ?? L.T("settings.port_in_use");
    public bool CanCopyObsUrl => ObsServer.Url != null;
    public string ObsStatus => ObsServer.Url == null ? L.T("settings.off") : L.T("settings.on");
    public string OverlayShortcutsTitle => L.T("settings.overlay_shortcuts");
    public string OverlayShortcutsText => L.T("settings.ctrl_shift_o_show_ctrl_shift_l_lock");
    public string OverlayToggleLockLabel => L.T("settings.toggle_overlay_lock");
    public string OverlayScaleLabel => L.T("settings.overlay_scale");
    public string OverlayPreviewLabel => L.T("settings.overlay_preview");
    public string OverlayDefaultLabel => L.T("settings.overlay_default");
    public string OverlayScaleText => L.F("settings.overlay_scale_current", (int)_overlayScale);
    public double OverlayPreviewWidth => OverlayScale.Width((int)_overlayScale);
    public double OverlayPreviewHeight => OverlayScale.Height((int)_overlayScale);
    public string SocialTitle => L.T("settings.web_and_social");
    public string ProfileEstimateLabel => _runtime.Monitor.ShownProfileName is { } name
        ? L.F("settings.estimate_added_to_s_total", name)
        : L.T("settings.estimate_added_to_your_total");
    public IReadOnlyList<UpdateOption> UpdateModes { get; }
    public IReadOnlyList<LanguageOption> Languages { get; }
    public string Status { get; private set; } = "";

    [RelayCommand]
    private void ToggleOverlayLock() => _runtime.Monitor.ToggleOverlayLock();

    public double OverlayScaleValue
    {
        get => _overlayScale;
        set
        {
            if (!double.IsFinite(value))
            {
                ErrorLog.Write("Overlay scale", new ArgumentOutOfRangeException(nameof(value)));
                OnPropertyChanged(nameof(OverlayScaleValue));
                return;
            }
            var scale = OverlayScale.Clamp((int)Math.Round(value / 10) * 10);
            if (scale == _overlayScale) return;
            if (!_runtime.Monitor.SetOverlayScale(scale))
            {
                SetStatus(L.T("startup.unexpected_error_the_details_are_in_errors_log"));
                OnPropertyChanged(nameof(OverlayScaleValue));
                return;
            }
            RefreshOverlayScale();
        }
    }

    public void RefreshOverlayScale()
    {
        if (SetProperty(ref _overlayScale, (double)OverlayScale.Clamp(_runtime.Monitor.Config.OverlayScalePercent), nameof(OverlayScaleValue)))
        {
            OnPropertyChanged(nameof(OverlayScaleText));
            OnPropertyChanged(nameof(OverlayPreviewWidth));
            OnPropertyChanged(nameof(OverlayPreviewHeight));
        }
    }

    [RelayCommand]
    private void ResetOverlayScale() => OverlayScaleValue = OverlayScale.Default;

    public bool StartWithFortnite
    {
        get => _startWithFortnite;
        set
        {
            if (!SetProperty(ref _startWithFortnite, value))
            {
                return;
            }
            if (!_runtime.Monitor.SetStartWithFortnite(value))
            {
                _startWithFortnite = !value;
                OnPropertyChanged(nameof(StartWithFortnite));
                Status = L.T("settings.not_possible_from_here_open_the_counter_with");
                OnPropertyChanged(nameof(Status));
            }
        }
    }

    public bool OpenReportOnStart
    {
        get => _openReportOnStart;
        set
        {
            if (SetProperty(ref _openReportOnStart, value))
            {
                _runtime.Monitor.Change(config => config.OpenReportOnStart = value);
            }
        }
    }

    public bool ShowTeammates
    {
        get => _showTeammates;
        set
        {
            if (value && !_showTeammates)
            {
                OnPropertyChanged(nameof(ShowTeammates));
                ShowTeammatesConfirmationRequested?.Invoke();
                return;
            }
            ApplyShowTeammates(value);
        }
    }

    public bool CountTeammates
    {
        get => _countTeammates;
        set
        {
            if (value && !_countTeammates)
            {
                OnPropertyChanged(nameof(CountTeammates));
                CountTeammatesConfirmationRequested?.Invoke();
                return;
            }
            ApplyCountTeammates(value);
        }
    }

    public bool CountAmmoBoxes
    {
        get => _countAmmoBoxes;
        set
        {
            if (SetProperty(ref _countAmmoBoxes, value))
            {
                _runtime.Monitor.SetCountExtra(config => config.CountAmmoBoxes = value);
            }
        }
    }

    public bool CountFoodBoxes
    {
        get => _countFoodBoxes;
        set
        {
            if (SetProperty(ref _countFoodBoxes, value))
            {
                _runtime.Monitor.SetCountExtra(config => config.CountFoodBoxes = value);
            }
        }
    }

    public bool CountLootCannons
    {
        get => _countLootCannons;
        set
        {
            if (SetProperty(ref _countLootCannons, value))
            {
                _runtime.Monitor.SetCountExtra(config => config.CountLootCannons = value);
            }
        }
    }

    public string InitialChests
    {
        get => _initialChests;
        set
        {
            if (value.Any(character => !char.IsDigit(character)))
            {
                OnPropertyChanged(nameof(InitialChests));
                return;
            }
            if (SetProperty(ref _initialChests, value))
            {
                var count = int.TryParse(value, System.Globalization.NumberStyles.None, L.Culture, out var parsed)
                    ? Math.Clamp(parsed, 0, MaxInitial)
                    : 0;
                _runtime.Monitor.SetInitialChests(count);
            }
        }
    }

    public string DemosFolder
    {
        get => _demosFolder;
        private set => SetProperty(ref _demosFolder, value);
    }

    public UpdateOption? UpdateMode
    {
        get => _updateMode;
        set
        {
            if (SetProperty(ref _updateMode, value) && value != null)
            {
                _runtime.Monitor.Change(config => config.UpdateMode = value.Code);
            }
        }
    }

    public LanguageOption? Language
    {
        get => _language;
        set
        {
            if (SetProperty(ref _language, value) && value?.Code != _runtime.Monitor.Config.Language)
            {
                _runtime.Monitor.Change(config => config.Language = value?.Code);
                LanguageChangeRequested?.Invoke();
            }
        }
    }

    public event Action? FolderSelectionRequested;
    public event Action? LanguageChangeRequested;
    public event Action? ResetRequested;
    public event Action? CopyObsRequested;
    public event Action? ShowTeammatesConfirmationRequested;
    public event Action? CountTeammatesConfirmationRequested;
    public event Action<string>? SocialLinkRequested;

    [RelayCommand]
    private void ChooseFolder() => FolderSelectionRequested?.Invoke();

    [RelayCommand]
    private void CopyObs() => CopyObsRequested?.Invoke();

    [RelayCommand]
    private void ResetStats() => ResetRequested?.Invoke();

    [RelayCommand]
    private void AddTen() => SetInitialChests(ParseInitialChests() + 10);

    [RelayCommand]
    private void SubtractTen() => SetInitialChests(ParseInitialChests() - 10);

    [RelayCommand]
    private void OpenTelegram() => SocialLinkRequested?.Invoke("https://t.me/fortnitebrasil");

    [RelayCommand]
    private void OpenX() => SocialLinkRequested?.Invoke("https://x.com/Embananeiro/");

    [RelayCommand]
    private void OpenYouTube() => SocialLinkRequested?.Invoke("https://www.youtube.com/c3bola");

    [RelayCommand]
    private void OpenTikTok() => SocialLinkRequested?.Invoke("https://www.tiktok.com/@c3bola");

    [RelayCommand]
    private void OpenGitHub() => SocialLinkRequested?.Invoke(ProjectRepositoryUrl);

    public void SetFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder == DemosFolder)
        {
            return;
        }
        DemosFolder = folder;
        _runtime.Monitor.ChangeDemosFolder(folder);
    }

    public async Task<string> ResetHistoryAsync()
    {
        var backup = await _runtime.Monitor.ResetStatsAsync();
        SetProperty(ref _initialChests, "0", nameof(InitialChests));
        return L.F("settings.statistics_reset_backup_copy", Path.GetFileName(backup));
    }

    public void SetStatus(string message)
    {
        Status = message;
        OnPropertyChanged(nameof(Status));
    }
}

internal sealed record UpdateOption(string Code, string Text)
{
    public override string ToString() => Text;
}

internal sealed record LanguageOption(string? Code, string Text)
{
    public override string ToString() => Text;
}
