namespace Tsubaki.ServiceCheck.Core.Profiles;

/// <summary>Defines a versioned configuration for a diagnostic target.</summary>
public sealed record TargetProfile
{
    public required int SchemaVersion { get; init; }
    public required string Name { get; init; }
    public required ProfileTarget Target { get; init; }

    public ProfileRoutes Routes { get; init; } = new();
    public ProfileAuthentication Authentication { get; init; } = new();
    public ProfileSynthesis Synthesis { get; init; } = new();
    public ProfileTimeouts TimeoutsMs { get; init; } = new();
    public ProfileLimits Limits { get; init; } = new();
    public ProfileOutput Output { get; init; } = new();
}

/// <summary>Identifies the configured server endpoint.</summary>
public sealed record ProfileTarget
{
    public required string Id { get; init; }
    public required string BaseUrl { get; init; }
}

/// <summary>Defines routes relative to the configured service root.</summary>
public sealed record ProfileRoutes
{
    public string Health { get; init; } = "health";
    public string HealthFallback { get; init; } = "v1/health";
    public string Status { get; init; } = "tsbk/server/status";
    public string Voices { get; init; } = "tsbk/audio/voices";
    public string Speech { get; init; } = "v1/audio/speech";
}

/// <summary>Configures authentication without storing credential values.</summary>
public sealed record ProfileAuthentication
{
    public string Type { get; init; } = "none";
    public string? TokenEnvironmentVariable { get; init; }
}

/// <summary>Defines the diagnostic speech request settings.</summary>
public sealed record ProfileSynthesis
{
    public string Model { get; init; } = "tts-1";
    public string Voice { get; init; } = "piper_base";
    public string Input { get; init; } = "This is a Tsubaki service check.";
    public string ResponseFormat { get; init; } = "wav";
    public bool Stream { get; init; }
    public double Speed { get; init; } = 1.0;
}

/// <summary>Defines operation deadlines in milliseconds.</summary>
public sealed record ProfileTimeouts
{
    public int Dns { get; init; } = 3_000;
    public int Tcp { get; init; } = 3_000;
    public int Metadata { get; init; } = 5_000;
    public int Speech { get; init; } = 60_000;
    public int Run { get; init; } = 90_000;
}

/// <summary>Bounds response sizes and diagnostic input length.</summary>
public sealed record ProfileLimits
{
    public int MaxMetadataBytes { get; init; } = 1_048_576;
    public int MaxAudioBytes { get; init; } = 10_485_760;
    public int MaxInputCharacters { get; init; } = 200;
}

/// <summary>Controls retention of diagnostic audio.</summary>
public sealed record ProfileOutput
{
    public bool RetainAudio { get; init; } = true;
}