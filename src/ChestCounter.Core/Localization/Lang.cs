using System.Globalization;

namespace ChestCounter;

/// <summary>
/// Idioma de la aplicación. Los textos visibles se resuelven desde <see cref="Translations"/> mediante claves.
/// Los nombres guardados en data.json siguen en inglés por compatibilidad y se traducen al mostrarlos.
/// </summary>
public static class L
{
    /// <summary>
    /// Códigos de idioma soportados, en el mismo orden que las columnas del catálogo.
    /// </summary>
    private static readonly string[] Supported = ["es", "en", "pt", "ru", "fr", "de", "it", "pl", "tr", "ja"];

    private static readonly Dictionary<string, string> Cultures = new()
    {
        ["es"] = "es-ES",
        ["en"] = "en-US",
        ["pt"] = "pt-BR",
        ["ru"] = "ru-RU",
        ["fr"] = "fr-FR",
        ["de"] = "de-DE",
        ["it"] = "it-IT",
        ["pl"] = "pl-PL",
        ["tr"] = "tr-TR",
        ["ja"] = "ja-JP",
    };

    private static readonly (string Code, string Key)[] LanguageKeys =
    [
        ("es", "language.es"), ("en", "language.en"), ("pt", "language.pt"), ("ru", "language.ru"),
        ("fr", "language.fr"), ("de", "language.de"), ("it", "language.it"), ("pl", "language.pl"),
        ("tr", "language.tr"), ("ja", "language.ja"),
    ];

    /// <summary>Idiomas que se pueden elegir en Ajustes, con su nombre nativo desde el catálogo.</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages =>
        LanguageKeys.Select(language => (language.Code, T(language.Key))).ToArray();

    /// <summary>
    /// Código del idioma actual ("es", "en", "pt", "ru", "fr", "de", "it", "pl", "tr" o "ja").
    /// Por defecto portugués de Brasil; <see cref="Use"/> aplica el elegido en Ajustes. Para probar: la variable
    /// CHESTCOUNTER_LANG fuerza el idioma por encima de todo.
    /// </summary>
    public static string Code { get; private set; } = "pt";

    /// <summary>True cuando la interfaz va en inglés.</summary>
    public static bool En => Code == "en";

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(Cultures[Code]);

    /// <summary>
    /// Posición del idioma actual en las columnas del catálogo.
    /// </summary>
    private static int Slot = Array.IndexOf(Supported, Code);

    /// <summary>Idioma de Windows (o el forzado con CHESTCOUNTER_LANG), sin tener en cuenta lo elegido en Ajustes.</summary>
    public static string SystemCode => Detect();

    /// <summary>
    /// Aplica el idioma elegido en Ajustes (null o desconocido = el de Windows). Se llama al arrancar, antes de crear
    /// las ventanas: los textos se leen al construirlas, así que un cambio necesita reiniciar el contador.
    /// </summary>
    public static void Use(string? code)
    {
        var chosen = Environment.GetEnvironmentVariable("CHESTCOUNTER_LANG") is { Length: > 0 } || code == null || Array.IndexOf(Supported, code) < 0
            ? Detect()
            : code;
        Code = chosen;
        Culture = CultureInfo.GetCultureInfo(Cultures[chosen]);
        Slot = Array.IndexOf(Supported, chosen);
    }

    private static string Detect()
    {
        var code = Environment.GetEnvironmentVariable("CHESTCOUNTER_LANG") is { Length: > 0 } forced
            ? forced
            : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        code = code.Length >= 2 ? code[..2].ToLowerInvariant() : "";
        return Array.IndexOf(Supported, code) >= 0 ? code : "en";
    }

    /// <summary>Texto en el idioma actual, identificado por una clave del catálogo.</summary>
    public static string T(string key) => Pick(key);

