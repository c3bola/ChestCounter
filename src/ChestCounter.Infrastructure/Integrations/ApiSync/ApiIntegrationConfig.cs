using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChestCounter;

public sealed class ApiIntegrationConfig
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = ApiIntegrationConfigStore.DefaultBaseUrl;
    public string ApplicationId { get; set; } = ReplayIngestionMapper.DefaultApplicationId;
}

public sealed class ApiIntegrationConfigStore
{
    public const string DefaultBaseUrl = "https://api.fortcards.me/api/v1/";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    public ApiIntegrationConfigStore(string path) => Path = path;

    public string Path { get; }
    public string? LoadError { get; private set; }

    public ApiIntegrationConfig Load()
    {
        lock (_gate)
        {
            LoadError = null;
            if (!File.Exists(Path))
            {
                var config = new ApiIntegrationConfig();
                try
                {
                    SaveCore(config);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LoadError = ex.Message;
                    ErrorLog.Write("Create default API integration configuration", ex);
                }
                return config;
            }

            try
            {
                var text = File.ReadAllText(Path);
                var config = JsonSerializer.Deserialize<ApiIntegrationConfig>(text, Json);
                if (config == null)
                {
                    throw new InvalidDataException("The API configuration file is empty.");
                }

                if (!TryNormalizeBaseUrl(config.BaseUrl, out var normalized))
                {
                    throw new InvalidDataException("The API base URL must be an absolute HTTP or HTTPS URL.");
                }
                if (string.IsNullOrWhiteSpace(config.ApplicationId) || config.ApplicationId.Length > 150)
                {
                    throw new InvalidDataException("The API application ID must contain between 1 and 150 characters.");
                }

                config.BaseUrl = normalized;
                RemoveLegacyToken(text, config);
                return config;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                LoadError = ex.Message;
                ErrorLog.Write("Load API integration configuration", ex);
                return new ApiIntegrationConfig();
            }
        }
    }

    public void Save(ApiIntegrationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!TryNormalizeBaseUrl(config.BaseUrl, out var normalized))
        {
            throw new ArgumentException("The API base URL must be an absolute HTTP or HTTPS URL.", nameof(config));
        }

        lock (_gate)
        {
            if (LoadError != null)
            {
                throw new InvalidDataException("The existing API configuration could not be loaded and was not overwritten.");
            }

            config.BaseUrl = normalized;
            SaveCore(config);
        }
    }

    public void UpdateBaseUrl(string baseUrl)
    {
        if (!TryNormalizeBaseUrl(baseUrl, out var normalized))
        {
            throw new ArgumentException("The API base URL must be an absolute HTTP or HTTPS URL.", nameof(baseUrl));
        }

        lock (_gate)
        {
            var config = Load();
            if (LoadError != null)
            {
                throw new InvalidDataException("The existing API configuration could not be loaded and was not overwritten.");
            }
            config.BaseUrl = normalized;
            SaveCore(config);
        }
    }

    public static bool TryNormalizeBaseUrl(string? value, out string normalized)
    {
        normalized = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        return true;
    }

    // Earlier versions stored a manually pasted token here; it is no longer used, so drop it from disk.
    private void RemoveLegacyToken(string text, ApiIntegrationConfig config)
    {
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.EnumerateObject().Any(p => p.Name.Equals("bearerToken", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        try
        {
            SaveCore(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Remove legacy token from API configuration", ex);
        }
    }

    private void SaveCore(ApiIntegrationConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporaryPath = Path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(config, Json));
        File.Move(temporaryPath, Path, overwrite: true);
        LoadError = null;
    }
}
