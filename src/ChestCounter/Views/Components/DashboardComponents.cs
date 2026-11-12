using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace ChestCounter;

public partial class DashboardHeader : UserControl
{
    private bool _stacked;

    public DashboardHeader() => InitializeComponent();

    private void OnHeaderSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 720;
        if (stacked == _stacked || sender is not Grid grid)
        {
            return;
        }

        _stacked = stacked;
        grid.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,Auto");
        grid.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto" : "Auto");
        var profile = this.FindControl<Border>("ProfileSelector")
            ?? throw new InvalidOperationException("Profile selector is missing.");
        Grid.SetColumn(profile, stacked ? 0 : 1);
        Grid.SetRow(profile, stacked ? 1 : 0);
        profile.HorizontalAlignment = stacked
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Stretch;
        var banner = this.FindControl<Border>("HeaderBanner")
            ?? throw new InvalidOperationException("Header banner is missing.");
        banner.Width = stacked ? double.NaN : 420;
        banner.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        var mark = this.FindControl<Control>("HeaderMark")
            ?? throw new InvalidOperationException("Header icon is missing.");
        var stripes = this.FindControl<Control>("HeaderStripes")
            ?? throw new InvalidOperationException("Header decoration is missing.");
        var title = this.FindControl<TextBlock>("HeaderTitle")
            ?? throw new InvalidOperationException("Header title is missing.");
        mark.IsVisible = !stacked;
        stripes.IsVisible = !stacked;
        title.FontSize = stacked ? 19 : 23;
    }

    private Window OwnerWindow => TopLevel.GetTopLevel(this) as Window
        ?? throw new InvalidOperationException("Dashboard header has no window.");

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
        }
        else
        {
            OwnerWindow.BeginMoveDrag(e);
        }
    }

    private void ToggleMaximized() => OwnerWindow.WindowState = OwnerWindow.WindowState == WindowState.Maximized
        ? WindowState.Normal
        : WindowState.Maximized;

    private void OnMinimize(object? sender, RoutedEventArgs e) => OwnerWindow.WindowState = WindowState.Minimized;
    private void OnMaximizeRestore(object? sender, RoutedEventArgs e) => ToggleMaximized();
    private void OnClose(object? sender, RoutedEventArgs e) => OwnerWindow.Close();
}

public partial class HeroPanel : UserControl
{
    public HeroPanel() => InitializeComponent();
}

public partial class LastMatchPanel : UserControl
{
    public LastMatchPanel() => InitializeComponent();
}

public partial class WeeklyChart : UserControl
{
    public WeeklyChart() => InitializeComponent();
}

public partial class MetricCard : UserControl
{
    public MetricCard() => InitializeComponent();
}

public partial class StatisticsPanel : UserControl
{
    public StatisticsPanel() => InitializeComponent();
}

public partial class TeamPanel : UserControl
{
    public TeamPanel() => InitializeComponent();
}
