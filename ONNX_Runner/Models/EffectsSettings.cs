namespace ONNX_Runner.Models;

/// <summary>
/// Available server-side audio effects for post-processing voice generation.
/// </summary>
public enum VoiceEffectType
{
    None,

    /// <summary>Lo-Fi equalization with hard transistor clipping.</summary>
    Telephone,

    /// <summary>Warm tube saturation and cubic waveshaping distortion.</summary>
    Overdrive,

    /// <summary>Bit-depth reduction and sample rate decimation (Retro 8-bit / Arcade effect).</summary>
    Bitcrusher,

    /// <summary>Fast sine wave multiplication (Classic Robot / Dalek effect).</summary>
    RingModulator,

    /// <summary>Modulated short delay with heavy feedback (Metallic space tube effect).</summary>
    Flanger,

    /// <summary>Modulated long delay (Thick, multi-voice ensemble effect).</summary>
    Chorus
}

/// <summary>
/// Global configuration for the Audio Effects Engine.
/// </summary>
public class EffectsSettings
{
    public bool EnableGlobalEffects { get; set; } = true;
    public string DefaultEffect { get; set; } = "None";
    public float DefaultIntensity { get; set; } = 1.0f;
}