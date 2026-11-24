using System.Text.Json;
using Xunit;

namespace ChestCounter.Core.Tests;

public sealed class DataArchitectureTests
{
    [Fact]
    public void RemoteProfileUrlUsesGlobalPlayerIdWithoutAdditionalParameters()
    {
        var built = RemoteProfileUrl.TryBuild(170, out var uri, out var error);

        Assert.True(built);
        Assert.Equal("", error);
        Assert.Equal("https://contadordebau.fortcards.me/jogadores/170", uri!.AbsoluteUri);
    }

    [Fact]
    public void RemoteProfileUrlRejectsMissingOrInvalidGlobalPlayerId()
    {
        Assert.False(RemoteProfileUrl.TryBuild(null, out _, out var missingError));
        Assert.Contains("not available", missingError, StringComparison.OrdinalIgnoreCase);
        Assert.False(RemoteProfileUrl.TryBuild(0, out _, out _));
        Assert.False(RemoteProfileUrl.TryBuild(-1, out _, out _));
    }

    [Fact]
    public void MatchSummaryContainsDashboardDataWithoutRawReplayData()
    {
        var match = new MatchRecord
        {
            SessionId = "session",
            ReplayFile = "match.replay",
            ReplayFileSha256 = new string('a', 64),
            MatchStartUtc = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc),
            ChestsOpened = 12,
            AmmoBoxesOpened = 2,
            OtherContainersOpened = 3,
            Eliminations = 4,
            Placement = 2,
            RawReplayData = null,
            IsSynced = true,
            Chests =
            [
                new ChestRecord { Destroyed = true },
            ],
            TreasureFinds =
            [
                new TreasureFind { Item = "Prize" },
            ],
        };

        var summary = MatchSummary.FromMatch(match);

        Assert.Equal("session", summary.MatchKey);
        Assert.Equal(12, summary.ChestsOpened);
        Assert.Equal(1, summary.ChestsDestroyed);
        Assert.Equal(3, summary.OtherContainers);
        Assert.Equal(1, summary.TreasuresFound);
        Assert.Equal(4, summary.Eliminations);
        Assert.Equal(2, summary.Placement);
        Assert.True(summary.IsSynced);
    }

    [Fact]
    public void LegacyRawReplayDataDeserializesAndCanBeCleared()
    {
        const string json = """
        {
          "version": 1,
          "matches": [{
            "sessionId":"legacy",
            "replayFileSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "rawReplayData":{"parseMode":"Full","match":{},"players":[],"teams":[],"eventEliminations":[],"killFeed":[],"map":{},"weapons":[],"observations":{}}
          }]
        }
        """;

        var data = System.Text.Json.JsonSerializer.Deserialize<TrackerData>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(data);
        Assert.NotNull(Assert.Single(data!.Matches).RawReplayData);
    }
}
