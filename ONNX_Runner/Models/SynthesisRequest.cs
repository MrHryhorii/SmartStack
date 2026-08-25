namespace ONNX_Runner.Models;

/// <summary>
/// Wire-format-agnostic representation of a text-to-speech request. Every external API
/// shape (OpenAI, Tsubaki's own extended endpoint, and any future ones) maps its own DTO
/// into this one via a small adapter, so SpeechSynthesisService and the generation
/// pipeline it drives never need to know which endpoint a request actually arrived through.
/// </summary>
public class SynthesisRequest
{
    /// <summary>
    /// The text to synthesize into audio. Mutable rather than init-only because
    /// SpeechSynthesisService truncates this in place under MaxTextLength (OOM protection),
    /// mirroring how the original endpoint mutated OpenAiSpeechRequest.Input directly.
    /// </summary>
    public required string Input { get; set; }

    /// <summary>
    /// Already-resolved output audio format. Parsing the wire-level format string (which
    /// may use different naming per external API) is the adapter's job, not the service's.
    /// </summary>
    public required AudioFormat Format { get; set; }

    /// <summary>
    /// The voice to use. For OpenVoice cloning, this should match a saved voice fingerprint name.
    /// If empty or "piper_base", it defaults to the base Piper voice.
    /// </summary>
    public required string Voice { get; set; }

    /// <summary>
    /// Generation speed multiplier. Adapters are responsible for clamping this to a safe
    /// range before it reaches here, same as the original OpenAiSpeechRequest.Speed setter did.
    /// </summary>
    public required float Speed { get; set; }

    /// <summary>
    /// Overrides the server's default streaming behavior.
    /// True = Chunked Transfer Encoding (stream). False = Wait for full file.
    /// </summary>
    public bool? Stream { get; set; }

    /// <summary>
    /// Variance of pitch/intonation (Expression). Typically ranges from 0.0 to 1.0.
    /// </summary>
    public float? NoiseScale { get; set; }

    /// <summary>
    /// Variance of phoneme duration (Rhythm/Pacing). Typically ranges from 0.0 to 1.0.
    /// </summary>
    public float? NoiseW { get; set; }

    /// <summary>
    /// Specifies an artistic DSP effect to apply (e.g., "Overdrive", "Telephone").
    /// </summary>
    public string? Effect { get; set; }

    /// <summary>
    /// Controls the intensity of the chosen effect. Ranges from 0.0 (bypass) to 1.0 (maximum).
    /// </summary>
    public float? EffectIntensity { get; set; }

    /// <summary>
    /// Specifies an acoustic spatial environment to apply (e.g., "LivingRoom", "ConcreteHall").
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>
    /// Controls the intensity of the spatial environment. Ranges from 0.0 (bypass) to 1.0 (maximum).
    /// </summary>
    public float? EnvironmentIntensity { get; set; }

    /// <summary>
    /// Pitch shift factor. 1.0 = original, >1.0 = higher pitch, <1.0 = lower pitch.
    /// </summary>
    public float? Pitch { get; set; }

    /// <summary>
    /// Volume multiplier. 1.0 = original, <1.0 = quieter, >1.0 = louder.
    /// </summary>
    public float? Volume { get; set; }

    /// <summary>
    /// Intensity of voice cloning. 1.0 = Exact copy of the target voice (Standard).
    /// If provided, this value overrides the server's default CloneIntensity from the configuration 
    /// for this specific request.
    /// </summary>
    public float? CloneIntensity { get; set; }

    /// <summary>
    /// Tau parameter for adjusting tone diversity. 1.0 = Standard. < 1.0 = More stable. > 1.0 = More expressive.
    /// If provided, this value overrides the server's default ToneTemperature from the configuration 
    /// for this specific request.
    /// </summary>
    public float? ToneTemperature { get; set; }
}
