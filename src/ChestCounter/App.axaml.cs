using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ChestCounter;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = AppRuntime.Current is { } runtime
                ? new MainWindow(runtime)
                : new StartupNoticeWindow(AppRuntime.StatusMessage);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class RuntimeContext(
    Monitor monitor,
    Store store,
    Tracker tracker,
    ApiSyncService apiSync,
    bool firstRun,
    IReadOnlyList<FileInfo> existingReplays)
{
    public Monitor Monitor { get; } = monitor;
    public Store Store { get; } = store;
    public Tracker Tracker { get; } = tracker;
    public ApiSyncService ApiSync { get; } = apiSync;
    public bool FirstRun { get; } = firstRun;
    public IReadOnlyList<FileInfo> ExistingReplays { get; } = existingReplays;
}

internal static class AppRuntime
{
    public static RuntimeContext? Current { get; set; }
    public static string StatusMessage { get; set; } = "";
}
