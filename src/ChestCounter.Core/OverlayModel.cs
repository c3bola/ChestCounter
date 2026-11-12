namespace ChestCounter;

/// <summary>Construye datos del overlay a partir de las partidas guardadas y la partida en curso.</summary>
public static class OverlayModel
{
    /// <summary>Texto del contador derecho entre dos partidas (también sirve para mantener el ancho del overlay).</summary>
    public static string LastMatchCaption => L.T("overlay.previous");

    /// <param name="live">Cifras de la partida en curso, cuando el replay se pudo leer (null si no).</param>
    /// <param name="inProgress">Hay una partida grabándose (aunque sus cifras aún no se puedan leer).</param>
    /// <param name="initialChests">Cofres estimados de antes de usar la herramienta (solo se suman al total).</param>
    public static OverlayState Build(
        TrackerData d,
        MatchRecord? live,
        bool inProgress,
        DateTime sessionStartUtc,
        int initialChests = 0)
    {
        var liveChests = live?.ChestsOpened ?? 0;
        var liveTreasures = live?.TreasureFinds.Count ?? 0;

        var total = d.Matches.Sum(m => m.ChestsOpened) + liveChests + Math.Max(0, initialChests);
        var session = d.Matches.Where(m => Profiles.IsThisSession(m, sessionStartUtc)).ToList();
        var rows = new List<OverlayMetric>();

        // Sesión: cofres, y debajo "n partidas · media x" (partidas terminadas de esta sesión).
        // (el detalle solo lo usa la ventana principal; el overlay muestra la media en su propia columna).
        string? sessionDetail = null;
        var avgText = "—";
        if (session.Count > 0)
        {
            var avg = (double)session.Sum(m => m.ChestsOpened) / session.Count;
            avgText = Math.Round(avg, MidpointRounding.AwayFromZero).ToString("0", L.Culture);
            sessionDetail = L.F("overlay.session_detail", L.Count(session.Count, "common.match", "common.matches"), avgText);
        }
        // Solo números en las columnas: la etiqueta ya dice qué son, y un número largo no choca con la columna de al lado.
        rows.Add(new OverlayMetric(L.T("main.session"), L.N(session.Sum(m => m.ChestsOpened) + liveChests), Detail: sessionDetail));
        rows.Add(new OverlayMetric(L.T("overlay.avg_media"), avgText));

        var last = d.Matches.OrderByDescending(m => m.MatchStartUtc ?? m.AnalyzedAtUtc).FirstOrDefault();
        // Entre partidas el contador derecho ya muestra la última partida: la fila solo aparece durante una partida.
        if (last != null && (live != null || inProgress))
        {
            rows.Add(new OverlayMetric(L.T("overlay.previous_anterior"), L.N(last.ChestsOpened)));
        }

        var treasures = d.Matches.Sum(m => m.TreasureFinds.Count) + liveTreasures;
        rows.Add(new OverlayMetric(L.T("main.treasures"), L.N(treasures), Treasure: true));

        // Contador derecho: la partida en curso si se puede leer, "…" mientras no se pueda, si no la última partida.
        if (live != null)
        {
            return new OverlayState(L.N(total), L.N(live.ChestsOpened), L.T("overlay.current"), true,
                L.T("overlay.live"), rows);
        }
        // Sin etiqueta en la cabecera: el contador derecho ya dice "en curso".
        if (inProgress)
        {
            return new OverlayState(L.N(total), "…", L.T("overlay.in_progress"), true, null, rows);
        }
        return new OverlayState(L.N(total), last != null ? L.N(last.ChestsOpened) : null, LastMatchCaption, false, null, rows);
    }
}
