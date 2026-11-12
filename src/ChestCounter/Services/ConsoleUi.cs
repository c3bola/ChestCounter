namespace ChestCounter;

/// <summary>
/// Salida de consola para los modos de línea de comandos (--once, --help, --overlay-preview). La ventana
/// de la aplicación no usa la consola.
/// </summary>
public static class ConsoleUi
{
    /// <summary>True when the console can be redrawn in place (a real console, not a redirected output).</summary>
    public static bool Interactive { get; } = !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static void Text(string text, ConsoleColor? color = null)
    {
        if (color is { } c) Console.ForegroundColor = c;
        foreach (var line in text.Split('\n'))
        {
            Console.WriteLine("  " + line);
        }
        if (color != null) Console.ResetColor();
    }

    public static void Hint(string text) => Text(text, ConsoleColor.DarkGray);
}
