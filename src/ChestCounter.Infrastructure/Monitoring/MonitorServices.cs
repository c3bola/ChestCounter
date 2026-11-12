namespace ChestCounter;

public sealed class MonitorServices :
    IMonitorReporting,
    IObsServer,
    IUpdateService,
    IGameProcessMonitor,
    IErrorLogger,
    IExecutableInfo
{
    public DateTime SessionStartUtc => ReportWriter.SessionStartUtc;
    public string? ExePath => AppInfo.ExePath;

    public void WriteAll(IMonitorStorage store, Config config, TrackerData data) =>
        ReportWriter.WriteAll(store, config, data);

    public void WriteOverlay(IMonitorStorage store, OverlayState state, int scale) =>
        ReportWriter.WriteOverlay(store, state, scale);

    public void Start(int port) => ObsServer.Start(port);
    public void Stop() => ObsServer.Stop();
    public void UpdateScale(int scale) => ObsServer.UpdateScale(scale);

    public Task<UpdateInfo?> CheckAsync() => Updater.CheckAsync();
    public string? Install(UpdateInfo update) => Updater.Install(update);

    public bool Apply(bool enabled, string dataDir) => FortniteWatch.Apply(enabled, dataDir);
    public bool IsFortniteRunning() => FortniteWatch.IsFortniteRunning();

    public void Write(string context, Exception exception) => ErrorLog.Write(context, exception);
}
