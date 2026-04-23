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
    Chorus,

    /// <summary> Simulates the characteristic warmth and coloration of analog cassette tape. </summary>
    LoFiTape
}

/// <summary>
/// Global configuration for the Audio Effects Engine.
/// </summary>
public class EffectsSettings
{
    public bool EnableGlobalEffects { get; set; } = true;

    // Character Effects (Voice)
    public string DefaultEffect { get; set; } = "None";
    public float DefaultIntensity { get; set; } = 1.0f;

    // Spatial Effects (Environment)
    public string DefaultEnvironment { get; set; } = "None";
    public float DefaultEnvironmentIntensity { get; set; } = 1.0f;
}

/// <summary>
/// Available server-side audio effects for post-processing spatial audio generation.
/// </summary>
public enum SpatialEnvironment
{
    None,           // No reverb, dry signal only.
    LivingRoom,     // Small room with short, bright reverb.
    ConcreteHall,   // Large hall with long, dense reverb and strong early reflections.
    Forest,         // Open outdoor space with long, diffuse reverb and minimal early reflections.
    Underwater      // Underwater environment with unique acoustic properties.
}