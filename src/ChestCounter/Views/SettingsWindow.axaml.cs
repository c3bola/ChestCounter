using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Diagnostics;

namespace ChestCounter;

public partial class SettingsWindow : Window
{
    private readonly RuntimeContext _runtime;
    private readonly SettingsViewModel _viewModel;
    private bool _resetting;
    private ReplayDiagnosticWindow? _replayDiagnosticWindow;

    public SettingsWindow()
        : this(AppRuntime.Current ?? throw new InvalidOperationException("The application runtime is not initialized."))
    {
    }

    public SettingsWindow(RuntimeContext runtime)
    {
        _runtime = runtime;
        _viewModel = new SettingsViewModel(runtime);
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.FolderSelectionRequested += ChooseFolder;
        _viewModel.LanguageChangeRequested += ConfirmLanguageChange;
        _viewModel.ResetRequested += ConfirmReset;
        _viewModel.CopyObsRequested += CopyObsUrl;
        _viewModel.ShowTeammatesConfirmationRequested += ConfirmShowTeammates;
        _viewModel.CountTeammatesConfirmationRequested += ConfirmCountTeammates;
        _viewModel.SocialLinkRequested += OpenSocialLink;
        _viewModel.ApiUrlEditRequested += EditApiUrl;
        _viewModel.ApiOperationRequested += StartApiOperation;
        _runtime.ApiSync.AuthStatusChanged += OnApiAuthStatusChanged;
        _runtime.ApiSync.StateChanged += OnApiSyncStateChanged;
        if (_runtime.Monitor.LatestOverlay is { } overlay)
            this.FindControl<OverlayVisual>("OverlayPreview")?.SetState(overlay);
        _runtime.Monitor.Changed += OnMonitorChanged;
        Closed += OnClosed;
    }

