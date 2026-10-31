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

    /// <summary>Three fixed harmony voices with preserved formants and measured-power intensity mixing.</summary>
    Harmonizer,

    /// <summary>Zero-phase spectral robotization with aligned, measured-power intensity mixing.</summary>
    Robot,

    /// <summary>Short modulated delay with feedback (classic flanger effect).</summary>
    Flanger,

    /// <summary>Long modulated delay for a thick multi-voice effect.</summary>
    Chorus,

    /// <summary>Repeats short audio fragments to simulate a digital decoder glitch.</summary>
    DecoderGlitch,

    /// <summary>Military-grade CVSD codec simulation with heavy compression and slope-overload distortion.</summary>
    TacticalRadio,

    /// <summary>Simulates the warmth and coloration of analog cassette tape.</summary>
    LoFiTape,

    /// <summary>Analogue voice-radio audio approximation with fixed filtering and shared final peak protection.</summary>
    FmRadio,

    /// <summary>G.711 μ-law telephony companding; finer quantization near silence than A-law.</summary>
    G711MuLaw,

    /// <summary>G.711 A-law telephony companding; a different quantization grid with similar telephone coloration.</summary>
    G711ALaw
}

/// <summary>
/// Global configuration for the Audio Effects Engine.
/// </summary>
public class EffectsSettings
{
    /// <summary>Master switch for equalizer profiles, character effects, and spatial environments, including reverb tails.</summary>
    public bool EnableGlobalEffects { get; set; } = true;

    /// <summary>Ordered playback profiles inherited by omitted/null request arrays; empty arrays bypass the layer.</summary>
    public string?[]? DefaultEqualizer { get; set; } = [];

    // Character Effects (Voice)
    public string DefaultEffect { get; set; } = "None";
    public float DefaultIntensity { get; set; } = 1.0f;

    // Spatial Effects (Environment)
    public string DefaultEnvironment { get; set; } = "None";
    public float DefaultEnvironmentIntensity { get; set; } = 0.25f;

    /// <summary>
    /// Allows the active spatial reverb tail to decay naturally to silence instead of cutting off abruptly.
    /// Only applies to spatial environments (e.g., Cave, ConcreteHall); character effects remain unaffected.
    /// </summary>
    public bool ExtendReverbTailOnFinish { get; set; } = true;

    /// <summary>
    /// The amplitude threshold (0.0 to 1.0) at which the spatial reverb tail is considered silent
    /// and safely cut off. 0.005 (~-46 dBFS) is the default perceptual silence threshold. 
    /// Lower values (e.g., 0.001) yield mathematically perfect tails but longer audio files.
    /// Higher values (e.g., 0.01) cut the tail faster, suitable for interactive or game audio.
    /// </summary>
    public float ReverbTailSilenceFloor { get; set; } = 0.005f;
}

/// <summary>
/// Available server-side audio effects for post-processing spatial audio generation.
/// </summary>
public enum SpatialEnvironment
{
    // --- Default & Everyday ---

    /// <summary>No reverb; dry signal only. Highest performance mode.</summary>
    None,

    /// <summary>Small room with short, clear reflections and less high-frequency absorption.</summary>
    Room,

    /// <summary>Furnished room with short, soft reflections and strong high-frequency absorption.</summary>
    LivingRoom,

    // --- Public Spaces ---

    /// <summary>Wooden performance hall with warm, delayed reflections.</summary>
    Stage,

    /// <summary>Large concert hall with a long, dense reflected tail.</summary>
    ConcreteHall,

    // --- Adventure & Nature ---

    /// <summary>Compact stone chamber with dense, short reflections.</summary>
    Dungeon,

    /// <summary>Reflective rocky cavern with a longer decay.</summary>
    Cave,

    /// <summary>Open outdoor space. Discrete echoes rather than dense reverb tails; simulates natural canyon or forest acoustics.</summary>
    Forest,

    // --- Extreme & Narrative ---

    /// <summary>Occlusion effect. Pure low-pass filter with no spatial reflections. Simulates hearing sound through walls, earplugs, or UI pause menus.</summary>
    Muffled,

    /// <summary>Muffled underwater space. Significant high-frequency roll-off and characteristic "slapback" echo.</summary>
    Underwater,

    /// <summary>Intracranial acoustic space. Short tap delays simulate bone-conducted sound; used for internal thoughts.</summary>
    InnerVoice
}
