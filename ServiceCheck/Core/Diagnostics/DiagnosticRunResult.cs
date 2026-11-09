using System.Text.Json.Serialization;
using Tsubaki.ServiceCheck.Core.Audio;
using Tsubaki.ServiceCheck.Core.Metadata;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Core.Diagnostics;

/// <summary>
/// Identifies the requested diagnostic operation.
/// </summary>
public enum DiagnosticMode
{
    Inspect,
    Check
}

/// <summary>
/// Describes how diagnostic execution ended.
/// </summary>
public enum DiagnosticExecutionState
{
    Completed,
    Cancelled,
    TimedOut,
    Error
}

/// <summary>
/// Describes the aggregate diagnostic outcome.
/// </summary>
public enum DiagnosticOutcome
{
    Passed,
    PassedWithWarnings,
    Failed,
    Incomplete
}

/// <summary>
/// Represents server-reported synthesis readiness.
/// </summary>
public enum DiagnosticReadiness
{
    Unknown,
    Ready,
    NotReady
}

/// <summary>
/// Identifies the utility that produced a result.
/// </summary>
public sealed record DiagnosticToolInfo(string Name, string Version);

/// <summary>
/// Stores effective profile settings suitable for export.
/// </summary>
public sealed record DiagnosticProfileSnapshot
{
    public required string Name { get; init; }

    public required string Fingerprint { get; init; }

    public required ProfileRoutes Routes { get; init; }

    public required string AuthenticationType { get; init; }

    public required ProfileTimeouts TimeoutsMs { get; init; }

    public required ProfileLimits Limits { get; init; }
}

/// <summary>
/// Identifies a speech request without retaining its input text.
/// </summary>
public sealed record DiagnosticRequestSnapshot
{
    public required string Fingerprint { get; init; }

    public required string Route { get; init; }

    public required string Model { get; init; }

    public required string Voice { get; init; }

    public required string ResponseFormat { get; init; }

    public required double Speed { get; init; }

    public required bool Stream { get; init; }

    public required int InputLength { get; init; }

    public required string InputSha256 { get; init; }
}

/// <summary>
/// Stores client-observed durations in milliseconds.
/// </summary>
public sealed record DiagnosticTimings(
    long TotalMs,
    long? SpeechHeadersMs,
    long? SpeechBodyMs,
    long? SpeechTotalMs);

/// <summary>
/// Records one direct connection attempt.
/// </summary>
public sealed record DiagnosticConnectionEvidence(
    string Address,
    int Port,
    DiagnosticCheckStatus Status,
    string Code,
    long DurationMs);

/// <summary>
/// Stores available client-side network observations.
/// </summary>
public sealed record DiagnosticNetworkEvidence(
    int Port,
    IReadOnlyList<string> ResolvedAddresses,
    IReadOnlyList<DiagnosticConnectionEvidence> ConnectionAttempts);

/// <summary>
/// Stores selected server claims when metadata is available.
/// </summary>
public sealed record DiagnosticServerEvidence(
    string? Service,
    string? Version,
    DiagnosticReadiness Readiness,
    IReadOnlyList<string> AdvertisedVoices)
{
    public string? IdentitySourceRoute { get; init; }

    public string? VersionSourceRoute { get; init; }

    public string? ReadinessSourceRoute { get; init; }

    public DateTimeOffset? ReadinessObservedAtUtc { get; init; }

    public string? VoicesSourceRoute { get; init; }

    public bool AdvertisedVoicesComplete { get; init; }

    public MetadataStatusSnapshot? StatusSnapshot { get; init; }

    public string? StatusSourceRoute { get; init; }

    public IReadOnlyList<MetadataObservation> Observations { get; init; } = [];
}

/// <summary>
/// Combines audio inspection with artifact retention information.
/// </summary>
public sealed record DiagnosticAudioResult(
    WavInspectionResult Inspection,
    bool Retained = false,
    string? SamplePath = null);

/// <summary>
/// Stores the canonical result of one diagnostic run.
/// </summary>
public sealed record DiagnosticRunResult
{
    public required int SchemaVersion { get; init; }

    public required string RunId { get; init; }

    public required DiagnosticToolInfo Tool { get; init; }

    public required DiagnosticMode Mode { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset FinishedAtUtc { get; init; }

    public required DiagnosticExecutionState ExecutionState { get; init; }

    public required DiagnosticOutcome Outcome { get; init; }

    public required ProfileTarget Target { get; init; }

    public required DiagnosticProfileSnapshot Profile { get; init; }

    public DiagnosticRequestSnapshot? Request { get; init; }

    public DiagnosticNetworkEvidence? Network { get; init; }

    public DiagnosticServerEvidence? Server { get; init; }

    public required DiagnosticTimings Timings { get; init; }

    public DiagnosticAudioResult? Audio { get; init; }

    public required IReadOnlyList<DiagnosticCheckResult> Checks { get; init; }

    [JsonIgnore]
    public byte[]? AudioPayload { get; init; }
}