    private void OnMonitorChanged(MonitorState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible) return;
            this.FindControl<OverlayVisual>("OverlayPreview")?.SetState(state.Overlay);
            _viewModel.RefreshOverlayScale();
        });
    }

    private void OnApiSyncStateChanged(ApiSyncState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
            {
                _viewModel.RefreshApiStatus();
            }
        });
    }

    private void OnApiAuthStatusChanged(ApiAuthStatus status) => OnApiSyncStateChanged(_runtime.ApiSync.State);

    private async void EditApiUrl()
    {
        var dialog = new ApiUrlEditWindow(_viewModel.ApiBaseUrl);
        if (!await dialog.ShowDialog<bool>(this) || dialog.SavedUrl == null)
        {
            return;
        }

        try
        {
            _viewModel.SaveApiBaseUrl(dialog.SavedUrl);
            _viewModel.SetApiStatus(L.T("settings.api_url_saved"));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Update API URL", ex);
            _viewModel.SetApiStatus(L.T("settings.api_url_invalid"));
        }
    }

    private async void OnReplayDiagnosticClick(object? sender, RoutedEventArgs e)
    {
        if (!await LocalPasswordDialog.RequestAsync(this))
        {
            return;
        }

        if (_replayDiagnosticWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        var window = new ReplayDiagnosticWindow(_runtime);
        _replayDiagnosticWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_replayDiagnosticWindow, window))
            {
                _replayDiagnosticWindow = null;
            }
        };
        window.Show(this);
    }

    private async void StartApiOperation(bool testConnection)
    {
        if (!_viewModel.CanStartApiOperation)
        {
            return;
        }

        _viewModel.SetApiOperationRunning(true);
        try
        {
            var operationWindow = new ApiOperationWindow(_runtime.ApiSync, testConnection);
            operationWindow.Show(this);
            _viewModel.SetApiStatus(await operationWindow.RunAsync());
        }
        catch (Exception ex)
        {
            ErrorLog.Write(testConnection ? "Test API connection" : "Manual API synchronization", ex);
            _viewModel.SetApiStatus(L.T("startup.unexpected_error_the_details_are_in_errors_log"));
        }
        finally
        {
            _viewModel.SetApiOperationRunning(false);
            _viewModel.RefreshApiStatus();
        }
    }

    private async void ChooseFolder()
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L.T("settings.fortnite_replays_folder"),
                AllowMultiple = false,
            });
            var folder = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(folder))
            {
                _viewModel.SetFolder(folder);
            }
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus(ex.Message);
        }
    }

    private async void CopyObsUrl()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var url = ObsServer.Url;
            if (url == null)
            {
                _viewModel.SetStatus(L.T("settings.port_in_use"));
                return;
            }
            if (clipboard == null)
            {
                ErrorLog.Write("Copy OBS URL", new InvalidOperationException("Clipboard is unavailable."));
                _viewModel.SetStatus(L.T("startup.unexpected_error_the_details_are_in_errors_log"));
                return;
            }
            await clipboard.SetTextAsync(url);
            _viewModel.SetStatus(L.T("settings.copy"));
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus(ex.Message);
        }
    }

    private async void ConfirmLanguageChange()
    {
        var confirm = new InfoDialogWindow(
            L.T("settings.language"),
            L.T("settings.the_new_language_applies_when_the_counter_restar"),
            "",
            L.T("settings.restart_now"),
            L.T("settings.later"));
        if (await confirm.ShowDialog<bool>(this))
        {
            _runtime.Monitor.Restart();
            Close();
        }
    }

    private async void ConfirmReset()
    {
        if (_resetting)
        {
            return;
        }
        var confirm = new InfoDialogWindow(
            L.T("settings.reset_statistics_restablecer_estadisticas"),
            L.T("settings.all_your_saved_matches_chests_and_treasures_will"),
            "",
            L.T("settings.reset_statistics"),
            L.T("settings.cancel"));
        if (await confirm.ShowDialog<bool>(this))
        {
            _resetting = true;
            try
            {
                _viewModel.SetStatus(await _viewModel.ResetHistoryAsync());
            }
            catch (OperationCanceledException)
            {
                // The application is shutting down.
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Reset statistics", ex);
                _viewModel.SetStatus(L.T("error.reset_failed"));
            }
            finally
            {
                _resetting = false;
            }
        }
    }

    private async void ConfirmShowTeammates()
    {
        var dialog = new InfoDialogWindow(
            L.T("settings.teammates_in_the_report"),
            L.T("settings.the_window_and_the_report_will_show_who"),
            $"{L.T("settings.exact")}: {L.T("settings.who_you_play_with_matches_together_and_wins")}\n\n" +
            $"{L.T("settings.approximate_their_chests")}: {L.T("long.teammatesrange")}",
            L.T("settings.turn_on"),
            L.T("settings.cancel"));
        _viewModel.ConfirmShowTeammates(await dialog.ShowDialog<bool>(this));
    }

    private async void ConfirmCountTeammates()
    {
        var dialog = new InfoDialogWindow(
            L.T("settings.add_their_chests_to_your_total"),
            L.T("settings.your_total_your_average_your_records_and_the"),
            $"{L.T("settings.approximate_their_chests")}: {L.T("long.teammatesrange")}\n\n" +
            $"{L.T("settings.your_history_does_not_change")}: {L.T("settings.turning_it_off_shows_only_your_chests_again")}",
            L.T("settings.turn_on"),
            L.T("settings.cancel"));
        _viewModel.ConfirmCountTeammates(await dialog.ShowDialog<bool>(this));
    }

    private void OpenSocialLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus(ex.Message);
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _runtime.Monitor.Changed -= OnMonitorChanged;
        _runtime.ApiSync.StateChanged -= OnApiSyncStateChanged;
        this.FindControl<OverlayVisual>("OverlayPreview")?.Dispose();
        _viewModel.FolderSelectionRequested -= ChooseFolder;
        _viewModel.LanguageChangeRequested -= ConfirmLanguageChange;
        _viewModel.ResetRequested -= ConfirmReset;
        _viewModel.CopyObsRequested -= CopyObsUrl;
        _viewModel.ShowTeammatesConfirmationRequested -= ConfirmShowTeammates;
        _viewModel.CountTeammatesConfirmationRequested -= ConfirmCountTeammates;
        _viewModel.SocialLinkRequested -= OpenSocialLink;
        _viewModel.ApiUrlEditRequested -= EditApiUrl;
        _runtime.ApiSync.AuthStatusChanged -= OnApiAuthStatusChanged;
        _viewModel.ApiOperationRequested -= StartApiOperation;
    }
}
