using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ChestCounter;

public partial class MainWindow : Window
{
    private readonly RuntimeContext _runtime;
    private readonly DashboardViewModel _viewModel;
    private bool _stackedDashboard;

    public MainWindow()
        : this(AppRuntime.Current ?? throw new InvalidOperationException("The application runtime is not initialized."))
    {
    }

    public MainWindow(RuntimeContext runtime)
    {
        _runtime = runtime;
        _viewModel = new DashboardViewModel(runtime);
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.SettingsRequested += ShowSettings;
        runtime.Monitor.CloseRequested += OnCloseRequested;
        Opened += OnOpened;
        Closed += OnClosed;
        if (runtime.Monitor.AutoMode)
        {
            WindowState = WindowState.Minimized;
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        if (_runtime.FirstRun)
        {
            var message = L.T("startup.counts_your_fortnite_chests_from_your_replays");
            var dialog = new InfoDialogWindow(
                L.T("startup.welcome_to_chestcounter"),
                message,
                _runtime.ExistingReplays.Count > 0
                    ? L.Count(_runtime.ExistingReplays.Count, "common.replay", "common.replays")
                    : L.T("startup.turn_on_record_replays_in_fortnite"),
                _runtime.ExistingReplays.Count > 0 ? L.T("startup.count_them") : L.T("startup.start"),
                _runtime.ExistingReplays.Count > 0 ? L.T("startup.next_matches_only") : null);
            var countThem = await dialog.ShowDialog<bool>(this);
            if (!IsVisible)
            {
                return;
            }
            if (_runtime.ExistingReplays.Count > 0 && !countThem)
            {
                _runtime.Tracker.MarkSkipped(_runtime.ExistingReplays);
            }
        }

        _runtime.Monitor.Start();
    }

    private async void ShowSettings()
    {
        var settings = new SettingsWindow(_runtime);
        await settings.ShowDialog(this);
    }

    private void OnDashboardSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 760;
        if (stacked == _stackedDashboard || sender is not Grid grid)
        {
            return;
        }

        _stackedDashboard = stacked;
        grid.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "1.03*,1*");
        grid.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto" : "Auto");
        var statistics = this.FindControl<StatisticsPanel>("Statistics")
            ?? throw new InvalidOperationException("Statistics panel is missing.");
        Grid.SetColumn(statistics, stacked ? 0 : 1);
        Grid.SetRow(statistics, stacked ? 1 : 0);
    }

    private void OnCloseRequested() => Dispatcher.UIThread.Post(Close);

    private void OnClosed(object? sender, EventArgs e)
    {
        _runtime.Monitor.CloseRequested -= OnCloseRequested;
        _viewModel.SettingsRequested -= ShowSettings;
        _viewModel.Dispose();
    }
}
