using System.Runtime.Versioning;

namespace ChestCounter;

public enum LocalReplayPhase
{
    Identifying,
    Processing,
    Waiting,
    Completed,
    Failed,
}

public sealed record LocalReplayProgress(
    LocalReplayPhase Phase,
    int BatchNumber,
    int Processed,
    int Total,
    int UpdatedRecords,
    int Failed,
    int Skipped,
    int Waiting,
    string? CurrentReplay)
{
    public int Remaining => Math.Max(0, Total - Processed);
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Processed * 100.0 / Total, 0, 100);

    public static LocalReplayProgress Initial { get; } =
        new(LocalReplayPhase.Identifying, 0, 0, 0, 0, 0, 0, 0, null);
}

/// <summary>Lo que muestra la ventana.</summary>
public sealed record MonitorState(OverlayState Overlay, IReadOnlyList<Profile> Profiles, string? CurrentKey, UpdateInfo? Update, string? Notice, bool AutoCloseOffer, ChestStats Stats)
{
    public string? CurrentEpicId { get; init; }
    public string? CurrentPlayerName { get; init; }
    /// <summary>Equipo de la partida en curso o de la última (null si «Compañeros» está desactivado).</summary>
    public TeamSummary? Team { get; init; }
    public bool PirateTokenLive { get; init; }
    public LocalReplayProgress LocalProgress { get; init; } = LocalReplayProgress.Initial;
    public bool LocalPriorityActive { get; init; } = true;
}

/// <summary>Un compañero y los cofres que se le vieron abrir (un mínimo).</summary>
public sealed record TeamMember(string Name, int Chests);

/// <summary>
/// Equipo de una partida para la ventana principal.
/// <paramref name="Unknown"/>: partida analizada antes de guardar compañeros (sin datos de equipo).
/// </summary>
public sealed record TeamSummary(bool Live, int Mine, IReadOnlyList<TeamMember> Mates, bool Unknown)
{
    public int MatesTotal => Mates.Sum(m => m.Chests);
    public int Total => Mine + MatesTotal;
}

/// <summary>Estadísticas del perfil mostrado (la partida en curso cuenta ya en hoy y en la mejor partida).</summary>
/// <param name="Today">Cofres de hoy.</param>
/// <param name="BestDay">Día con más cofres, o null si no hay partidas.</param>
/// <param name="BestDayChests">Cofres de ese día.</param>
/// <param name="Average">Media de cofres por partida terminada.</param>
/// <param name="BestMatch">Cofres de la mejor partida.</param>
public sealed record ChestStats(int Matches, double Average, int BestMatch, int Treasures, int Today, DateTime? BestDay, int BestDayChests)
{
    public static readonly ChestStats Empty = new(0, 0, 0, 0, 0, null, 0);

    /// <summary>Cofres de los últimos 7 días, del más antiguo al de hoy (incluye la partida en curso).</summary>
    public IReadOnlyList<DayChests> LastDays { get; init; } = Array.Empty<DayChests>();

    /// <summary>Partidas guardadas en el histórico (todas, no solo las 100 últimas; sobreviven aunque Fortnite borre el replay).</summary>
    public int HistoryMatches { get; init; }

    /// <summary>Fecha local de la primera partida del histórico, o null si no hay ninguna.</summary>
    public DateTime? HistorySince { get; init; }

    /// <summary>Resumen de la última partida terminada del perfil mostrado, o null si no hay ninguna.</summary>
    public LastMatchSummary? LastMatch { get; init; }
}

/// <summary>Cifras de la última partida: cofres abiertos (incluidos los destruidos), tesoros, puesto y eliminaciones.</summary>
public sealed record LastMatchSummary(int Chests, int Destroyed, int Treasures, int? Placement, int? Players, int? Eliminations, bool PirateToken = false);

/// <summary>Cofres de un día concreto.</summary>
public sealed record DayChests(DateTime Day, int Chests);

