using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ChestCounter;

public sealed record ReplayFileRegistration(
    string FileSha256,
    string FileName,
    long? FileSizeBytes,
    DateTimeOffset? FileCreatedAt);

public sealed record ReplayAnalyzerVersionInput(
    [property: JsonPropertyName("application_version")] string ApplicationVersion,
    [property: JsonPropertyName("analyzer_version")] string AnalyzerVersion,
    [property: JsonPropertyName("parser_name")] string ParserName,
    [property: JsonPropertyName("parser_version")] string ParserVersion,
    [property: JsonPropertyName("description")] string? Description);

public sealed record ReplayIngestionRequest(
    [property: JsonPropertyName("application_id")] string ApplicationId = "",
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey = "",
    [property: JsonPropertyName("analyzer_version")] ReplayAnalyzerVersionInput? AnalyzerVersion = null,
    [property: JsonPropertyName("data")] ReplayRawData? Data = null);

public sealed record ReplayIngestionSnapshot(
    ReplayFileRegistration ReplayFile,
    ReplayIngestionRequest Request);

public static class ReplayIngestionMapper
{
    public const string DefaultApplicationId = "chestcounter";
    private const string ParserName = "FortniteReplayReader";
    private const string SnapshotSchemaVersion = "parser-snapshot-v1";

    public static ReplayIngestionSnapshot Create(
        MatchRecord match,
        FileInfo replayFile,
        string fileSha256,
        string applicationVersion,
        string parserVersion,
        string applicationId = DefaultApplicationId)
    {
        ArgumentNullException.ThrowIfNull(replayFile);
        DateTimeOffset? fileCreatedAt = replayFile.CreationTimeUtc == DateTime.MinValue
            ? null
            : new DateTimeOffset(replayFile.CreationTimeUtc, TimeSpan.Zero);
        return Create(
            match,
            replayFile.Name,
            fileSha256,
            replayFile.Length,
            fileCreatedAt,
            applicationVersion,
            parserVersion,
            applicationId);
    }

    public static ReplayIngestionSnapshot Create(
        MatchRecord match,
        string fileName,
        string fileSha256,
        long? fileSizeBytes,
        DateTimeOffset? fileCreatedAt,
        string applicationVersion,
        string parserVersion,
        string applicationId = DefaultApplicationId)
    {
        ArgumentNullException.ThrowIfNull(match);

        if (match.RawReplayData == null)
        {
            throw new InvalidDataException("The analyzed match does not contain a parser snapshot.");
        }
        if (!IsValidSha256(fileSha256))
        {
            throw new ArgumentException("A SHA-256 file hash is required.", nameof(fileSha256));
        }
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
        {
            throw new InvalidDataException("The replay file has no valid file name.");
        }
        if (string.IsNullOrWhiteSpace(applicationId) || applicationId.Length > 150)
        {
            throw new InvalidDataException("The API application ID must contain between 1 and 150 characters.");
        }
        var normalizedApplicationVersion = match.AnalysisApplicationVersion ?? applicationVersion;
        if (string.IsNullOrWhiteSpace(normalizedApplicationVersion) || normalizedApplicationVersion.Length > 50)
        {
            throw new InvalidDataException("The application version must contain between 1 and 50 characters.");
        }
        if (string.IsNullOrWhiteSpace(parserVersion) || parserVersion.Length > 100)
        {
            throw new InvalidDataException("The parser version must contain between 1 and 100 characters.");
        }

        var normalizedHash = fileSha256.ToLowerInvariant();
        var analyzerVersion = match.AnalyzerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var normalizedParserVersion = match.ParserVersion ?? parserVersion;
        if (normalizedParserVersion.Length == 0 || normalizedParserVersion.Length > 100)
        {
            throw new InvalidDataException("The parser version must contain between 1 and 100 characters.");
        }

        var idempotencySource = string.Join(
            ":",
            SnapshotSchemaVersion,
            normalizedHash,
            applicationId,
            normalizedApplicationVersion,
            analyzerVersion,
            normalizedParserVersion);
        var idempotencyKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(idempotencySource))).ToLowerInvariant();
        var fileRegistration = new ReplayFileRegistration(
            normalizedHash,
            fileName,
            fileSizeBytes,
            fileCreatedAt);
        var analyzer = new ReplayAnalyzerVersionInput(
            normalizedApplicationVersion,
            analyzerVersion,
            ParserName,
            normalizedParserVersion,
            null);
        var request = new ReplayIngestionRequest(
            applicationId,
            idempotencyKey,
            analyzer,
            match.RawReplayData);

        return new ReplayIngestionSnapshot(fileRegistration, request);
    }

    private static bool IsValidSha256(string? value) =>
        value is { Length: 64 } && value.All(character => Uri.IsHexDigit(character));
}
