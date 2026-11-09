using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Http;

namespace Tsubaki.ServiceCheck.Core.Metadata;

/// <summary>Describes whether a response contains recognized metadata.</summary>
public enum MetadataDocumentState
{
    Unavailable,
    Recognized,
    Unrecognized,
    Invalid
}

/// <summary>Records a limitation in parsed metadata.</summary>
public sealed record MetadataWarning(string Code, string Summary);

/// <summary>Stores advertised voice IDs and whether the retained list is complete.</summary>
public sealed record MetadataVoiceList(
    IReadOnlyList<string> Ids,
    bool Complete);

/// <summary>Stores selected server-reported rate-limit settings.</summary>
public sealed record MetadataRateLimitSnapshot(
    int? PermitLimit,
    double? WindowSeconds,
    int? QueueLimit);

/// <summary>Stores an allowlist of server-reported capabilities and defaults.</summary>
public sealed record MetadataStatusSnapshot
{
    public string? BaseVoiceDialect { get; init; }

    public int? SampleRateHz { get; init; }

    public bool? VoiceCloningEnabled { get; init; }

    public bool? VoiceCloningAvailable { get; init; }

    public bool? WavAvailable { get; init; }

    public int? DefaultSampleRate { get; init; }

    public double? DefaultPitch { get; init; }

    public double? DefaultVolume { get; init; }

    public double? VolumeBoosterDb { get; init; }

    public double? DefaultTotalGainDb { get; init; }

    /// <summary>Identifies a valid text-limit claim, including an explicit null.</summary>
    public bool TextLimitKnown { get; init; }

    public int? MaxTextLength { get; init; }

    public MetadataRateLimitSnapshot? RateLimit { get; init; }
}

/// <summary>Preserves one metadata observation with its source and client timestamp.</summary>
public sealed record MetadataObservation
{
    public required MetadataEndpoint Endpoint { get; init; }

    public required MetadataDocumentState State { get; init; }

    public required string Code { get; init; }

    public required string Summary { get; init; }

    public required DateTimeOffset ObservedAtUtc { get; init; }

    public required DiagnosticHttpEvidence Http { get; init; }

    /// <summary>Indicates that the reported service label matched the expected value.</summary>
    public bool ServiceIdentityMatched { get; init; }

    public string? Service { get; init; }

    public string? Version { get; init; }

    public string? Status { get; init; }

    public bool? SynthesisReady { get; init; }

    public DateTimeOffset? ServerTimestampUtc { get; init; }

    public MetadataVoiceList? Voices { get; init; }

    public MetadataStatusSnapshot? StatusSnapshot { get; init; }

    public IReadOnlyList<MetadataWarning> Warnings { get; init; } = [];
}
