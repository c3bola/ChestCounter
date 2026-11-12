namespace ChestCounter;

public sealed record OverlayMetric(string Label, string Value, bool Treasure = false, string? Detail = null);

public sealed record OverlayState(
    string Total,
    string? Current,
    string CurrentCaption,
    bool CurrentLive,
    string? Badge,
    IReadOnlyList<OverlayMetric> Rows);

public static class OverlayScale
{
    public const int Default = 70;
    public const int Minimum = 50;
    public const int Maximum = 100;
    public const int BaseWidth = 560;
    public const int BaseHeight = 265;

    public static int Clamp(int percent) => Math.Clamp(percent, Minimum, Maximum);
    public static int Width(int percent) => (int)Math.Round(BaseWidth * Clamp(percent) / 100.0);
    public static int Height(int percent) => (int)Math.Round(BaseHeight * Clamp(percent) / 100.0);
}
