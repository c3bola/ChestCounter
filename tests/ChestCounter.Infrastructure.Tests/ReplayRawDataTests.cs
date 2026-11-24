using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using ChestCounter;
using FortniteReplayReader;
using FortniteReplayReader.Models;
using FortniteReplayReader.Models.NetFieldExports;
using FortniteReplayReader.Models.NetFieldExports.Builds;
using Unreal.Core;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;
using Xunit;

namespace ChestCounter.Infrastructure.Tests;

public sealed class ReplayRawDataTests
{
    [Fact]
    public void DisposedReplayAnalyzerReleasesStaticRawProbeHooks()
    {
        var analyzer = new ReplayAnalyzer();
        var filter = RawProbe.Filter;
        var sink = RawProbe.Sink;

        Assert.NotNull(filter);
        Assert.NotNull(sink);

        analyzer.Dispose();

        Assert.NotSame(filter, RawProbe.Filter);
        Assert.NotSame(sink, RawProbe.Sink);
    }

    [Fact]
    public void DisposedReplayAnalyzerCanBeCollectedWithoutGenerationRetention()
    {
        var analyzer = CreateAnalyzerWeakReference();

        for (var attempt = 0; attempt < 3 && analyzer.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(analyzer.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAnalyzerWeakReference()
    {
        var analyzer = new ReplayAnalyzer();
        var reference = new WeakReference(analyzer);
        analyzer.Dispose();
        return reference;
    }

    [Fact]
    public void PlayerMovementAndPoiTagsArePreservedByTheReplayBuilder()
    {
        var builder = new FortniteReplayBuilder();
        builder.AddActorChannel(10, 100);
        builder.UpdatePlayerState(10, new FortPlayerState { PlayerID = 7, TeamIndex = 1 });
        builder.AddActorChannel(20, 200);
        builder.UpdatePlayerPawn(20, new PlayerPawn
        {
            PlayerState = 100,
            ReplicatedMovement = new FRepMovement
            {
                Location = new FVector(1000, 2000, 300),
                LinearVelocity = new FVector(10, 20, 30),
                Rotation = new FRotator(1, 2, 3),
            },
            Vehicle = 300,
            SeatIndex = 1,
        });
        var poiTags = Array.Empty<FGameplayTagContainer>();
        builder.UpdatePoiManager(new FortPoiManager
        {
            PoiTagContainerTable = poiTags,
            PoiTagContainerTableSize = 0,
        });

        var replay = builder.Build(new FortniteReplay());
        var player = Assert.Single(replay.PlayerData);
        var movement = Assert.Single(player.Locations);

        Assert.Equal((uint)200, movement.PawnActorId);
        Assert.Equal((uint)300, movement.Vehicle);
        Assert.Equal((byte)1, movement.SeatIndex);
        Assert.Null(movement.ReplicatedWorldTimeSeconds);
        Assert.Null(movement.ReplicatedWorldTimeSecondsDouble);
        Assert.Equal(new FVector(1000, 2000, 300), movement.ReplicatedMovement?.Location);
        Assert.Equal(new FVector(10, 20, 30), movement.ReplicatedMovement?.LinearVelocity);
        Assert.Same(poiTags, replay.MapData.PoiTagContainerTable);
        Assert.Equal(0, replay.MapData.PoiTagContainerTableSize);
    }

    [Fact]
    public void PlayerAndBotIdentitiesRemainAssociatedWithTheirTeam()
    {
        var builder = new FortniteReplayBuilder();
        builder.AddActorChannel(10, 100);
        builder.UpdatePlayerState(10, new FortPlayerState
        {
            PlayerID = 7,
            UniqueId = "human-epic-id",
            TeamIndex = 3,
        });
        builder.AddActorChannel(11, 101);
        builder.UpdatePlayerState(11, new FortPlayerState
        {
            PlayerID = 8,
            BotUniqueId = "bot-8",
            bIsABot = true,
            TeamIndex = 3,
        });

        var replay = builder.Build(new FortniteReplay());
        var players = replay.PlayerData.OrderBy(player => player.Id).ToArray();
        var team = Assert.Single(replay.TeamData);

        Assert.Equal("human-epic-id", players[0].PlayerId);
        Assert.False(players[0].IsBot);
        Assert.Equal("bot-8", players[1].PlayerId);
        Assert.True(players[1].IsBot);
        Assert.Equal(new int?[] { 7, 8 }, team.PlayerIds);
        Assert.Equal(3, team.TeamIndex);
    }

    [Fact]
    public void RepeatedIdenticalKillFeedStateIsStoredOnce()
    {
        var builder = new FortniteReplayBuilder();
        builder.AddActorChannel(10, 100);
        var state = new FortPlayerState { PlayerID = 7, bDBNO = true };

        builder.UpdatePlayerState(10, state);
        builder.UpdatePlayerState(10, state);

        var feed = Assert.Single(builder.Build(new FortniteReplay()).KillFeed);
        Assert.Equal(7, feed.PlayerId);
        Assert.True(feed.IsDowned);
    }

    [Fact]
    public void PlayerAndBotCountsAreNotRelabeledAsMatchStartTotals()
    {
        var builder = new FortniteReplayBuilder();
        builder.UpdateGameState(new GameState
        {
            TeamCount = 100,
            TeamSize = 3,
            PlayerBotsLeft = 10,
            PlayersLeft = 45,
            TeamsLeft = 30,
        });

        var gameData = builder.Build(new FortniteReplay()).GameData;

        Assert.Null(gameData.MaxPlayers);
        Assert.Null(gameData.TotalBots);
        Assert.Equal(100, gameData.TeamCount);
        Assert.Equal(10, gameData.PlayerBotsLeft);
        Assert.Equal(45, gameData.PlayersLeft);
        Assert.Equal(30, gameData.TeamsLeft);
        Assert.Equal(3, gameData.TeamSize);
    }

    [Fact]
    public void ExportGroupPrefixesCanBeIgnoredDuringFullParsing()
    {
        const string groupPath = "/Game/Building/ActorBlueprints/Player/Wood/L1/PBWA_W1_Solid.PBWA_W1_Solid_C";
        var parser = new NetFieldParser(new NetGuidCache(), ParseMode.Full);
        var ignoredParser = new NetFieldParser(
            new NetGuidCache(),
            ParseMode.Full,
            ignoredTypes: new HashSet<Type> { typeof(PlayerBuild) });

        Assert.True(parser.WillReadType(groupPath));
        Assert.False(ignoredParser.WillReadType(groupPath));
    }

    [Fact]
    public void RawReplayDataUsesApiDataFieldWithoutDuplicateProperty()
    {
        var match = new MatchRecord
        {
            SessionId = "session-raw-data",
            RawReplayData = new ReplayRawData
            {
                ParseMode = "Full",
                Match = ParseJson("""{"source":"test"}"""),
                Players = ParseJson("""[{"id":7,"locations":[{"vehicle":300}]}]"""),
                Teams = ParseJson("[]"),
                EventEliminations = ParseJson("[]"),
                KillFeed = ParseJson("[]"),
                Map = ParseJson("{}"),
                Weapons = ParseJson("[]"),
                Observations = ParseJson("{}"),
            },
        };
        var replayHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("replay"))).ToLowerInvariant();

        var snapshot = ReplayIngestionMapper.Create(match, "match.replay", replayHash, null, null, "1.0", "1.0");
        var requestJson = JsonSerializer.Serialize(snapshot.Request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var requestDocument = JsonDocument.Parse(requestJson);
        var data = requestDocument.RootElement.GetProperty("data");
        var player = Assert.Single(data.GetProperty("players").EnumerateArray());

        Assert.DoesNotContain("rawReplayData", requestJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Full", data.GetProperty("parseMode").GetString());
        Assert.Equal(7, player.GetProperty("id").GetInt32());
        Assert.Equal(300, player.GetProperty("locations")[0].GetProperty("vehicle").GetInt32());
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