/// <summary>
/// Vigila los replays en segundo plano: escaneo, partida en curso, overlay, actualizaciones y cierre automático.
/// Solo el hilo de vigilancia toca los datos (el escaneo puede tardar minutos); la ventana pide cambios y
/// recibe el estado publicado, así nunca se congela mientras se analizan replays.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Monitor
{
    /// <summary>Protege solo la configuración (escrituras cortas desde la ventana, el overlay y el hilo de vigilancia).</summary>
    private readonly object _cfgGate = new();
    private readonly object _localWorkGate = new();
    private readonly IMonitorStorage _store;
    private readonly Config _config;
    private readonly TrackerData _data;
    private readonly IMonitorTracking _tracker;
    private readonly IOverlayHost _overlayHost;
    private readonly MonitorDependencies _services;
    private readonly string _dataDir;
    private readonly AutoResetEvent _wake = new(false);
    private FileSystemWatcher? _watcher;
    private volatile bool _stop;
    private volatile bool _publishRequested;
    private volatile OverlayState? _latestOverlay;
    private volatile bool _reportRequested;
    private volatile string? _shownKey;
    private volatile string? _shownName;
    private DateTime _nextScan = DateTime.UtcNow;
    private volatile UpdateInfo? _update;
    private bool _updateAnnounced;
    private volatile bool _installing;
    private volatile string? _notice;
    private volatile bool _keepOpenRequested;
    private DateTime? _fortniteClosedSince;
    private DateTime? _autoCloseAt;
    private bool _closeRequested;
    private readonly object _localProgressGate = new();
    private LocalReplayProgress _localProgress = LocalReplayProgress.Initial;
    private bool _localWorkPending = true;
    private long _localWorkGeneration;

    public Monitor(IMonitorStorage store, Config config, TrackerData data, IMonitorTracking tracker, string dataDir, IOverlayHost overlayHost, MonitorDependencies services)
    {
        _store = store;
        _config = config;
        _data = data;
        _tracker = tracker;
        _dataDir = dataDir;
        _overlayHost = overlayHost;
        _services = services;
        _tracker.LocalProgressChanged += OnLocalProgressChanged;
    }

    /// <summary>Ajustes actuales (leer; los cambios se hacen con <see cref="Change"/>).</summary>
    public Config Config => _config;
    public OverlayState? LatestOverlay => _latestOverlay;

    /// <summary>Abierto por el proceso en espera de Fortnite: se cierra solo cuando Fortnite se cierra.</summary>
    public bool AutoMode { get; set; }

    /// <summary>Se pone a true cuando hay que reiniciar la nueva versión.</summary>
    public bool RestartAfterUpdate { get; private set; }

    /// <summary>Reinicio pedido desde Ajustes (cambio de idioma): la ventana se cierra y el exe vuelve a arrancar.</summary>
    public bool RestartRequested { get; private set; }

    /// <summary>Cierra el contador para volver a abrirlo (los textos se leen al arrancar).</summary>
    public void Restart()
    {
        RestartRequested = true;
        CloseRequested?.Invoke();
    }

    /// <summary>Estado nuevo. Se llama desde el hilo de vigilancia: la ventana debe usar BeginInvoke.</summary>
    public event Action<MonitorState>? Changed;

    /// <summary>Indica al sincronizador cuándo debe pausar o reanudar el envío remoto.</summary>
    public event Action<bool>? LocalProcessingChanged;

    /// <summary>Pide cerrar la ventana (Fortnite cerrado, o nueva versión instalada). Desde otro hilo.</summary>
    public event Action? CloseRequested;

    public void Start()
    {
        if (_config.OverlayScalePercent != OverlayScale.Clamp(_config.OverlayScalePercent))
        {
            _services.Errors.Write("Overlay scale", new ArgumentOutOfRangeException(nameof(_config.OverlayScalePercent)));
            _config.OverlayScalePercent = OverlayScale.Clamp(_config.OverlayScalePercent);
        }
        if (OperatingSystem.IsWindows())
        {
            StartOverlay();
        }
        RestartWatcher();
        _services.Obs.Start(_config.ObsPort > 0 ? _config.ObsPort : Config.DefaultObsPort);
        _services.Obs.UpdateScale(_config.OverlayScalePercent);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _latestOverlay = BuildOverlay();
                _overlayHost.Update(_latestOverlay);
            }
            catch (Exception ex)
            {
                _services.Errors.Write("Initial overlay", ex);
            }
        }

        new Thread(Loop) { IsBackground = true, Name = "Vigilancia" }.Start();

        // Nueva versión en GitHub: única conexión de red de la herramienta (se puede desactivar en ajustes).
        if (_config.UpdateMode != "off")
        {
            _ = Task.Run(async () =>
            {
                var found = await _services.Updates.CheckAsync();
                if (found != null)
                {
                    _update = found;
                    RequestPublish();
                }
            });
        }
    }

    public void Stop()
    {
        ResetRequest? pendingReset;
        lock (_cfgGate)
        {
            _stop = true;
            pendingReset = _resetRequest;
            _resetRequest = null;
        }
        pendingReset?.Completion.TrySetCanceled();
        _wake.Set();
        _services.Obs.Stop();
        _overlayHost.Stop();
        _watcher?.Dispose();
        _tracker.LocalProgressChanged -= OnLocalProgressChanged;
    }

    /// <summary>Mensaje de estado de la ventana (por ejemplo, el reanálisis tras una actualización).</summary>
    public void SetNotice(string? text)
    {
        _notice = text;
        RequestPublish();
    }

    /// <summary>Cambia un ajuste y lo guarda. La ventana se actualiza enseguida.</summary>
    public void Change(Action<Config> change)
    {
        lock (_cfgGate)
        {
            change(_config);
            SaveQuietly();
        }
        RequestPublish();
    }

    public void SetProfile(string key)
    {
        Change(c =>
        {
            c.Profile = key;
            _reportRequested = true;
        });
    }

    /// <summary>Sumar o no al total las cajas de munición, de comida o los cañones de botín. Rehace el informe enseguida.</summary>
    public void SetCountExtra(Action<Config> change)
    {
        _reportRequested = true;
        Change(change);
    }

    /// <summary>Sumar o no a tu total los cofres vistos de tus compañeros. Rehace el informe enseguida.</summary>
    public void SetCountTeammates(bool count)
    {
        _reportRequested = true;
        Change(c => c.CountTeammateChests = count);
    }

    public void SetCountTeammateTreasures(bool count)
    {
        _reportRequested = true;
        Change(c => c.CountTeammateTreasures = count);
    }

    /// <summary>Contar solo desde una fecha (null = todas las partidas). Rehace el informe enseguida.</summary>
    public void SetCountFrom(DateTime? from)
    {
        _reportRequested = true;
        Change(c => c.CountFrom = from?.Date);
    }

    /// <summary>
    /// Días con partidas del perfil mostrado (todas, sin el filtro de fecha), del más reciente al más antiguo, con
    /// cuántas partidas hubo: es lo que ofrece el selector «Contar desde».
    /// </summary>
    public IReadOnlyList<(DateTime Day, int Matches)> MatchDays()
    {
        try
        {
            var key = Profiles.Current(_data, _config)?.Key;
            return _data.Matches.ToList()
                .Where(m => (key == null || Profiles.Key(m) == key)
                    && Profiles.IsSupportedVersion(m)
                    && Profiles.IsBattleRoyalePlaylist(m.Playlist))
                .GroupBy(Profiles.Day)
                .OrderByDescending(g => g.Key)
                .Select(g => (g.Key, g.Count()))
                .ToList();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<(DateTime, int)>(); // la lista cambió mientras se leía: sin días esta vez
        }
    }

    /// <summary>Mostrar u ocultar la sección «Compañeros» del informe. Rehace el informe enseguida.</summary>
    public void SetShowTeammates(bool show)
    {
        _reportRequested = true;
        Change(c => c.ShowTeammates = show);
    }

    private sealed record ResetRequest(string Backup, TaskCompletionSource<string> Completion);
    private ResetRequest? _resetRequest;
    private bool _resetInProgress;

    /// <summary>
    /// Restablece las estadísticas: borra todo el historial (y los cofres de antes del contador) y empieza de cero.
    /// Los replays que ya están en la carpeta no se vuelven a contar. Antes se guarda una copia de data.json.
    /// La tarea devuelve la ruta de esa copia cuando termina el trabajo en el hilo de vigilancia.
    /// </summary>
    public Task<string> ResetStatsAsync()
    {
        var backup = Path.Combine(_store.Folder, $"data-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cfgGate)
        {
            if (_stop)
            {
                completion.SetCanceled();
                return completion.Task;
            }
            if (_resetRequest != null || _resetInProgress)
            {
                throw new InvalidOperationException("A statistics reset is already pending.");
            }
            _resetRequest = new ResetRequest(backup, completion);
        }
        _wake.Set();
        return completion.Task;
    }

    /// <summary>Nombre del perfil mostrado, o null si aún no hay partidas.</summary>
    public string? ShownProfileName => _shownName;

    public FortCardsPlayerIdentity? GetFortCardsPlayerIdentity(string identityKey)
    {
        lock (_cfgGate)
        {
            return _config.FortCardsPlayerIdentities.TryGetValue(identityKey, out var identity)
                ? identity
                : null;
        }
    }

    public void SetFortCardsPlayerIdentity(string identityKey, long playerId)
    {
        if (string.IsNullOrWhiteSpace(identityKey) || playerId <= 0)
        {
            throw new ArgumentException("A profile identity key and positive FortCards player ID are required.");
        }

        Change(config => config.FortCardsPlayerIdentities[identityKey] =
            new FortCardsPlayerIdentity(playerId, DateTimeOffset.UtcNow));
    }

    public void RemoveFortCardsPlayerIdentity(string identityKey)
    {
        Change(config => config.FortCardsPlayerIdentities.Remove(identityKey));
    }

    /// <summary>Cofres estimados de antes de usar el contador, del perfil mostrado.</summary>
    public int InitialChests
    {
        get
        {
            lock (_cfgGate)
            {
                return Profiles.InitialChests(_config, _shownKey);
            }
        }
    }

    /// <summary>Guarda el estimado de cofres de antes para el perfil mostrado y rehace el informe.</summary>
    public void SetInitialChests(int count)
    {
        var key = _shownKey ?? "";
        Change(c =>
        {
            if (count > 0)
            {
                c.InitialChests[key] = count;
            }
            else
            {
                c.InitialChests.Remove(key);
            }
        });
        _reportRequested = true;
        _wake.Set();
    }

    /// <summary>
    /// Regenera los informes locales al arrancar. El Dashboard abre el perfil remoto cuando se resuelve su identidad.
    /// </summary>
    private void WriteReportAtStart()
    {
        _services.Reporting.WriteAll(_store, _config, _data);
    }

    public void ChangeDemosFolder(string folder)
    {
        Change(c => c.DemosFolder = folder);
        RestartWatcher();
    }

    /// <summary>Abrir con Fortnite: crea o quita la entrada de inicio. Devuelve false si no se puede.</summary>
    public bool SetStartWithFortnite(bool on)
    {
        lock (_cfgGate)
        {
            if (!OperatingSystem.IsWindows() || !_services.Game.Apply(on, _dataDir))
            {
                return false;
            }
            _config.StartWithFortnite = on;
            SaveQuietly();
        }
        RequestPublish();
        return true;
    }

    /// <summary>El jugador decide mantener el contador abierto tras cerrar Fortnite.</summary>
    public void KeepOpen()
    {
        AutoMode = false;
        _keepOpenRequested = true;
        RequestPublish();
    }

    /// <summary>El overlay siempre está disponible: el botón y Ctrl+Mayús+O lo muestran y ocultan.</summary>
    public bool OverlayEnabled => OperatingSystem.IsWindows();

    /// <summary>True cuando el overlay está en pantalla (desactivado por defecto).</summary>
    public bool OverlayVisible => OperatingSystem.IsWindows() && _overlayHost.IsVisible;

    public void ToggleOverlay()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        if (_overlayHost.IsRunning)
        {
            _overlayHost.Toggle();
            return;
        }
        lock (_cfgGate)
        {
            _config.OverlayVisible = true;
            SaveQuietly();
        }
        StartOverlay();
        RequestPublish();
    }

    public void ToggleOverlayLock()
    {
        if (_overlayHost.IsRunning) _overlayHost.ToggleLock();
    }

    public bool SetOverlayScale(int value)
    {
        var scale = OverlayScale.Clamp(value);
        lock (_cfgGate)
        {
            var previous = _config.OverlayScalePercent;
            _config.OverlayScalePercent = scale;
            try
            {
                _store.SaveConfig(_config);
            }
            catch (Exception ex)
            {
                _config.OverlayScalePercent = previous;
                _services.Errors.Write("Save overlay scale", ex);
                _overlayHost.SetScale(previous);
                return false;
            }
            _overlayHost.SetScale(scale);
            _services.Obs.UpdateScale(scale);
        }
        RequestPublish();
        return true;
    }

    /// <summary>Descarga e instala la nueva versión en segundo plano; luego pide reiniciar.</summary>
    public void InstallUpdate()
    {
        var u = _update;
        if (u == null || _installing)
        {
            return;
        }
        _installing = true;
        RequestPublish();
        _ = Task.Run(() =>
        {
            var error = _services.Updates.Install(u);
            _installing = false;
            if (error == null)
            {
                RestartAfterUpdate = true;
                CloseRequested?.Invoke();
                return;
            }
            _notice = error;
            RequestPublish();
        });
    }

    /// <summary>Pide al hilo de vigilancia que publique el estado (se hace en cuanto puede).</summary>
    public void RequestPublish()
    {
        _publishRequested = true;
        _wake.Set();
    }

    // ---- Hilo de vigilancia ----------------------------------------------------------------------

    private void Loop()
    {
        WriteReportAtStart();
        var first = true;
        while (!_stop)
        {
            var changed = first || _publishRequested;
            first = false;
            _publishRequested = false;
            var localScanCompleted = false;
            var localScanStarted = false;
            var localWorkGeneration = 0L;

            ResetRequest? reset;
            lock (_cfgGate)
            {
                reset = _resetRequest;
                _resetRequest = null;
                _resetInProgress = reset != null;
            }
            if (reset != null)
            {
                try
                {
                    if (!File.Exists(_store.DataPath))
                    {
                        _store.SaveData(_data);
                    }
                    _tracker.Reset(reset.Backup);
                    _reportRequested = true;
                    lock (_cfgGate)
                    {
                        _config.InitialChests.Clear();
                        _config.Profile = null;
                        _store.SaveConfig(_config);
                    }
                    changed = true;
                    reset.Completion.TrySetResult(reset.Backup);
                }
                catch (Exception ex)
                {
                    _services.Errors.Write("Restablecer estadísticas", ex);
                    _notice = L.T("error.reset_failed");
                    changed = true;
                    reset.Completion.TrySetException(ex);
                }
                finally
                {
                    lock (_cfgGate)
                    {
                        _resetInProgress = false;
                    }
                }
            }

            try
            {
                if (DateTime.UtcNow >= _nextScan)
                {
                    var priorityChanged = SetLocalWorkPending(true);
                    if (priorityChanged)
                    {
                        PublishProgressSafely();
                    }
                    localWorkGeneration = GetLocalWorkGeneration();
                    localScanStarted = true;
                    var added = _tracker.Scan();
                    var liveChanged = _tracker.UpdateLive();
                    changed |= MarkLocalScanRecovered();
                    changed |= added > 0 || liveChanged;
                    localScanCompleted = true;

                    // Partida en curso: se relee cada 15 s. Replay terminado: enseguida. Si no, un escaneo de respaldo.
                    _nextScan = DateTime.UtcNow.AddSeconds(_tracker.IsFinishing ? 2 : _tracker.HasPending ? 15 : (_watcher != null && !AutoMode ? 60 : 10));
                }
                if (AutoMode)
                {
                    changed |= CheckAutoClose();
                }
            }
            catch (Exception ex)
            {
                _services.Errors.Write("Vigilancia", ex);
                if (localScanStarted && !localScanCompleted)
                {
                    SetLocalProgressFailed();
                    PublishProgressSafely();
                }
            }

            changed |= CheckUpdate();
            if (_reportRequested)
            {
                _reportRequested = false;
                _services.Reporting.WriteAll(_store, _config, _data);
            }
            if (changed || _publishRequested)
            {
                _publishRequested = false;
                Publish();
            }
            if (localScanCompleted)
            {
                CompleteLocalWorkIfDrained(localWorkGeneration);
            }

            // Espera a que pase algo (cambio de archivo, ajuste) o un segundo.
            _wake.WaitOne(TimeSpan.FromSeconds(1));
        }
    }

    private void OnLocalProgressChanged(LocalReplayProgress progress)
    {
        lock (_localProgressGate)
        {
            _localProgress = progress;
        }
        PublishProgressSafely();
    }

    private void PublishProgressSafely()
    {
        try
        {
            Publish();
        }
        catch (Exception ex)
        {
            _services.Errors.Write("Publish replay processing progress", ex);
        }
    }

    private void SetLocalProgressIdentifying()
    {
        lock (_localProgressGate)
        {
            if (_localProgress.Phase == LocalReplayPhase.Processing)
            {
                return;
            }
            _localProgress = new LocalReplayProgress(
                LocalReplayPhase.Identifying,
                _localProgress.BatchNumber,
                0,
                0,
                0,
                0,
                0,
                0,
                null);
        }
        RequestPublish();
    }

    private void SetLocalProgressFailed()
    {
        lock (_localProgressGate)
        {
            _localProgress = _localProgress with { Phase = LocalReplayPhase.Failed };
        }
    }

    private bool MarkLocalScanRecovered()
    {
        lock (_localProgressGate)
        {
            if (_localProgress.Phase != LocalReplayPhase.Failed)
            {
                return false;
            }
            _localProgress = _localProgress with { Phase = LocalReplayPhase.Completed, CurrentReplay = null };
            return true;
        }
    }

    private bool SetLocalWorkPending(bool pending)
    {
        var changed = false;
        lock (_localWorkGate)
        {
            if (pending)
            {
                _localWorkGeneration++;
            }
            changed = _localWorkPending != pending;
            _localWorkPending = pending;
            if (changed)
            {
                try
                {
                    LocalProcessingChanged?.Invoke(pending);
                }
                catch (Exception ex)
                {
                    _services.Errors.Write("Notify replay processing priority", ex);
                }
            }
        }
        return changed;
    }

    private LocalReplayProgress GetLocalProgress()
    {
        lock (_localProgressGate)
        {
            return _localProgress;
        }
    }

    private bool IsLocalWorkPending
    {
        get
        {
            lock (_localWorkGate)
            {
                return _localWorkPending;
            }
        }
    }

    private long GetLocalWorkGeneration()
    {
        lock (_localWorkGate)
        {
            return _localWorkGeneration;
        }
    }

    public void ResumeSynchronizationIfLocalWorkIsDrained(Action resume)
    {
        lock (_localWorkGate)
        {
            if (!_localWorkPending)
            {
                resume();
            }
        }
    }

    public void SubscribeToLocalProcessing(Action<bool> observer)
    {
        lock (_localWorkGate)
        {
            LocalProcessingChanged += observer;
            observer(_localWorkPending);
        }
    }

    public void UnsubscribeFromLocalProcessing(Action<bool> observer)
    {
        lock (_localWorkGate)
        {
            LocalProcessingChanged -= observer;
        }
    }

    private void CompleteLocalWorkIfDrained(long scanGeneration)
    {
        if (_tracker.BacklogCount() > 0)
        {
            return;
        }

        lock (_localWorkGate)
        {
            if (!_localWorkPending || _localWorkGeneration != scanGeneration)
            {
                return;
            }
            _localWorkPending = false;
        }

        try
        {
            Publish();
        }
        catch (Exception ex)
        {
            _services.Errors.Write("Publish completed local replay processing", ex);
            return;
        }

        try
        {
            LocalProcessingChanged?.Invoke(false);
        }
        catch (Exception ex)
        {
            _services.Errors.Write("Notify replay processing priority", ex);
        }
    }

    private OverlayState BuildOverlay()
    {
        var liveAdjusted = (_tracker.LiveMatch != null && Profiles.Counted(_tracker.LiveMatch, _config)) ? Profiles.Adjust(_tracker.LiveMatch, _config) : null;
        return OverlayModel.Build(
            Profiles.View(_data, _config),
            liveAdjusted,
            _tracker.IsLive && (_tracker.LiveMatch == null || liveAdjusted != null),
            _services.Reporting.SessionStartUtc,
            Profiles.InitialChests(_data, _config));
    }

    private void Publish()
    {
        var shown = Profiles.Current(_data, _config);
        _shownKey = shown?.Key;
        _shownName = shown?.Name;
        var currentEpicId = shown == null
            ? null
            : _data.Matches
                .Where(match => Profiles.Key(match) == shown.Key)
                .OrderByDescending(match => match.MatchStartUtc ?? match.AnalyzedAtUtc)
                .Select(match => match.EpicId)
                .FirstOrDefault(epicId => !string.IsNullOrWhiteSpace(epicId));
        var overlay = BuildOverlay();
        _latestOverlay = overlay;
        _services.Reporting.WriteOverlay(_store, overlay, _config.OverlayScalePercent);
        if (OperatingSystem.IsWindows())
        {
            _overlayHost.Update(overlay);
        }

        var notice = _installing
            ? L.T("status.downloading_the_update")
            : _notice ?? (_autoCloseAt != null ? L.T("status.fortnite_is_closed_the_counter_closes_in_30") : null);
        var state = new MonitorState(overlay, Profiles.List(_data, _config), Profiles.Current(_data, _config)?.Key, _update, notice, _autoCloseAt != null, BuildStats())
        {
            CurrentEpicId = currentEpicId,
            CurrentPlayerName = shown?.Name,
            Team = BuildTeam(),
            PirateTokenLive = _tracker.IsLive
                && _tracker.LiveMatch != null
                && Profiles.Counted(_tracker.LiveMatch, _config)
                && Profiles.Adjust(_tracker.LiveMatch, _config)!.PirateTokenSeen,
            LocalProgress = GetLocalProgress(),
            LocalPriorityActive = IsLocalWorkPending,
        };
        Changed?.Invoke(state);
    }

    /// <summary>
    /// Estadísticas del perfil mostrado sobre todo su historial guardado (no solo los replays que siguen en disco),
    /// con la partida en curso incluida. Son las mismas cifras que el informe.
    /// </summary>
    private ChestStats BuildStats()
    {
        var matches = Profiles.View(_data, _config).Matches;
        var live = _tracker.IsLive && _tracker.LiveMatch != null && Profiles.Counted(_tracker.LiveMatch, _config) ? Profiles.Adjust(_tracker.LiveMatch, _config) : null;
        var today = DateTime.Now.Date;

        var perDay = new Dictionary<DateTime, int>();
        foreach (var m in matches)
        {
            var day = (m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime().Date;
            perDay[day] = perDay.GetValueOrDefault(day) + m.ChestsOpened;
        }
        if (live != null)
        {
            perDay[today] = perDay.GetValueOrDefault(today) + live.ChestsOpened;
        }

        var all = live != null ? matches.Append(live).ToList() : matches;
        var average = matches.Count > 0 ? (double)matches.Sum(m => m.ChestsOpened) / matches.Count : 0;
        var best = perDay.Count > 0 ? perDay.MaxBy(kv => kv.Value) : (KeyValuePair<DateTime, int>?)null;
        return new ChestStats(
            Matches: matches.Count,
            Average: average,
            BestMatch: all.Count > 0 ? all.Max(m => m.ChestsOpened) : 0,
            Treasures: all.Sum(m => m.TreasureFinds.Count),
            Today: perDay.GetValueOrDefault(today),
            BestDay: best?.Key,
            BestDayChests: best?.Value ?? 0)
        {
            LastDays = BuildLastDays(today, live),
            HistoryMatches = Profiles.View(_data, _config).Matches.Count,
            LastMatch = BuildLastMatch(),
            HistorySince = Profiles.View(_data, _config).Matches
                .Select(m => (DateTime?)(m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime().Date)
                .Min(),
        };
    }

    /// <summary>
    /// Equipo de la partida en curso (si se puede leer) o de la última: tus cofres y los vistos de cada compañero.
    /// Null si «Compañeros» está desactivado o aún no hay partidas.
    /// </summary>
    private TeamSummary? BuildTeam()
    {
        if (!_config.ShowTeammates)
        {
            return null;
        }
        var kinds = _config.ExtraKinds();
        var live = _tracker.IsLive && _tracker.LiveMatch != null && Profiles.Counted(_tracker.LiveMatch, _config) ? Profiles.Adjust(_tracker.LiveMatch, _config) : null;
        var m = live ?? Profiles.View(_data, _config).Matches.OrderByDescending(x => x.MatchStartUtc ?? x.AnalyzedAtUtc).FirstOrDefault();
        if (m == null)
        {
            return null;
        }
        var mates = (m.Teammates ?? new List<TeammateRecord>())
            .Select(t => new TeamMember(t.Name, MatchRecord.Seen(t, kinds)))
            .OrderByDescending(t => t.Chests)
            .ToList();
        return new TeamSummary(live != null, m.OwnChests, mates, m.Teammates == null);
    }

    /// <summary>Última partida terminada del perfil mostrado.</summary>
    private LastMatchSummary? BuildLastMatch()
    {
        var m = Profiles.View(_data, _config).Matches
            .OrderByDescending(x => x.MatchStartUtc ?? x.AnalyzedAtUtc)
            .FirstOrDefault();
        if (m == null)
        {
            return null;
        }
        return new LastMatchSummary(m.ChestsOpened, m.Chests.Count(c => c.Destroyed), m.TreasureFinds.Count,
            m.Placement, m.TotalPlayers, m.Eliminations, m.PirateTokenSeen);
    }

    /// <summary>Cofres de los últimos 7 días del perfil mostrado (todas sus partidas, no solo las 100 últimas).</summary>
    private List<DayChests> BuildLastDays(DateTime today, MatchRecord? live)
    {
        const int days = 7;
        var first = today.AddDays(-(days - 1));
        var sums = new Dictionary<DateTime, int>();
        var all = Profiles.View(_data, _config).Matches.Append(live).OfType<MatchRecord>();
        foreach (var m in all)
        {
            var day = (m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime().Date;
            if (day >= first && day <= today)
            {
                sums[day] = sums.GetValueOrDefault(day) + m.ChestsOpened;
            }
        }
        return Enumerable.Range(0, days)
            .Select(i => first.AddDays(i))
            .Select(d => new DayChests(d, sums.GetValueOrDefault(d)))
            .ToList();
    }

    /// <summary>Anuncia una versión nueva; en modo automático la instala cuando no hay partidas en curso. Devuelve true si hay que publicar.</summary>
    private bool CheckUpdate()
    {
        var u = _update;
        if (u == null || _updateAnnounced || _installing)
        {
            return false;
        }

        if (_config.UpdateMode == "auto")
        {
            var idle = !_tracker.IsLive && !_tracker.HasPending && !_tracker.IsFinishing;
            if (!idle || _services.Executable.ExePath == null)
            {
                return false;
            }
            _updateAnnounced = true;
            InstallUpdate();
            return true;
        }

        _updateAnnounced = true;
        return true;
    }

    /// <summary>Opened with Fortnite: closes once Fortnite is closed and the last match is counted (5 min at most).</summary>
    private bool CheckAutoClose()
    {
        if (_keepOpenRequested)
        {
            _keepOpenRequested = false;
            _autoCloseAt = null;
            _fortniteClosedSince = null;
            _closeRequested = false;
            return true;
        }

        var now = DateTime.UtcNow;
        if (_services.Game.IsFortniteRunning())
        {
            var wasOffering = _autoCloseAt != null;
            _fortniteClosedSince = null;
            _autoCloseAt = null;
            return wasOffering;
        }

        _fortniteClosedSince ??= now;
        var lastMatchDone = !_tracker.IsLive && !_tracker.HasPending && !_tracker.IsFinishing;
        if (!lastMatchDone && now - _fortniteClosedSince.Value < TimeSpan.FromMinutes(5))
        {
            return false;
        }

        if (_autoCloseAt == null)
        {
            _autoCloseAt = now.AddSeconds(30);
            return true;
        }
        if (now >= _autoCloseAt && !_closeRequested)
        {
            _closeRequested = true;
            CloseRequested?.Invoke();
        }
        return false;
    }

    // ---- Piezas internas -------------------------------------------------------------------------

    private void StartOverlay()
    {
        _overlayHost.Start(_config.OverlayX, _config.OverlayY, _config.OverlayLocked, _config.OverlayScalePercent,
            (x, y) =>
            {
                lock (_cfgGate)
                {
                    _config.OverlayX = x;
                    _config.OverlayY = y;
                    SaveQuietly();
                }
            },
            locked =>
            {
                lock (_cfgGate)
                {
                    _config.OverlayLocked = locked;
                    SaveQuietly();
                }
            },
            scale => { SetOverlayScale(scale); },
            _config.OverlayVisible,
            visible =>
            {
                lock (_cfgGate)
                {
                    _config.OverlayVisible = visible;
                    SaveQuietly();
                }
                RequestPublish(); // el botón de la ventana cambia de color
            });
    }

    /// <summary>Windows avisa cuando cambia un replay: no hace falta releer la carpeta todo el tiempo.</summary>
    private void RestartWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            if (Directory.Exists(_config.DemosFolder))
            {
                _watcher = new FileSystemWatcher(_config.DemosFolder, "*.replay")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _watcher.Created += OnReplayFileChanged;
                _watcher.Changed += OnReplayFileChanged;
                _watcher.Renamed += OnReplayFileChanged;
            }
        }
        catch
        {
            _watcher = null; // escaneo periódico de respaldo
        }
    }

    private void OnReplayFileChanged(object? sender, FileSystemEventArgs e)
    {
        SetLocalWorkPending(true);
        SetLocalProgressIdentifying();
        Wake();
    }

    private void Wake()
    {
        var soon = DateTime.UtcNow.AddSeconds(2);
        if (soon < _nextScan)
        {
            _nextScan = soon;
        }
        _wake.Set();
    }

    private void SaveQuietly()
    {
        try
        {
            _store.SaveConfig(_config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Errors.Write("Save application configuration", ex);
        }
    }
}
