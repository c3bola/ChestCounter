namespace ChestCounter;

public interface IMonitorStorage
{
    string Folder { get; }
    string DataPath { get; }
    string ReportPath { get; }
    string OverlayPath { get; }
    string CsvPath { get; }
    void SaveConfig(Config config);
    void SaveData(TrackerData data);
}

public interface IMonitorTracking
{
    bool HasPending { get; }
    bool IsFinishing { get; }
    bool IsLive { get; }
    MatchRecord? LiveMatch { get; }
    int Scan(bool ignoreQuietPeriod = false);
    bool UpdateLive();
    void Reset(string backupPath);
}

public interface IMonitorReporting
{
    DateTime SessionStartUtc { get; }
    void WriteAll(IMonitorStorage store, Config config, TrackerData data);
    void WriteOverlay(IMonitorStorage store, OverlayState state, int scale);
}

public interface IObsServer
{
    void Start(int port);
    void Stop();
    void UpdateScale(int scale);
}

public interface IUpdateService
{
    Task<UpdateInfo?> CheckAsync();
    string? Install(UpdateInfo update);
}

public interface IGameProcessMonitor
{
    bool Apply(bool enabled, string dataDir);
    bool IsFortniteRunning();
}

public interface IErrorLogger
{
    void Write(string context, Exception exception);
}

public interface IExecutableInfo
{
    string? ExePath { get; }
}

public sealed record MonitorDependencies(
    IMonitorReporting Reporting,
    IObsServer Obs,
    IUpdateService Updates,
    IGameProcessMonitor Game,
    IErrorLogger Errors,
    IExecutableInfo Executable);
