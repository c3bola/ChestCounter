namespace ChestCounter;

public enum ApiDiagnosticSeverity
{
    Information,
    Progress,
    Success,
    Warning,
    Error,
}

public sealed record ApiOperationProgress(
    string Operation,
    string BaseUrl,
    DateTimeOffset StartedAtUtc,
    string Status,
    string Stage,
    int? Total,
    int Processed,
    int Succeeded,
    int Failed,
    int Pending,
    string? CurrentReplay);

public sealed record ApiOperationLogEntry(
    DateTimeOffset TimestampUtc,
    ApiDiagnosticSeverity Severity,
    string Description,
    string? Method = null,
    string? Endpoint = null,
    string? ReplayFileName = null,
    string? ReplaySha256 = null,
    string? RequestBody = null,
    int? HttpStatusCode = null,
    string? ResponseSummary = null,
    string? ErrorDetails = null,
    bool? IsRetryable = null,
    TimeSpan? Duration = null);
