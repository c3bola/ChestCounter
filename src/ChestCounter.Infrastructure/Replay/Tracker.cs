using System.Text;
using System.Security.Cryptography;

namespace ChestCounter;

/// <summary>
/// Vigila la carpeta de replays de Fortnite, analiza una vez cada replay terminado y acumula los resultados.
/// </summary>
public sealed class Tracker : IMonitorTracking
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(10);

    /// <summary>Replay terminado por Fortnite (ya no en directo, con la clave real escrita): bastan unos segundos sin cambios.</summary>
    private static readonly TimeSpan FinalizedQuietPeriod = TimeSpan.FromSeconds(4);

    /// <summary>Un replay terminado sin cambios desde hace este tiempo está completo seguro (replays de antes).</summary>
    private static readonly TimeSpan OldReplayAge = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromMinutes(3);

    private readonly Store _store;
    private readonly Config _config;
    private readonly TrackerData _data;
    private readonly Dictionary<string, long> _lastSeenSize = new();

    public Tracker(Store store, Config config, TrackerData data)
    {
        _store = store;
        _config = config;
        _data = data;

        // Antes de la versión 1.1, las partidas de otras cuentas se rechazaban (valor antiguo "autre joueur"): ahora tienen su propio perfil.
        foreach (var key in _data.ProcessedFiles.Where(kv => kv.Value.StartsWith("autre joueur", StringComparison.Ordinal)).Select(kv => kv.Key).ToList())
        {
            _data.ProcessedFiles.Remove(key);
        }

        // Historial de versiones anteriores a la 2.0.9: nombres internos en francés -> nombres actuales.
        if (_data.UpgradeNames())
        {
            try { _store.SaveData(_data); } catch { /* se guardará con el próximo cambio */ }
        }
    }

    public TrackerData Data => _data;

    public event Action<MatchRecord, FileInfo>? MatchPersisted;

    /// <summary>True while a replay is waiting to be finished / to become stable.</summary>
    public bool HasPending { get; private set; }

    /// <summary>True when a match has just ended and its replay is about to be read: check again within seconds.</summary>
    public bool IsFinishing { get; private set; }

    /// <summary>Devuelve a Windows la memoria usada por el análisis (la herramienta en reposo ocupa unos pocos MB).</summary>
    private static void ReleaseMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    public static string FileKey(FileInfo f) => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";

    public IEnumerable<FileInfo> ReplayFiles()
    {
        var dir = new DirectoryInfo(_config.DemosFolder);
        if (!dir.Exists)
        {
            return Enumerable.Empty<FileInfo>();
        }
        return dir.EnumerateFiles("*.replay", SearchOption.TopDirectoryOnly).OrderBy(f => f.LastWriteTimeUtc);
    }

    public IEnumerable<FileInfo> UnprocessedFiles() => ReplayFiles().Where(f => !_data.ProcessedFiles.ContainsKey(FileKey(f)));

    /// <summary>Replays not analyzed yet and not being recorded (what the next scan will read).</summary>
    public int BacklogCount() => UnprocessedFiles().Count(f => ReplayHeaderProbe.IsLive(f.FullName) != true);

    /// <summary>
    /// Deletes the whole history (a copy of data.json is saved to <paramref name="backupPath"/> first) and marks
    /// the replays already in the folder as skipped, so that only the next matches are counted. The match being
    /// played, if any, still counts.
    /// </summary>
    public void Reset(string backupPath)
    {
        if (File.Exists(_store.DataPath))
        {
            File.Copy(_store.DataPath, backupPath, true);
        }
        _data.Matches.Clear();
        _data.ProcessedFiles.Clear();
        foreach (var f in ReplayFiles().Where(f => ReplayHeaderProbe.IsLive(f.FullName) != true))
        {
            _data.ProcessedFiles[FileKey(f)] = "skipped";
        }
        _store.SaveData(_data);
    }

    public void MarkSkipped(IEnumerable<FileInfo> files)
    {
        foreach (var f in files)
        {
            _data.ProcessedFiles[FileKey(f)] = "skipped";
        }
        _store.SaveData(_data);
    }

    /// <summary>Result of the analysis of one replay.</summary>
    private sealed record Outcome(MatchRecord? Match = null, bool Updated = false, string? Skipped = null, string? Error = null, List<string>? NewPrizeIds = null);

    /// <summary>
    /// One pass over the folder. Returns the number of new matches recorded. Several replays at once (replays left
    /// from earlier) are shown as a progress bar and a summary; a single one (end of a match) gets its own line.
    /// </summary>
    /// <param name="ignoreQuietPeriod">Process finished replays immediately (used by --once).</param>
    public int Scan(bool ignoreQuietPeriod = false)
    {
        HasPending = false;
        IsFinishing = false;
        var ready = new List<FileInfo>();
        foreach (var file in UnprocessedFiles().ToList())
        {
            file.Refresh();
            if (!file.Exists)
            {
                continue;
            }
            if (!IsReady(file, ignoreQuietPeriod))
            {
                HasPending = true;
                continue;
            }
            ready.Add(file);
        }
        if (ready.Count == 0)
        {
            return 0;
        }

        var batch = ready.Count >= 2;
        var profilesBefore = Profiles.List(_data).Count;
        var bar = batch ? new ProgressBar(L.T("tracker.analyzing_replays"), ready.Count) : null;
        var outcomes = new List<Outcome>();
        for (var i = 0; i < ready.Count; i++)
        {
            var file = ready[i];
            if (batch)
            {
                bar!.Update(i, file.Name);
                ReportWriter.Progress = new ScanProgress(i, ready.Count);
                ReportWriter.WriteAll(_store, _config, _data);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                var mb = (file.Length / 1024 / 1024.0).ToString("F1", L.Culture);
                var justEnded = DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromMinutes(2);
                Console.WriteLine(justEnded
                    ? L.F("tracker.match_over_analyzing_the_replay_mb", DateTime.Now, mb)
                    : L.F("tracker.analyzing_mb", DateTime.Now, file.Name, mb));
                Console.ResetColor();
            }

            var outcome = Process(file);
            outcomes.Add(outcome);
            if (!batch)
            {
                PrintOutcome(outcome);
            }
        }

        if (batch)
        {
            bar!.Update(ready.Count);
            bar.Clear();
            ReportWriter.Progress = null;
            PrintBatchSummary(outcomes);
            if (profilesBefore < 2 && Profiles.List(_data).Count >= 2)
            {
                NewProfilesFound = true;
            }
        }

        var added = outcomes.Count(o => o.Match != null);
        if (added > 0 || batch)
        {
            ReportWriter.WriteAll(_store, _config, _data);
        }
        return added;
    }

    /// <summary>Set when a scan found replays of a second account: the player should choose their profile.</summary>
    public bool NewProfilesFound { get; set; }

    private bool IsReady(FileInfo file, bool ignoreQuietPeriod)
    {
        var live = ReplayHeaderProbe.IsLive(file.FullName);
        if (live == true)
        {
            return false; // match still being recorded
        }

        var quietFor = DateTime.UtcNow - file.LastWriteTimeUtc;
        if (live == false && (ignoreQuietPeriod || quietFor > OldReplayAge))
        {
            return true;
        }

        // The file must not have changed for a while and its size must be stable between two scans.
        var stable = _lastSeenSize.TryGetValue(file.FullName, out var size) && size == file.Length;
        _lastSeenSize[file.FullName] = file.Length;

        if (live == null)
        {
            // Header could not be read: be extra careful.
            return stable && quietFor > TimeSpan.FromMinutes(2);
        }

        // Fortnite clears the "live" flag and writes the real encryption key when it stops recording: the replay
        // is complete. Read it as soon as its size has not changed for a few seconds (two checks in a row).
        if (!ReplayHeaderProbe.KeyMissing(file.FullName))
        {
            IsFinishing = true;
            return stable && quietFor >= FinalizedQuietPeriod;
        }
        return stable && quietFor > QuietPeriod;
    }

    private Outcome Process(FileInfo file)
    {
        var key = FileKey(file);
        MatchRecord record;
        try
        {
            record = Analyze(file, ParseTimeout);
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Unreadable replay: {file.FullName} ({file.Length} bytes, {file.LastWriteTime:yyyy-MM-dd HH:mm:ss})", ex);
            _data.ProcessedFiles[key] = "error";
            _store.SaveData(_data);
            return new Outcome(Error: file.Name);
        }

        record.ReplayFile = file.Name;

        if (!record.IsBattleRoyale)
        {
            // Also drop a record left by an older version of the tool for this replay.
            _data.Matches.RemoveAll(m => m.SessionId == record.SessionId);
            _data.ProcessedFiles[key] = "other mode";
            _store.SaveData(_data);
            return new Outcome(Skipped: L.T("tracker.not_a_battle_royale_match_creative_other_mode"));
        }

        if (!Profiles.IsSupportedVersion(record))
        {
            _data.Matches.RemoveAll(m => m.SessionId == record.SessionId);
            _data.ProcessedFiles[key] = "older version";
            _store.SaveData(_data);
            return new Outcome(Skipped: L.T("tracker.version_prior_to_v43_chapter_7_mini_season"));
        }

        var existing = _data.Matches.FirstOrDefault(m => m.SessionId == record.SessionId);
        if (existing != null && existing.AnalyzerVersion >= record.AnalyzerVersion)
        {
            _data.ProcessedFiles[key] = record.SessionId;
            _store.SaveData(_data);
            return new Outcome(Skipped: L.T("tracker.match_already_counted_same_match_renamed_file"));
        }

        if (existing != null)
        {
            // Re-analysis with a newer version of the tool: replace the old record.
            record.AnalyzedAtUtc = existing.AnalyzedAtUtc;
            _data.Matches.Remove(existing);
        }

        _data.Matches.Add(record);
        _data.ProcessedFiles[key] = record.SessionId;
        _store.SaveData(_data);
        try
        {
            MatchPersisted?.Invoke(record, file);
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Queue replay synchronization ({file.Name})", ex);
        }
        if (_liveFile == file.FullName)
        {
            ClearLive(); // the final analysis replaces the live estimate
        }

        // First match ever: its account becomes the profile.
        if (string.IsNullOrEmpty(_config.Profile))
        {
            _config.Profile = Profiles.Key(record);
            if (_config.InitialChests.Remove("", out var before) && !_config.InitialChests.ContainsKey(_config.Profile))
            {
                _config.InitialChests[_config.Profile] = before;
            }
            try { _store.SaveConfig(_config); } catch { /* ignore */ }
        }

        var newIds = _store.RegisterPrizeIds(record.AllTreasureFinds.Where(f => f.PrizeId != null).Select(f => f.PrizeId!));
        return new Outcome(record, Updated: existing != null, NewPrizeIds: newIds);
    }

    private void PrintOutcome(Outcome o)
    {
        if (o.Error != null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(L.F("tracker.cannot_read_this_replay", o.Error));
            Console.ResetColor();
            PrintErrorLogHint();
            return;
        }
        if (o.Skipped != null)
        {
            Console.WriteLine(L.F("tracker.skipped_line", L.T("tracker.skipped"), o.Skipped));
            return;
        }

        var m = o.Match!;
        PrintMatch(m, o.Updated);
        var current = Profiles.Current(_data, _config);
        if (current != null && Profiles.Key(m) != current.Key)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            var name = m.PlayerName ?? L.T("tracker.unknown_account");
            Console.WriteLine(L.F("tracker.match_played_on_another_account_it_counts_for", name, current.Name));
            Console.ResetColor();
        }
        PrintNewPrizeIds(o.NewPrizeIds);
    }

    /// <summary>Only real prize ids are worth asking about (unknown gizmos are listed in treasures.json anyway).</summary>
    private void PrintNewPrizeIds(List<string>? ids)
    {
        ids = ids?.Where(id => !id.StartsWith(TreasureNames.UnknownPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ids is { Count: > 0 })
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(L.F("tracker.treasure_s_to_name_in", _store.PrizeNamesPath, string.Join(", ", ids)));
            Console.ResetColor();
        }
    }

    private void PrintErrorLogHint()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(L.F("tracker.details_in_usually_caused_by_a_fortnite_update", _store.ErrorLogPath, AppInfo.IssuesUrl));
        Console.ResetColor();
    }

    private void PrintBatchSummary(List<Outcome> outcomes)
    {
        var matches = outcomes.Where(o => o.Match != null).Select(o => o.Match!).ToList();
        var skipped = outcomes.Count(o => o.Skipped != null);
        var errors = outcomes.Where(o => o.Error != null).Select(o => o.Error!).ToList();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"  ✔ {L.Count(outcomes.Count, "common.replay_analyzed", "common.replays_analyzed")}");
        Console.ResetColor();
        Console.Write(": ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{L.Count(matches.Count, "common.match", "common.matches")}, {L.Count(matches.Sum(m => m.ChestsOpened), "common.chest", "common.chests")}");
        var treasures = matches.Sum(m => m.TreasureFinds.Count);
        if (treasures > 0)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($", ★ {L.Count(treasures, "common.treasure", "common.treasures")}");
        }
        Console.ResetColor();
        Console.WriteLine();
        if (skipped > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(L.F("tracker.skipped_older_versions_creative_modes_or_matches", skipped));
            Console.ResetColor();
        }
        if (errors.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(L.F("tracker.unreadable_replay_s", errors.Count));
            foreach (var e in errors.Take(5))
            {
                Console.WriteLine($"      {e}");
            }
            Console.ResetColor();
            PrintErrorLogHint();
        }
        PrintNewPrizeIds(outcomes.Where(o => o.NewPrizeIds != null).SelectMany(o => o.NewPrizeIds!).ToList());
    }

    /// <summary>Reads the file (even while Fortnite writes it) and parses it on a lowest-priority background thread.</summary>
    private MatchRecord Analyze(FileInfo file, TimeSpan timeout)
    {
        byte[] bytes;
        long fileSize;
        DateTime fileCreatedAtUtc;
        using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            fileSize = fs.Length;
            file.Refresh();
            fileCreatedAtUtc = file.CreationTimeUtc;
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }

        MatchRecord? result = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                // The replays saved on a PC are those of its player: the replay owner is tracked (one profile per account).
                result = new ReplayAnalyzer().Analyze(ms, file.Name, "");
            }
            catch (Exception e)
            {
                failure = e;
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "ReplayAnalysis",
        };
        worker.Start();
        if (!worker.Join(timeout))
        {
            throw new TimeoutException(L.T("tracker.analysis_took_too_long"));
        }
        var fileSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        bytes = Array.Empty<byte>();
        ReleaseMemory();
        if (failure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); // keep the original stack
        }
        result!.ReplayFileSha256 = fileSha256;
        result.ReplayFileSizeBytes = fileSize;
        result.ReplayFileCreatedAtUtc = fileCreatedAtUtc;
        result.AnalysisApplicationVersion = AppInfo.VersionText;
        result.ParserVersion = typeof(FortniteReplayReader.ReplayReader).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        return result;
    }

    // ---- Live match ---------------------------------------------------------------------------
    private string? _liveFile;
    private long _liveSize = -1;
    private DateTime _liveParsedUtc = DateTime.MinValue;

    /// <summary>Estimate of the match being played (replay still recorded), or null. Not saved.</summary>
    public MatchRecord? LiveMatch { get; private set; }

    /// <summary>True while a replay is being recorded (a match is in progress).</summary>
    public bool IsLive => _liveFile != null;

    private void ClearLive()
    {
        _liveFile = null;
        _liveSize = -1;
        LiveMatch = null;
    }

    /// <summary>
    /// Re-reads the replay being recorded when Fortnite has appended data to it (about every 25 s), at most once
    /// every <see cref="Config.LiveIntervalSeconds"/>. Returns true when the live figures changed.
    /// </summary>
    public bool UpdateLive()
    {
        // The newest replay still flagged "live" and written recently (an old one left by a crash is ignored).
        var file = UnprocessedFiles()
            .Where(f => DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromMinutes(3))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault(f => ReplayHeaderProbe.IsLive(f.FullName) == true);

        if (file == null)
        {
            var had = _liveFile != null;
            // Match over: keep the estimate on screen until the final analysis replaces it, unless it went stale.
            if (had && (!File.Exists(_liveFile) || DateTime.UtcNow - File.GetLastWriteTimeUtc(_liveFile!) > TimeSpan.FromMinutes(5)))
            {
                ClearLive();
                return true;
            }
            return false;
        }

        var started = false;
        if (file.FullName != _liveFile)
        {
            ClearLive();
            _liveFile = file.FullName;
            started = true;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(L.F("tracker.match_in_progress_its_chests_will_be_counted", DateTime.Now));
            Console.ResetColor();
        }

        // Fortnite writes the encryption key only at the end of the match: until then the data cannot be read,
        // so we only know that a match is in progress (no useless re-reading).
        if (!_config.LiveUpdate || ReplayHeaderProbe.KeyMissing(file.FullName))
        {
            return started;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, _config.LiveIntervalSeconds));
        if (file.Length == _liveSize || DateTime.UtcNow - _liveParsedUtc < interval)
        {
            return started;
        }

        MatchRecord record;
        try
        {
            record = Analyze(file, TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            _liveParsedUtc = DateTime.UtcNow; // try again at the next interval
            if (Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG") is { Length: > 0 })
            {
                Console.Error.WriteLine($"DBG live parse failed: {ex}");
            }
            return started;
        }
        _liveSize = file.Length;
        _liveParsedUtc = DateTime.UtcNow;

        var before = LiveMatch;
        LiveMatch = record;
        var changed = before?.ChestsOpened != LiveMatch?.ChestsOpened
            || before?.AllTreasureFinds.Count() != LiveMatch?.AllTreasureFinds.Count()
            || before?.PirateTokenSeen != LiveMatch?.PirateTokenSeen
            || (before == null) != (LiveMatch == null);
        if (changed && LiveMatch != null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var liveTreasures = LiveMatch.AllTreasureFinds.Count();
            var message = liveTreasures > 0
                ? L.F("tracker.live_summary_treasures", DateTime.Now,
                    L.Count(LiveMatch.ChestsOpened, "common.chest", "common.chests"),
                    L.Count(liveTreasures, "common.treasure", "common.treasures"))
                : L.F("tracker.live_summary", DateTime.Now,
                    L.Count(LiveMatch.ChestsOpened, "common.chest", "common.chests"));
            Console.WriteLine(message);
            Console.ResetColor();
        }
        return changed || started;
    }

    /// <summary>
    /// When the analysis logic changed, re-analyze the replays still on disk (records are replaced, not duplicated).
    /// Must run before any scan. Returns true when existing matches will be re-analyzed (worth telling the player).
    /// </summary>
    public bool UpgradeIfNeeded()
    {
        if (_data.AnalyzerVersion >= ReplayAnalyzer.CurrentVersion)
        {
            return false;
        }

        var hadData = _data.Matches.Count > 0;
        foreach (var key in _data.ProcessedFiles.Where(kv => kv.Value != "skipped" && kv.Value != "other mode" && kv.Value != "older version").Select(kv => kv.Key).ToList())
        {
            _data.ProcessedFiles.Remove(key);
        }
        _data.AnalyzerVersion = ReplayAnalyzer.CurrentVersion;
        _store.SaveData(_data);
        return hadData;
    }

    private void PrintMatch(MatchRecord m, bool updated)
    {
        m = Profiles.Adjust(m, _config)!;
        var total = _data.Matches.Where(x => Profiles.Key(x) == Profiles.Key(m) && Profiles.Counted(x, _config)).Sum(x => Profiles.Adjust(x, _config)!.ChestsOpened) + Profiles.InitialChests(_config, Profiles.Key(m));
        var when = (m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"   {(updated ? "↻" : "✔")} {L.F("tracker.match_date", L.DateTimeShort(when))}");
        Console.ResetColor();
        Console.Write($" ({Pretty.Playlist(m.Playlist)}, {Pretty.Placement(m)}): ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        var destroyed = m.Chests.Count(c => c.Destroyed);
        Console.Write(L.Count(m.ChestsOpened, "common.chest", "common.chests")
            + (destroyed > 0 ? L.F("tracker.destroyed", destroyed) : ""));
        Console.ResetColor();
        Console.WriteLine(L.F("tracker.total", L.N(total)));

        foreach (var find in m.TreasureFinds)
        {
            Console.ForegroundColor = find.FromGoldenChest ? ConsoleColor.Yellow : ConsoleColor.Cyan;
            Console.WriteLine(L.F("tracker.treasure_obtained_line", find.DisplayName, Pretty.Obtained(find)));
            if (!find.SourceIsChest)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(L.F("tracker.unusual_source_noted_in_the_report", find.SourceLabel,
                    find.SourceCertain ? "" : L.T("tracker.likely"), find.SourceBlueprint));
            }
            Console.ResetColor();
        }
    }

    /// <summary>Totals of the profile shown.</summary>
    public void PrintSummary()
    {
        var profile = Profiles.Current(_data, _config);
        var matches = Profiles.View(_data, _config).Matches;
        var counted = matches.Sum(m => m.ChestsOpened);
        var chests = counted + Profiles.InitialChests(_data, _config);
        var treasures = matches.Sum(m => m.TreasureFinds.Count);
        var sb = new StringBuilder();
        if (profile != null)
        {
            sb.Append($"{profile.Name}   |   ");
        }
        sb.Append(L.F("tracker.matches_chests_treasures", L.N(matches.Count), L.N(chests), L.N(treasures)));
        if (matches.Count > 0)
        {
            var avg = ((double)counted / matches.Count).ToString("F1", L.Culture);
            sb.Append(L.F("tracker.average_match", avg));
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  " + sb);
        Console.ResetColor();
    }
}

/// <summary>Reads just enough of the replay file header to know whether the match is still being recorded.</summary>
public static class ReplayHeaderProbe
{
    private const uint FileMagic = 0x1CA2E27F;

    /// <summary>
    /// True when the replay is encrypted and its key is not written yet. Fortnite writes the real key only when
    /// the match ends (all zeros before): the data of a match in progress cannot be read.
    /// </summary>
    public static bool KeyMissing(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != FileMagic) return true;
            var fileVersion = br.ReadUInt32();
            if (fileVersion >= 7)
            {
                var count = br.ReadInt32();
                if (count < 0 || count > 64) return true;
                fs.Seek(count * 20L, SeekOrigin.Current);
            }
            fs.Seek(12, SeekOrigin.Current);
            var len = br.ReadInt32();
            fs.Seek(len < 0 ? -2L * len : len, SeekOrigin.Current);
            br.ReadUInt32(); // bIsLive
            br.ReadInt64(); // timestamp
            br.ReadUInt32(); // compressed
            if (br.ReadUInt32() == 0) return false; // not encrypted
            var keyLen = br.ReadInt32();
            if (keyLen <= 0 || keyLen > 256) return true;
            return br.ReadBytes(keyLen).All(b => b == 0);
        }
        catch
        {
            return true;
        }
    }

    public static bool? IsLive(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != FileMagic)
            {
                return null;
            }

            var fileVersion = br.ReadUInt32();
            if (fileVersion >= 7)
            {
                var count = br.ReadInt32();
                if (count < 0 || count > 64)
                {
                    return null;
                }
                fs.Seek(count * 20L, SeekOrigin.Current);
            }

            br.ReadUInt32(); // LengthInMS
            br.ReadUInt32(); // NetworkVersion
            br.ReadUInt32(); // Changelist

            var len = br.ReadInt32(); // FriendlyName (FString)
            if (len < 0)
            {
                fs.Seek(-2L * len, SeekOrigin.Current);
            }
            else
            {
                fs.Seek(len, SeekOrigin.Current);
            }

            return br.ReadUInt32() != 0;
        }
        catch
        {
            return null;
        }
    }
}
