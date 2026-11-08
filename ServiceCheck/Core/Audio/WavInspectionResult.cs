namespace Tsubaki.ServiceCheck.Core.Audio;

/// <summary>Describes structural validity and supported WAV coverage.</summary>
public enum WavContainerState
{
    Valid,
    Invalid,
    Unsupported
}

/// <summary>Describes the observed PCM sample signal.</summary>
public enum AudioSignalState
{
    NotChecked,
    Nonzero,
    Silent
}

/// <summary>Records an audio observation that does not fail validation.</summary>
public sealed record AudioInspectionWarning(string Code, string Summary);

/// <summary>Captures WAV structure, signal metrics, and artifact identity.</summary>
public sealed record WavInspectionResult
{
    public required WavContainerState ContainerState { get; init; }
    public required AudioSignalState SignalState { get; init; }
    public required string Code { get; init; }
    public required string Summary { get; init; }

    public required long PayloadBytes { get; init; }
    public required string PayloadSha256 { get; init; }
    public required long InspectionDurationMs { get; init; }

    public ushort? FormatCode { get; init; }
    public ushort? Channels { get; init; }
    public uint? SampleRateHz { get; init; }
    public ushort? BitsPerSample { get; init; }
    public ushort? BlockAlign { get; init; }
    public uint? ByteRate { get; init; }

    public long? DataBytes { get; init; }
    public long? FrameCount { get; init; }
    public double? DurationSeconds { get; init; }

    public double? PeakAbsolute { get; init; }
    public double? Rms { get; init; }
    public double? PeakDbfs { get; init; }
    public double? RmsDbfs { get; init; }
    public double? NearFullScaleFraction { get; init; }

    public IReadOnlyList<AudioInspectionWarning> Warnings { get; init; } = [];
}