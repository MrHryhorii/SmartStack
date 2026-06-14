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
    DecoderGlitch,

    /// <summary>Military-grade CVSD codec simulation with heavy compression and slope-overload distortion.</summary>
    TacticalRadio,

    /// <summary>Civilian handheld radio effect with an FM amplitude limiter and signal-dependent noise floor.</summary>
    FmRadio,

    /// <summary>Classic US telephony codec (μ-law) creating a gritty, retro 8-bit mainframe AI voice.</summary>
    G711MuLaw,

    /// <summary>European telephony codec (A-law) creating a sterile, clinical cyborg or droid voice with dead silences.</summary>
    G711ALaw
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
    /// <summary>No reverb; dry signal only. Highest performance mode.</summary>
    None,

    /// <summary>Small, intimate room. Short decay and balanced frequency response; natural for dialogue.</summary>
    LivingRoom,

    /// <summary>Large reflective hall. Long, dense reverb tail with strong early reflections; adds scale.</summary>
    ConcreteHall,

    /// <summary>Open outdoor space. Discrete echoes rather than dense reverb tails; simulates natural canyon or forest acoustics.</summary>
    Forest,

    /// <summary>Muffled underwater space. Significant high-frequency roll-off and characteristic "slapback" echo.</summary>
    Underwater,

    /// <summary>Large enclosed stone space. High reflectivity with very long, dark decay; emphasizes deep resonance.</summary>
    Cave,

    /// <summary>Performance stage. Distinct pre-delay simulates stage-to-audience projection; clean but spacious.</summary>
    Stage,

    /// <summary>Intracranial acoustic space. Short tap delays simulate bone-conducted sound; used for internal thoughts.</summary>
    InnerVoice,

    /// <summary>Tight stone space. Short, dark reverb characterized by distinct flutter echoes between parallel walls.</summary>
    Dungeon
}