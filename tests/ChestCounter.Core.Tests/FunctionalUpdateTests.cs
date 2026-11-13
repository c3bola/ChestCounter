using ChestCounter;
using Xunit;

namespace ChestCounter.Core.Tests;

public sealed class FunctionalUpdateTests
{
    [Theory]
    [InlineData("Athena/Playlist_DefaultSolo", true)]
    [InlineData("Athena/Playlist_NoBuildBR_Squad", true)]
    [InlineData("Athena/Playlist_Ranked_Solo", true)]
    [InlineData("Athena/Playlist_Creative", false)]
    [InlineData(null, false)]
    public void BattleRoyalePlaylistRequiresKnownBattleRoyaleMode(string? playlist, bool expected) =>
        Assert.Equal(expected, Profiles.IsBattleRoyalePlaylist(playlist));

    [Theory]
    [InlineData("B_SpritePosters_WantedPoster_C", ChestKinds.Poster, false)]
    [InlineData("B_SpritePosters_Booty_Chest_C", ChestKinds.PosterChest, true)]
    public void PosterActorsAreNotCountedAsChests(string blueprint, string expectedKind, bool expectedChest)
    {
        var kind = ChestKinds.Classify(blueprint);

        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedChest, ChestKinds.IsChest(kind));
    }

    [Fact]
    public void TeamTreasureSettingAddsOnlyTeamRewardsToAnAdjustedCopy()
    {
        var match = new MatchRecord
        {
            ChestsOpened = 12,
            TreasureFinds = [new TreasureFind { Scope = "own" }],
            NearbyTreasureFinds =
            [
                new TreasureFind { Scope = "team" },
                new TreasureFind { Scope = "nearby" },
                new TreasureFind { Scope = "unknown" },
            ],
        };

        var adjusted = match.WithExtras(Array.Empty<string>(), addTeammateTreasures: true);

        Assert.NotSame(match, adjusted);
        Assert.Equal(12, adjusted.ChestsOpened);
        Assert.Equal(new[] { "own", "team" }, adjusted.TreasureFinds.Select(find => find.Scope));
        Assert.Equal(new[] { "nearby", "unknown" }, adjusted.NearbyTreasureFinds.Select(find => find.Scope));
        Assert.Single(match.TreasureFinds);
        Assert.Equal(3, match.NearbyTreasureFinds.Count);
    }

    [Fact]
    public void PirateTokenRequiresExplicitSemanticEvidence()
    {
        Assert.True(TreasureNames.IsPirateToken("PirateShop:token"));
        Assert.True(TreasureNames.IsPirateToken("Token da Loja Pirata"));
        Assert.False(TreasureNames.IsPirateToken("DelilahPrize:dpid_item4"));

        var reward = new TreasureFind
        {
            FromGoldenChest = true,
            RawItem = "DelilahPrize:dpid_item4",
            PrizeId = "DelilahPrize:dpid_item4",
        };
        Assert.False(reward.IsPirateToken);

        TreasureNames.PrizeNames["DelilahPrize:dpid_item4"] = "Pirate Shop token";
        try
        {
            Assert.True(reward.IsPirateToken);
        }
        finally
        {
            TreasureNames.PrizeNames.Remove("DelilahPrize:dpid_item4");
        }
    }
}
