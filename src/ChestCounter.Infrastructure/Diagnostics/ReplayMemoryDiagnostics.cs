using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ChestCounter;

public static class ReplayMemoryDiagnostics
{
    private static readonly object Gate = new();
    private static string? _path;
    private static long _processed;

    public static void Initialize(string dataFolder)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CHESTCOUNTER_MEMORY_DIAGNOSTICS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        _path = Path.Combine(dataFolder, "memory-diagnostics.csv");
        Record("application.initialized");
    }

    public static void ReplayProcessed() => Interlocked.Increment(ref _processed);

    public static void Record(
        string stage,
        string? replayFile = null,
        TimeSpan? elapsed = null)
    {
        var path = _path;
        if (path == null)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var gc = GC.GetGCMemoryInfo();
        var generations = gc.GenerationInfo;
        var line = string.Join(
            ",",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Csv(stage),
            Csv(replayFile ?? ""),
            Volatile.Read(ref _processed).ToString(CultureInfo.InvariantCulture),
            (elapsed?.TotalMilliseconds ?? 0).ToString("0.###", CultureInfo.InvariantCulture),
            process.WorkingSet64.ToString(CultureInfo.InvariantCulture),
            process.PrivateMemorySize64.ToString(CultureInfo.InvariantCulture),
            gc.HeapSizeBytes.ToString(CultureInfo.InvariantCulture),
            GC.GetTotalAllocatedBytes(false).ToString(CultureInfo.InvariantCulture),
            GC.CollectionCount(0).ToString(CultureInfo.InvariantCulture),
            GC.CollectionCount(1).ToString(CultureInfo.InvariantCulture),
            GC.CollectionCount(2).ToString(CultureInfo.InvariantCulture),
            generations[0].SizeAfterBytes.ToString(CultureInfo.InvariantCulture),
            generations[1].SizeAfterBytes.ToString(CultureInfo.InvariantCulture),
            generations[2].SizeAfterBytes.ToString(CultureInfo.InvariantCulture),
            generations[^1].SizeAfterBytes.ToString(CultureInfo.InvariantCulture));

        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var needsHeader = !File.Exists(path);
            if (needsHeader)
            {
                File.AppendAllText(
                    path,
                    "timestamp_utc,stage,replay,replays_processed,elapsed_ms,working_set_bytes,private_bytes,gc_heap_bytes,total_allocated_bytes,gen0_collections,gen1_collections,gen2_collections,gen0_bytes,gen1_bytes,gen2_bytes,loh_bytes" + Environment.NewLine,
                    Encoding.UTF8);
            }
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static string Csv(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
