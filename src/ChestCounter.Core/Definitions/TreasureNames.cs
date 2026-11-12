namespace ChestCounter;

public static class TreasureNames
{
    public static Dictionary<string, string> PrizeNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public const string UnknownPrefix = "Unknown:";
    public const string UnknownGizmo = "Treasure gizmo";
    private const string OldUnknownPrefix = "Inconnu:";

    public static string Friendly(string item)
    {
        if (item.Contains(':'))
        {
            var unknown = item.StartsWith(UnknownPrefix, StringComparison.OrdinalIgnoreCase) || item.StartsWith(OldUnknownPrefix, StringComparison.OrdinalIgnoreCase);
            if (PrizeNames.TryGetValue(item, out var name) && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
            if (unknown)
            {
                var rest = item[(item.IndexOf(':') + 1)..];
                foreach (var prefix in new[] { UnknownPrefix, OldUnknownPrefix })
                {
                    if (PrizeNames.TryGetValue(prefix + rest, out var other) && !string.IsNullOrWhiteSpace(other))
                    {
                        return other;
                    }
                }
                return L.Name(UnknownGizmo);
            }
            return L.F("replay.unknown_treasure", item[(item.IndexOf(':') + 1)..]);
        }

        var i = item.ToLowerInvariant();
        if (i.Contains("flameceleb")) return "Golden firework";
        if (i.Contains("relicextractor")) return "Portable extractor";
        if (i.Contains("relicfinder")) return "Spirit locator";
        if (i.Contains("luckylocator")) return "Lucky locator";
        if (i.Contains("smugglerextractionkey") || i.Contains("extractionaccelerat")) return "Extraction accelerator";
        return item;
    }
}
