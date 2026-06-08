namespace ONNX_Runner.Models;

/// <summary>
/// Available server-side audio effects for post-processing voice generation.
/// </summary>
public enum VoiceEffectType
{
    /// <summary>No effect, clean voice output.</summary>
    None,

    /// <summary>Lo-fi telephone equalization with transistor-style clipping.</summary>
    Telephone,

    /// <summary>Warm tube-style saturation and harmonic distortion.</summary>
    Overdrive,

    /// <summary>Bit-depth reduction and sample-rate decimation (retro 8-bit effect).</summary>
    Bitcrusher,

    /// <summary>Sine-wave amplitude modulation (classic robot / Dalek effect).</summary>
    RingModulator,

    /// <summary>Short modulated delay with feedback (classic flanger effect).</summary>
    Flanger,

    /// <summary>Long modulated delay for a thick multi-voice effect.</summary>
    Chorus,

    /// <summary>Simulates the warmth and coloration of analog cassette tape.</summary>
    LoFiTape,

    /// <summary>Repeats short audio fragments to simulate a digital decoder glitch.</summary>
    DecoderGlitch
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