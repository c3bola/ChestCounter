namespace ChestCounter;

public static class ChestKinds
{
    public const string Normal = "Chest";
    public const string Rare = "Rare chest";
    public const string Buried = "Buried chest";
    public const string Extraction = "Extraction / relic chest";
    public const string PosterChest = "Spirit poster chest";
    public const string Poster = "Spirit wanted poster";
    public const string Other = "Other chest";

    public const string Cannon = "Loot cannon";
    public const string AmmoBox = "Ammo box";
    public const string FoodBox = "Food box";
    public const string OtherContainer = "Other container";

    private static readonly HashSet<string> ChestSet = new() { Normal, Rare, Buried, Extraction, PosterChest, Other };

    public static bool IsChest(string kind) => ChestSet.Contains(kind);

    public static string Classify(string blueprint)
    {
        var b = blueprint.ToLowerInvariant();
        if (b.Contains("lootcannon") || b.Contains("cannon")) return Cannon;
        if (b.Contains("ammo")) return AmmoBox;
        if (b.Contains("foodbox") || b.Contains("food_box")) return FoodBox;
        if (b.Contains("rarechest") || b.Contains("legendary") || b.Contains("rare_chest")) return Rare;
        if (b.Contains("buried")) return Buried;
        if ((b.Contains("spriteposter") || b.Contains("wantedposter") || b.Contains("wanted_poster")) && !b.Contains("chest")) return Poster;
        if (b.Contains("spriteposter") || b.Contains("booty")) return PosterChest;
        if (b.Contains("relic") || b.Contains("extraction")) return Extraction;
        if (b.Contains("tiered_chest") || b.Contains("normalchest") || b.Contains("standardchest")) return Normal;
        return b.Contains("chest") ? Other : OtherContainer;
    }
}
