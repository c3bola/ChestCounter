using System.Diagnostics;
using System.Text;
using Avalonia;

namespace ChestCounter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--standby"))
        {
            var dataIndex = Array.IndexOf(args, "--data");
            return OperatingSystem.IsWindows()
                ? FortniteWatch.RunStandby(dataIndex >= 0 && dataIndex + 1 < args.Length ? args[dataIndex + 1] : Store.DefaultFolder())
                : 1;
        }

        var cliMode = args.Any(a => a is "--once" or "--help" or "-h" or "--overlay-preview");
        if (OperatingSystem.IsWindows())
        {
            NativeConsole.Ensure(attachToParent: cliMode);
            if (!cliMode)
            {
                NativeConsole.Hide();
            }
        }
        Console.OutputEncoding = Encoding.UTF8;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write("Unexpected error", e.ExceptionObject);

        string? dataDir = null, demosArg = null, previewPath = null;
        var once = false;
        var reset = false;
        var auto = false;
        var afterUpdate = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data": dataDir = args[++i]; break;
                case "--demos": demosArg = args[++i]; break;
                case "--lang": i++; break;
                case "--once": once = true; break;
                case "--reset-config": reset = true; break;
                case "--overlay-preview": previewPath = args[++i]; break;
                case "--auto": auto = true; break;
                case "--after-update": afterUpdate = true; break;
                case "-h":
                case "--help":
                    Console.WriteLine(L.T("long.help"));
                    return 0;
            }
        }

        dataDir ??= Store.DefaultFolder();
        var store = new Store(dataDir);
        ErrorLog.FilePath = store.ErrorLogPath;
        using var instance = new Mutex(false, AppInfo.InstanceMutexFor(dataDir));
        if (previewPath == null && !AcquireInstance(instance, afterUpdate ? TimeSpan.FromSeconds(20) : TimeSpan.Zero))
        {
            if (auto)
            {
                return 0;
            }
            AppRuntime.StatusMessage = L.T("startup.the_chest_counter_is_already_open_look_in");
            return RunAvalonia();
        }

        var config = reset ? null : store.LoadConfig();
        var firstRun = config == null;
        config ??= new Config();
        L.Use(config.Language);
        if (demosArg != null)
        {
            config.DemosFolder = demosArg;
        }
        if (string.IsNullOrWhiteSpace(config.DemosFolder))
        {
            config.DemosFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FortniteGame", "Saved", "Demos");
        }

        if (previewPath != null)
        {
            return CreateOverlayPreviews(store, config, previewPath);
        }

        LowerPriority();
        store.LoadPrizeNames();
        var data = store.LoadData();
        var tracker = new Tracker(store, config, data);
        var reanalyzing = tracker.UpgradeIfNeeded();
        if (once)
        {
            tracker.Scan(ignoreQuietPeriod: true);
            ReportWriter.WriteAll(store, config, data);
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            var overlay = OverlayModel.Build(Profiles.View(data, config), null, false,
                ReportWriter.SessionStartUtc, Profiles.InitialChests(data, config));
            using var visual = new OverlayVisual();
            var (png, _) = visual.RenderState(overlay);
            File.WriteAllBytes(Path.Combine(dataDir, "overlay.png"), png);
            tracker.PrintSummary();
            Console.WriteLine(L.F("startup.report", store.ReportPath));
            return 0;
        }

        var existing = firstRun
            ? tracker.UnprocessedFiles().Where(f => ReplayHeaderProbe.IsLive(f.FullName) != true).ToArray()
            : Array.Empty<FileInfo>();
        store.SaveConfig(config);

        if (OperatingSystem.IsWindows())
        {
            Updater.CleanupOld();
            if (config.StartWithFortnite)
            {
                FortniteWatch.Apply(true, dataDir);
            }
        }

        var apiConfigStore = new ApiIntegrationConfigStore(Path.Combine(dataDir, "api-config.json"));
        _ = apiConfigStore.Load();
        var apiSync = new ApiSyncService(apiConfigStore, Path.Combine(dataDir, "api-sync-queue.json"));
        tracker.MatchPersisted += apiSync.QueueReplay;
        apiSync.Start();
        apiSync.QueueHistorical(data, tracker.ReplayFiles());

        var monitorServices = new MonitorServices();
        var monitorDependencies = new MonitorDependencies(
            monitorServices, monitorServices, monitorServices, monitorServices, monitorServices, monitorServices);
        var monitor = new ChestCounter.Monitor(store, config, data, tracker, dataDir,
            new NativeOverlayHost(Path.Combine(dataDir, "overlay.png")), monitorDependencies) { AutoMode = auto };
        if (reanalyzing)
        {
            monitor.SetNotice(L.T("startup.new_analysis_version_the_replays_are_read_again"));
        }
        if (OperatingSystem.IsWindows() && Process.GetProcessesByName("ChestTracker") is { Length: > 0 } oldTool)
        {
            monitor.SetNotice(L.T("startup.the_old_version_chesttracker_exe_is_still_open"));
            foreach (var process in oldTool)
            {
                process.Dispose();
            }
        }

        AppRuntime.Current = new RuntimeContext(monitor, store, tracker, apiSync, firstRun, existing);
        int exitCode;
        try
        {
            exitCode = RunAvalonia();
        }
        finally
        {
            tracker.MatchPersisted -= apiSync.QueueReplay;
            monitor.Stop();
            apiSync.Dispose();
        }
        if ((monitor.RestartAfterUpdate || monitor.RestartRequested) && AppInfo.ExePath is { } newExe)
        {
            try { instance.ReleaseMutex(); } catch { }
            var kept = args.Where(a => a != "--after-update").Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
            var restartArgs = string.Join(" ", monitor.RestartAfterUpdate ? kept.Append("--after-update") : kept);
            Process.Start(new ProcessStartInfo(newExe, restartArgs) { UseShellExecute = true })?.Dispose();
        }
        return exitCode;
    }

    private static int CreateOverlayPreviews(Store store, Config config, string previewPath)
    {
        store.LoadPrizeNames();
        var previewData = Profiles.View(store.LoadData(), config);
        foreach (var match in previewData.Matches.OrderByDescending(m => m.MatchStartUtc ?? m.AnalyzedAtUtc).Take(3))
        {
            match.MatchStartUtc = DateTime.UtcNow;
        }
        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        using var visual = new OverlayVisual();
        var fakeLive = new MatchRecord { ChestsOpened = 12, TreasureFinds = { new TreasureFind { Item = "Treasure" } } };
        foreach (var (suffix, live, inProgress) in new[]
                 {
                     ("-en-vivo", fakeLive, true),
                     ("-en-curso", (MatchRecord?)null, true),
                     ("-espera", null, false),
                 })
        {
            var state = OverlayModel.Build(
                previewData,
                live,
                inProgress,
                ReportWriter.SessionStartUtc,
                Profiles.InitialChests(config, previewData.Matches.Select(Profiles.Key).FirstOrDefault()));
            var file = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(previewPath)) ?? ".",
                Path.GetFileNameWithoutExtension(previewPath) + suffix + ".png");
            var (png, _) = visual.RenderState(state);
            File.WriteAllBytes(file, png);
            Console.WriteLine(L.F("startup.preview", file));
        }
        return 0;
    }

    private static int RunAvalonia() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(Array.Empty<string>());

    private static bool AcquireInstance(Mutex mutex, TimeSpan wait)
    {
        try
        {
            return mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static void LowerPriority()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        try { EcoQos.Enable(); } catch { }
    }
}
