namespace ONNX_Runner.Models.Audio;

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
