namespace STT_Runner.Services;

/// <summary>Named segmentation policy shared by HTTP uploads and live PCM sessions.</summary>
public sealed class VadProfile
{
    public int PauseMs { get; set; } = 800;
    public int MinSpeechMs { get; set; } = 250;
    public int PrefixPaddingMs { get; set; } = 300;
    public int TailPaddingMs { get; set; } = 300;
    public float Threshold { get; set; } = 0.5f;
    public float? ExitThreshold { get; set; }
    public int SplitOverlapMs { get; set; } = 128;
    public int MaxSegmentSeconds { get; set; }
    public int MaxBufferedSegmentSeconds { get; set; } = 600;

    public static VadProfile Read(IConfiguration config)
    {
        string name = config["VadSettings:Profile"] ?? "segment";
        IConfigurationSection section = config.GetSection($"VadSettings:Profiles:{name}");
        if (!section.Exists()) throw new InvalidDataException($"Unknown VAD profile: {name}.");
        var profile = section.Get<VadProfile>() ?? new();
        if (profile.PauseMs is < 32 or > 5000 ||
            profile.MinSpeechMs is < 0 or > 5000 ||
            profile.PrefixPaddingMs is < 0 or > 5000 || profile.TailPaddingMs is < 0 or > 5000 ||
            !float.IsFinite(profile.Threshold) || profile.Threshold is < 0 or > 1 ||
            (profile.ExitThreshold is { } exit && (!float.IsFinite(exit) || exit < 0 || exit > profile.Threshold)) ||
            profile.SplitOverlapMs is < 0 or > 5000 ||
            profile.MaxBufferedSegmentSeconds is < 1 or > 3600 ||
            profile.MaxSegmentSeconds < 0 || profile.MaxSegmentSeconds > profile.MaxBufferedSegmentSeconds ||
            (profile.MaxSegmentSeconds > 0 && profile.SplitOverlapMs >= profile.MaxSegmentSeconds * 1000))
            throw new InvalidDataException($"Invalid VAD profile: {name}. Check duration, padding, overlap, and threshold ranges in ReadMe.md.");
        return profile;
    }
}
