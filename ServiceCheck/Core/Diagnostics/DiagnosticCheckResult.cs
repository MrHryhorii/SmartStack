namespace Tsubaki.ServiceCheck.Core.Diagnostics;

/// <summary>
/// Describes the outcome of an individual check.
/// </summary>
public enum DiagnosticCheckStatus
{
    Passed,
    Warning,
    Failed,
    Skipped,
    Error
}

/// <summary>
/// Defines whether a check is required for the selected operation.
/// </summary>
public enum DiagnosticCheckRole
{
    Required,
    Supporting
}

/// <summary>
/// Stores selected, sanitized HTTP observations.
/// </summary>
public sealed record DiagnosticHttpEvidence
{
    public required string Method { get; init; }

    public required string Route { get; init; }

    public int? StatusCode { get; init; }

    public string? MediaType { get; init; }

    public long? DeclaredContentLength { get; init; }

    public long? BodyBytesRead { get; init; }

    public bool BodyComplete { get; init; }

    public int? ReportedSampleRateHz { get; init; }

    public string? RedirectLocation { get; init; }

    public string? ErrorExcerpt { get; init; }

    public bool ErrorExcerptTruncated { get; init; }

    public string? ExceptionType { get; init; }
}

/// <summary>
/// Records one diagnostic observation and its supporting evidence.
/// </summary>
public sealed record DiagnosticCheckResult
{
    public required string Id { get; init; }

    public required DiagnosticCheckRole Role { get; init; }

    public required DiagnosticCheckStatus Status { get; init; }

    public required string Code { get; init; }

    public required string Summary { get; init; }

    public required DateTimeOffset ObservedAtUtc { get; init; }

    public long? DurationMs { get; init; }

    public DiagnosticHttpEvidence? Http { get; init; }

    public IReadOnlyList<string> Suggestions { get; init; } = [];
}