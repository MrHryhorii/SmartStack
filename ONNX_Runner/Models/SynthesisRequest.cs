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
    /// Forces the engine to apply specific phonetic rules, bypassing the automatic Lingua language detector.
    /// Accepts standard base language codes (e.g., "en", "fr") or extended eSpeak dialect tags (e.g., "en-us", "fr-ca").
    /// If the base family of the provided code matches the base family of the currently loaded Piper model 
    /// (e.g., passing "en" when the model is "en-gb-x-rp"), the engine automatically upgrades the request to use 
    /// the model's full native dialect string to prevent phonetic dictionary conflicts.
    /// </summary>
    public string? Language { get; set; }

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

    /// <summary>
    /// Anti-aliasing low-pass filter to clean up cloning artifacts
    /// Only applies if Voice Cloning is active and the low-pass filter is enabled on the server.
    /// 0.577 = Bessel curve (smooth analog warmth, recommended).
    /// 0.707 = Butterworth curve (brighter, sharper cutoff).
    /// </summary>
    public float? LowPassQFactor { get; set; }

    /// <summary>
    /// Extends generation until the active spatial environment's reverb tail naturally fades below audibility.
    /// Overrides <c>ExtendReverbTailOnFinish</c> for this request. Does not affect character voice effects.
    /// </summary>
    public bool? ExtendReverbTail { get; set; }
}
