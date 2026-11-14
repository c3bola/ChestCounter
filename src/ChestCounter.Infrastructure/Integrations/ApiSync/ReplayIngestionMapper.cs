using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ChestCounter;

public sealed record ReplayFileRegistration(
    string FileSha256,
    string FileName,
    long? FileSizeBytes,
    DateTimeOffset? FileCreatedAt);

public sealed record ReplayAnalyzerVersionInput(
    string ApplicationVersion,
    string AnalyzerVersion,
    string ParserName,
    string ParserVersion);

public sealed record MatchIngestion(
    string MatchKey,
    DateTimeOffset? MatchStartAt,
    DateTimeOffset? MatchEndAt,
    double? DurationSeconds,
    int? TotalPlayers,
    string? Playlist,
    string? GameVersion);

public sealed record MatchPlayerIngestion(
    string SourcePlayerKey,
    string PlayerType,
    string? EpicId,
    string? CurrentDisplayName,
    int? Placement,
    int? Kills,
    bool? IsReplayOwner);

public sealed record ContainerInteractionIngestion(
    string SourceId,
    double EventTimeSeconds,
    bool IsRefillReopen,
    string InteractionType);

public sealed record ContainerIngestion(
    string SourceId,
    string ContainerKind,
    string? Blueprint,
    double? FirstSeenSeconds,
    IReadOnlyList<ContainerInteractionIngestion> Interactions);

public sealed record RewardIngestion(
    string SourceId,
    string PrizeId,
    double? EventTimeSeconds,
    bool IsGoldenChest,
    bool IsTeammateReward,
    bool? IsFromChest,
    bool? WasDestroyed);

public sealed record ReplayIngestionRequest(
    string IdempotencyKey,
    ReplayAnalyzerVersionInput AnalyzerVersion,
    MatchIngestion Match,
    IReadOnlyList<MatchPlayerIngestion> MatchPlayers,
    IReadOnlyList<ContainerIngestion> Containers,
    IReadOnlyList<RewardIngestion> Rewards);

public sealed record ReplayIngestionSnapshot(
    ReplayFileRegistration ReplayFile,
    ReplayIngestionRequest Request);

public static class ReplayIngestionMapper
{
    private static readonly Regex EpicIdPattern = new("^[a-f0-9]{32}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ReplayIngestionSnapshot Create(
        MatchRecord match,
        FileInfo replayFile,
        string fileSha256,
        string applicationVersion,
        string parserVersion)
    {
        ArgumentNullException.ThrowIfNull(replayFile);
        DateTimeOffset? fileCreatedAt = replayFile.CreationTimeUtc == DateTime.MinValue
            ? null
            : new DateTimeOffset(replayFile.CreationTimeUtc, TimeSpan.Zero);
        return Create(
            match,
            replayFile.Name,
            fileSha256,
            replayFile.Length,
            fileCreatedAt,
            applicationVersion,
            parserVersion);
    }

    public static ReplayIngestionSnapshot Create(
        MatchRecord match,
        string fileName,
        string fileSha256,
        long? fileSizeBytes,
        DateTimeOffset? fileCreatedAt,
        string applicationVersion,
        string parserVersion)
    {
        ArgumentNullException.ThrowIfNull(match);

        if (string.IsNullOrWhiteSpace(match.SessionId) || match.SessionId.Length > 200)
        {
            throw new InvalidDataException("The analyzed match has no valid stable session identifier.");
        }
        if (fileSha256.Length != 64 || fileSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A SHA-256 file hash is required.", nameof(fileSha256));
        }

        var normalizedHash = fileSha256.ToLowerInvariant();
        var idempotencySource = $"{normalizedHash}:{match.AnalyzerVersion}";
        var idempotencyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencySource))).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
        {
            throw new InvalidDataException("The replay file has no valid file name.");
        }

        var fileRegistration = new ReplayFileRegistration(
            normalizedHash,
            fileName,
            fileSizeBytes,
            fileCreatedAt);

        var players = new List<MatchPlayerIngestion>
        {
            new(
                "owner",
                "player",
                NormalizeEpicId(match.EpicId),
                Optional(match.PlayerName),
                match.Placement,
                match.Eliminations,
                match.IsReplayOwner),
        };

        if (match.Teammates != null)
        {
            for (var index = 0; index < match.Teammates.Count; index++)
            {
                var teammate = match.Teammates[index];
                players.Add(new MatchPlayerIngestion(
                    $"teammate-{index + 1}",
                    NormalizeEpicId(teammate.EpicId) == null ? "unknown" : "player",
                    NormalizeEpicId(teammate.EpicId),
                    Optional(teammate.Name),
                    null,
                    null,
                    false));
            }
        }

        var containers = new List<ContainerIngestion>(match.Chests.Count);
        for (var index = 0; index < match.Chests.Count; index++)
        {
            var chest = match.Chests[index];
            if (string.IsNullOrWhiteSpace(chest.Kind))
            {
                continue;
            }

            var sourceId = $"container-{index + 1}";
            var time = ValidSeconds(chest.TimeSeconds);
            var interaction = time is { } eventTime
                ? new[]
                {
                    new ContainerInteractionIngestion(
                        $"interaction-{index + 1}",
                        eventTime,
                        false,
                        chest.Destroyed ? "destroyed" : "opened"),
                }
                : Array.Empty<ContainerInteractionIngestion>();

            containers.Add(new ContainerIngestion(
                sourceId,
                chest.Kind,
                Optional(chest.Blueprint),
                time,
                interaction));
        }

        var rewards = new List<RewardIngestion>();
        var rewardIndex = 0;
        foreach (var reward in match.AllTreasureFinds)
        {
            if (string.IsNullOrWhiteSpace(reward.PrizeId)
                || (reward.Scope != "own" && reward.Scope != "team"))
            {
                continue;
            }

            rewardIndex++;
            var eventSeconds = reward.ObtainedAtSeconds is { } obtained ? ValidSeconds(obtained) : null;
            rewards.Add(new RewardIngestion(
                $"reward-{rewardIndex}",
                reward.PrizeId,
                eventSeconds,
                reward.FromGoldenChest,
                reward.Scope == "team",
                reward.SourceIsChest,
                reward.SourceDestroyed));
        }

        DateTimeOffset? startAt = match.MatchStartUtc is { } start
            ? new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc))
            : null;
        int? duration = match.DurationSeconds is >= 0 ? match.DurationSeconds : null;
        var request = new ReplayIngestionRequest(
            idempotencyKey,
            new ReplayAnalyzerVersionInput(
                match.AnalysisApplicationVersion ?? applicationVersion,
                match.AnalyzerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "FortniteReplayReader",
                match.ParserVersion ?? parserVersion),
            new MatchIngestion(
                match.SessionId,
                startAt,
                startAt is { } started && duration is { } seconds ? started.AddSeconds(seconds) : null,
                duration,
                match.TotalPlayers,
                Optional(match.Playlist),
                Optional(match.GameVersion)),
            players,
            containers,
            rewards);

        return new ReplayIngestionSnapshot(fileRegistration, request);
    }

    private static string? NormalizeEpicId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && EpicIdPattern.IsMatch(value) ? value.ToLowerInvariant() : null;

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static double? ValidSeconds(double value) => double.IsFinite(value) && value >= 0 ? value : null;
}
