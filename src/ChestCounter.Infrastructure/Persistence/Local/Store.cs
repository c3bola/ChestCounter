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

    public Config? LoadConfig() => Load<Config>(ConfigPath);
    public void SaveConfig(Config config) => Save(ConfigPath, config);
    public TrackerData LoadData() => Load<TrackerData>(DataPath) ?? new TrackerData { AnalyzerVersion = ReplayAnalyzer.CurrentVersion };
    public void SaveData(TrackerData data) => Save(DataPath, data);

    private static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
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
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
