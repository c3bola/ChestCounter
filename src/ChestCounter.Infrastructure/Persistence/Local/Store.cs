using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChestCounter;

/// <summary>Reads/writes JSON files atomically in the data folder.</summary>
public sealed class Store : IMonitorStorage
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Folder { get; }

    /// <summary>
    /// %APPDATA%\ChestCounter. The tool was called ChestTracker before version 1.1: its folder is moved
    /// (history, settings and treasure names are kept).
    /// </summary>
    public static string DefaultFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "ChestCounter");
        var old = Path.Combine(appData, "ChestTracker");
        try
        {
            if (!Directory.Exists(folder) && File.Exists(Path.Combine(old, "data.json")))
            {
                Directory.Move(old, folder);
            }
        }
        catch
        {
            // the old folder is in use (old version still running): start fresh, nothing is lost
        }
        return folder;
    }

    public string ConfigPath => Path.Combine(Folder, "config.json");
    public string DataPath => Path.Combine(Folder, "data.json");
    public string ReportPath => Path.Combine(Folder, "report.html");
    public string OverlayPath => Path.Combine(Folder, "overlay.html");
    public string CsvPath => Path.Combine(Folder, "matches.csv");
    public string PrizeNamesPath => Path.Combine(Folder, "treasures.json");
    public string ErrorLogPath => Path.Combine(Folder, "errors.log");
    public string FailedReplaysPath => Path.Combine(Folder, "failed-replays.json");

    /// <summary>Known prizes from the treasure hunt. New ids are added with an empty name.</summary>
    public static Dictionary<string, string> DefaultPrizeNames => L.Code != "es"
        ? new()
        {
            ["DelilahPrize:dpid_item1"] = "A$AP Rocky solid gold gauntlets (pickaxe) ?",
            ["DelilahPrize:dpid_item2"] = "Plunder in Paradise (loading screen)",
            ["DelilahPrize:dpid_item3"] = "Mercedes-AMG G 63 (vehicle) ?",
            ["DelilahPrize:dpid_item4"] = "No Trespassing (music track)",
        }
        : new()
        {
            ["DelilahPrize:dpid_item1"] = "Guanteletes de A$AP Rocky de oro macizo (pico) ?",
            ["DelilahPrize:dpid_item2"] = "Plunder in Paradise (pantalla de carga)",
            ["DelilahPrize:dpid_item3"] = "Mercedes-AMG G 63 (vehículo) ?",
            ["DelilahPrize:dpid_item4"] = "No Trespassing (pista musical)",
        };

    public void LoadPrizeNames()
    {
        var names = Load<Dictionary<string, string>>(PrizeNamesPath);
        if (names == null)
        {
            names = DefaultPrizeNames;
            Save(PrizeNamesPath, names);
        }

        TreasureNames.PrizeNames.Clear();
        foreach (var kv in names)
        {
            TreasureNames.PrizeNames[kv.Key] = kv.Value;
        }
    }

    /// <summary>Adds unknown prize ids to treasures.json so the user can name them. Returns the new ids.</summary>
    public List<string> RegisterPrizeIds(IEnumerable<string> ids)
    {
        var added = ids.Where(id => !TreasureNames.PrizeNames.ContainsKey(id)).Distinct().ToList();
        if (added.Count == 0)
        {
            return added;
        }

        var names = Load<Dictionary<string, string>>(PrizeNamesPath) ?? DefaultPrizeNames;
        foreach (var id in added)
        {
            names[id] = "";
            TreasureNames.PrizeNames[id] = "";
        }
        Save(PrizeNamesPath, names);
        return added;
    }

    public Store(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        MigrateFileNames();
    }

    /// <summary>Versions before 1.1 used other file names; keep user treasure names.</summary>
    private void MigrateFileNames()
    {
        try
        {
            var oldPrizes = Path.Combine(Folder, "tresors.json");
            if (File.Exists(oldPrizes) && !File.Exists(PrizeNamesPath))
            {
                File.Move(oldPrizes, PrizeNamesPath);
            }
            foreach (var generated in new[] { "rapport.html", "parties.csv" })
            {
                var path = Path.Combine(Folder, generated);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        catch
        {
            // Old generated files can be left in place and regenerated later.
        }
    }

    public Config? LoadConfig()
    {
        var config = Load<Config>(ConfigPath);
        if (config != null)
        {
            config.FortCardsPlayerIdentities ??= new Dictionary<string, FortCardsPlayerIdentity>(StringComparer.OrdinalIgnoreCase);
        }
        return config;
    }
    public void SaveConfig(Config config) => Save(ConfigPath, config);
    public TrackerData LoadData() => Load<TrackerData>(DataPath) ?? new TrackerData { AnalyzerVersion = ReplayAnalyzer.CurrentVersion };
    public void SaveData(TrackerData data) => Save(DataPath, data);

    public List<FailedReplay> LoadFailedReplays() =>
        Load<List<FailedReplay>>(FailedReplaysPath) ?? new List<FailedReplay>();

    public void SaveFailedReplays(List<FailedReplay> failures) =>
        Save(FailedReplaysPath, failures);

    /// <summary>
    /// Removes RawReplayData only for replay hashes that have a confirmed synchronized queue entry.
    /// Creates a recoverable backup and is safe to run repeatedly.
    /// </summary>
    public int MigrateConfirmedRawReplayData(TrackerData data, IReadOnlyCollection<string> synchronizedHashes)
    {
        if (synchronizedHashes.Count == 0)
        {
            return 0;
        }

        var hashes = synchronizedHashes.Where(hash => hash.Length == 64)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var migrated = data.Matches.Where(match =>
            match.RawReplayData != null
            && match.ReplayFileSha256 != null
            && hashes.Contains(match.ReplayFileSha256)).ToList();
        if (migrated.Count == 0)
        {
            return 0;
        }

        if (File.Exists(DataPath))
        {
            var backup = DataPath + $".pre-raw-migration-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(DataPath, backup, overwrite: true);
        }

        foreach (var match in migrated)
        {
            match.IsSynced = true;
            match.RawReplayData = null;
        }
        SaveData(data);
        return migrated.Count;
    }

    /// <summary>
    /// Removes raw snapshots from match history after they have been durably copied to the API outbox.
    /// Pending snapshots remain available for retry in the outbox until synchronization is confirmed.
    /// </summary>
    public int MigrateQueuedRawReplayData(TrackerData data, IReadOnlyCollection<string> queuedHashes)
    {
        if (queuedHashes.Count == 0)
        {
            return 0;
        }

        var hashes = queuedHashes.Where(hash => hash.Length == 64)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var migrated = data.Matches.Where(match =>
            match.RawReplayData != null
            && match.ReplayFileSha256 != null
            && hashes.Contains(match.ReplayFileSha256)).ToList();
        if (migrated.Count == 0)
        {
            return 0;
        }

        foreach (var match in migrated)
        {
            match.RawReplayData = null;
        }
        SaveData(data);
        return migrated.Count;
    }

    private static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<T>(stream, Json);
        }
        catch (Exception ex)
        {
            var backup = path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, overwrite: true);
            ErrorLog.Write($"Archivo ilegible: {path}", ex);
            return null;
        }
    }

    private static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Json);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