    /// <summary>Texto con huecos {0}, {1}… (formato de <c>string.Format</c>), identificado por una clave del catálogo.</summary>
    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);

    private static string Pick(string key)
    {
        if (!Translations.Table.TryGetValue(key, out var row))
        {
            throw new InvalidOperationException($"Missing localization key '{key}'.");
        }
        if (row.Length != Supported.Length || string.IsNullOrWhiteSpace(row[Slot]))
        {
            throw new InvalidOperationException($"Incomplete localization key '{key}' for language '{Code}'.");
        }
        return row[Slot];
    }

    public static string N(int n) => n.ToString("0", Culture);

    /// <summary>"3 cofres" / "1 chest" (plural según la cifra, en el idioma actual).</summary>
    public static string Count(int n, string oneKey, string manyKey) =>
        $"{N(n)} {T(n == 1 ? oneKey : manyKey)}";

    /// <summary>"12.º" / "12th" / "12.".</summary>
    public static string Ordinal(int n) => Code switch
    {
        "es" => F("format.ordinal.es", n),
        "pt" or "it" => F("format.ordinal.pt", n),
        "fr" => n == 1 ? T("format.ordinal.fr.first") : F("format.ordinal.fr.other", n),
        "ja" => F("format.ordinal.ja", n),
        "en" => F("format.ordinal.en", n, EnglishSuffix(n)),
        _ => F("format.ordinal.default", n),
    };

    private static string EnglishSuffix(int n) => T((n % 100) is 11 or 12 or 13
        ? "format.ordinal.suffix.th"
        : (n % 10) switch
        {
            1 => "format.ordinal.suffix.st",
            2 => "format.ordinal.suffix.nd",
            3 => "format.ordinal.suffix.rd",
            _ => "format.ordinal.suffix.th",
        });

    /// <summary>Fecha corta + hora de una partida ("05/11 22:54" / "Nov 5 22:54").</summary>
    public static string DateTimeShort(DateTime d) => d.ToString(En ? "MMM d HH:mm" : "dd/MM HH:mm", Culture);

    public static string HtmlLanguage => Code == "pt" ? "pt-BR" : Code;

    // Nombres internos guardados en data.json (en inglés) -> nombre mostrado.
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Chest"] = "items.chest",
        ["Rare chest"] = "items.rare_chest",
        ["Buried chest"] = "items.buried_chest",
        ["Extraction / relic chest"] = "items.extraction_relic_chest",
        ["Spirit poster chest"] = "items.spirit_poster_chest",
        ["Other chest"] = "items.other_chest",
        ["Loot cannon"] = "items.loot_cannon",
        ["Ammo box"] = "items.ammo_box",
        ["Food box"] = "items.food_box",
        ["Other container"] = "items.other_container",
        ["Treasure"] = "items.treasure",
        ["Treasure (unknown type)"] = "items.treasure_unknown_type",
        ["Treasure gizmo"] = "items.treasure_gizmo",
        ["Golden firework"] = "items.golden_firework",
        ["Portable extractor"] = "items.portable_extractor",
        ["Spirit locator"] = "items.spirit_locator",
        ["Lucky locator"] = "items.lucky_locator",
        ["Extraction accelerator"] = "items.extraction_accelerator",
        ["unknown"] = "items.unknown",
    };

    /// <summary>
    /// Nombres internos de versiones anteriores a la 2.0.9 (estaban en francés) -> nombre interno actual.
    /// Solo se usa para convertir el historial antiguo al abrirlo.
    /// </summary>
    private static readonly Dictionary<string, string> Legacy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Coffre"] = "Chest",
        ["Coffre rare"] = "Rare chest",
        ["Coffre enterré"] = "Buried chest",
        ["Coffre d'extraction / reliques"] = "Extraction / relic chest",
        ["Coffre des affiches d'esprit"] = "Spirit poster chest",
        ["Autre coffre"] = "Other chest",
        ["Canon à butin"] = "Loot cannon",
        ["Caisse de munitions"] = "Ammo box",
        ["Caisse de nourriture"] = "Food box",
        ["Autre conteneur"] = "Other container",
        ["Trésor"] = "Treasure",
        ["Trésor (type inconnu)"] = "Treasure (unknown type)",
        ["Gizmo de trésor"] = "Treasure gizmo",
        ["Feu d'artifice doré"] = "Golden firework",
        ["Extracteur portable"] = "Portable extractor",
        ["Localisateur d'esprit"] = "Spirit locator",
        ["Localisateur chanceux"] = "Lucky locator",
        ["Accélérateur d'extraction"] = "Extraction accelerator",
        ["inconnu"] = "unknown",
    };

    /// <summary>Nombre interno actual de un nombre guardado por una versión anterior (o el mismo si no cambió).</summary>
    public static string Upgrade(string stored) => Legacy.TryGetValue(stored, out var current) ? current : stored;

    /// <summary>Nombre mostrado de un tipo de cofre u objeto guardado en data.json.</summary>
    public static string Name(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return stored ?? "";
        }
        return Names.TryGetValue(Upgrade(stored), out var key) ? T(key) : stored;
    }
}
