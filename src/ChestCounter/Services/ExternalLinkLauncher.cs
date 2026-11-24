using System.Diagnostics;

namespace ChestCounter;

internal static class ExternalLinkLauncher
{
    public static void Open(string url) =>
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })?.Dispose();
}
