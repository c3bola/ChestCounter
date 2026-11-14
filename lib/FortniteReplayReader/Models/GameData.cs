namespace FortniteReplayReader.Models;

public class GameData
{
    public string GameSessionId { get; set; }
    public DateTime? UtcTimeStartedMatch { get; set; }
    public float? MatchEndTime { get; set; }
    public string MapInfo { get; set; }
    public string CurrentPlaylist { get; set; }
    public IEnumerable<string> AdditionalPlaylistLevels { get; set; }
    public IList<string> ActiveGameplayModifiers { get; set; } = new List<string>();

    /// <summary>
    /// Actor Id of recording player.
    /// </summary>
    public uint? RecorderId { get; set; }

    public int? MaxPlayers { get; set; }
    public int? TotalTeams { get; set; }
    public int? TotalBots { get; set; }
    public int? TeamSize { get; set; }
    public int? TeamCount { get; set; }
    public int? PlayerBotsLeft { get; set; }
    public int? PlayersLeft { get; set; }
    public int? TeamsLeft { get; set; }
    public int? ElapsedTime { get; set; }
    public int? GamePhase { get; set; }
    public byte? SafeZonePhase { get; set; }
    public bool? HasBegunPlay { get; set; }
    public int? TotalPlayerStructures { get; set; }
    public IList<GameStateObservation> GameStateObservations { get; set; } = new List<GameStateObservation>();

    public bool IsTournamentRound => TournamentRound > 0;
    public int? TournamentRound { get; set; }
    public bool? IsLargeTeamGame { get; set; }

    public float? AircraftStartTime { get; set; }
    public float? SafeZonesStartTime { get; set; }

    public uint? WinningTeam { get; set; }
    public IEnumerable<int> WinningPlayerIds { get; set; }
}

public sealed record GameStateObservation(
    float? ReplicatedWorldTimeSeconds,
    double? ReplicatedWorldTimeSecondsDouble,
    int? TeamCount,
    int? TeamSize,
    int? PlayerBotsLeft,
    int? PlayersLeft,
    int? TeamsLeft,
    int? ElapsedTime,
    int? GamePhase,
    byte? SafeZonePhase,
    bool? HasBegunPlay);
