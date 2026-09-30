namespace STT_Runner.Services;

/// <summary>Bounded, causal level control shared by file and microphone inputs.</summary>
public sealed class AudioNormalizationSettings
{
    public bool Enabled { get; set; } = true;
    public float TargetRmsDbfs { get; set; } = -20;
    public float NoiseFloorDbfs { get; set; } = -70;
    public float MaxGainDb { get; set; } = 30;
    public float MinGainDb { get; set; } = -18;
    public float PeakDbfs { get; set; } = -1;
    public int RmsWindowMs { get; set; } = 250;
    public int AttackMs { get; set; } = 100;
    public int ReleaseMs { get; set; } = 1000;

    public static AudioNormalizationSettings Read(IConfiguration config)
    {
        var value = config.GetSection("AudioNormalization").Get<AudioNormalizationSettings>() ?? new();
        if (!float.IsFinite(value.TargetRmsDbfs) || value.TargetRmsDbfs is < -40 or > -6 ||
            !float.IsFinite(value.NoiseFloorDbfs) || value.NoiseFloorDbfs is < -90 or > -20 ||
            value.NoiseFloorDbfs >= value.TargetRmsDbfs ||
            !float.IsFinite(value.MaxGainDb) || value.MaxGainDb is < 0 or > 30 ||
            !float.IsFinite(value.MinGainDb) || value.MinGainDb is < -60 or > 0 ||
            !float.IsFinite(value.PeakDbfs) || value.PeakDbfs is < -12 or > 0 ||
            value.TargetRmsDbfs >= value.PeakDbfs ||
            value.RmsWindowMs is < 32 or > 5000 ||
            value.AttackMs is < 1 or > 5000 || value.ReleaseMs is < 1 or > 10000)
            throw new InvalidDataException("Invalid AudioNormalization settings. Check level and time ranges in ReadMe.md.");
        return value;
    }
}
