using System.Text.Json.Serialization;

namespace ONNX_Runner.Models;

/// <summary>
/// Represents an incoming text-to-speech request to Tsubaki's own extended endpoint.
/// Carries every parameter the engine actually supports — the full set that used to live
/// directly on the OpenAI-compatible endpoint before it was split out to keep that one
/// strictly standards-compliant. Existing integrations that relied on the extended
/// parameters via the OpenAI endpoint only need to change their base URL to
/// <c>/tsbk/audio/speech</c> — the request body shape here is unchanged, since these were
/// never part of the official OpenAI schema to begin with.
/// </summary>
public class TsubakiSpeechRequest
{
    /// <summary>
    /// The model to use (e.g., "tts-1"). 
    /// Currently ignored as the server relies on the single locally loaded Piper model.
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "tts-1";

    /// <summary>
    /// The text to synthesize into audio.
    /// </summary>
    [JsonPropertyName("input")]
    public required string Input { get; set; }

    /// <summary>
    /// The voice to use. For OpenVoice cloning, this should match a saved voice fingerprint name.
    /// If empty or "piper_base", it defaults to the base Piper voice.
    /// </summary>
    [JsonPropertyName("voice")]
    public string Voice { get; set; } = "piper_base";

    /// <summary>
    /// The format of the returned audio. 
    /// Supported formats: "wav", "mp3", "opus", "pcm". Defaults to "mp3".
    /// </summary>
    [JsonPropertyName("response_format")]
    public string ResponseFormat { get; set; } = "mp3";

    /// <summary>
    /// Generation speed multiplier. Ranges from 0.25 to 4.0. Default is 1.0.
    /// </summary>
    private float _speed = 1.0f;
    [JsonPropertyName("speed")]
    public float Speed
    {
        get => _speed;
        // Clamp the value to a reasonable range to prevent extreme settings that could break the model.
        set => _speed = Math.Clamp(value, 0.25f, 4.0f);
    }

    // =====================================================================
    // CUSTOM EXTENSIONS (Piper/VITS & Server Specific)
    // =====================================================================

    /// <summary>
    /// Overrides the server's default streaming behavior.
    /// True = Chunked Transfer Encoding (stream). False = Wait for full file.
    /// </summary>
    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    /// <summary>
    /// Variance of pitch/intonation (Expression). Typically ranges from 0.0 to 1.0.
    /// </summary>
    private float? _noiseScale;
    [JsonPropertyName("noise_scale")]
    public float? NoiseScale
    {
        get => _noiseScale;
        // Clamp the value to a reasonable range (0.0 to 1.0) to prevent extreme settings that could break the model.
        set => _noiseScale = value.HasValue ? Math.Clamp(value.Value, 0f, 1.0f) : null;
    }

    /// <summary>
    /// Variance of phoneme duration (Rhythm/Pacing). Typically ranges from 0.0 to 1.0.
    /// </summary>
    private float? _noiseW;
    [JsonPropertyName("noise_w")]
    public float? NoiseW
    {
        get => _noiseW;
        // Clamp the value to a reasonable range (0.0 to 1.0) to prevent extreme settings that could break the model.
        set => _noiseW = value.HasValue ? Math.Clamp(value.Value, 0f, 1.0f) : null;
    }

    /// <summary>
    /// Specifies an artistic DSP effect to apply (e.g., "Overdrive", "Telephone").
    /// </summary>
    [JsonPropertyName("effect")]
    public string? Effect { get; set; }

    /// <summary>
    /// Controls the intensity of the chosen effect. 
    /// Ranges from 0.0 (bypass) to 1.0 (maximum). 
    /// The server may apply internal scaling based on the effect type, 
    /// so this is a relative intensity control rather than a direct parameter for the underlying DSP algorithm.
    /// </summary>
    private float? _effectIntensity;
    [JsonPropertyName("effect_intensity")]
    public float? EffectIntensity
    {
        get => _effectIntensity;
        // Clamp the value to a reasonable range (0.0 to 1.0) to prevent extreme settings that
        // could break the model or cause excessive distortion.
        set => _effectIntensity = value.HasValue ? Math.Clamp(value.Value, 0.0f, 1.0f) : null;
    }

    /// <summary>
    /// Specifies an acoustic spatial environment to apply (e.g., "LivingRoom", "ConcreteHall").
    /// </summary>
    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    /// <summary>
    /// Controls the intensity of the spatial environment. Ranges from 0.0 (bypass) to 1.0 (maximum).
    /// </summary>
    private float? _environmentIntensity;
    [JsonPropertyName("environment_intensity")]
    public float? EnvironmentIntensity
    {
        get => _environmentIntensity;
        // Clamp the value to a reasonable range (0.0 to 1.0) to prevent extreme settings 
        // that could break the model or cause excessive reverb.
        set => _environmentIntensity = value.HasValue ? Math.Clamp(value.Value, 0.0f, 1.0f) : null;
    }

    /// <summary>
    /// Pitch shift factor. 
    /// 1.0 = original, >1.0 = higher pitch, <1.0 = lower pitch.
    /// </summary>
    private float? _pitch;
    [JsonPropertyName("pitch")]
    public float? Pitch
    {
        get => _pitch;
        // Clamp the value to a safe range (0.5 to 2.0) to prevent extreme audio distortion or algorithm failure.
        set => _pitch = value.HasValue ? Math.Clamp(value.Value, 0.5f, 2.0f) : null;
    }

    /// <summary>
    /// Volume multiplier. 
    /// 1.0 = original, <1.0 = quieter, >1.0 = louder.
    /// </summary>
    private float? _volume;
    [JsonPropertyName("volume")]
    public float? Volume
    {
        get => _volume;
        // Clamp from 0.0 (mute) to 4.0 (+12dB boost)
        set => _volume = value.HasValue ? Math.Clamp(value.Value, 0.0f, 4.0f) : null;
    }

    /// <summary>
    /// Forces the engine to use a specific language code, bypassing automatic language detection.
    /// If the requested base language matches the loaded model's base language, the full model dialect is used.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Intensity of voice cloning. 
    /// 1.0 = Exact copy of the target voice (Standard).
    /// </summary>
    private float? _cloneIntensity;
    [JsonPropertyName("clone_intensity")]
    public float? CloneIntensity
    {
        get => _cloneIntensity;
        // Clamp the value to a reasonable range (0.0 to 2.0) to prevent extreme settings that could break the model.
        set => _cloneIntensity = value.HasValue ? Math.Clamp(value.Value, 0.0f, 2.0f) : null;
    }

    /// <summary>
    /// Tau parameter for adjusting tone diversity. 
    /// 1.0 = Standard. < 1.0 = More conservative/stable. > 1.0 = More expressive/diverse.
    /// </summary>
    private float? _toneTemperature;
    [JsonPropertyName("tone_temperature")]
    public float? ToneTemperature
    {
        get => _toneTemperature;
        // Clamp the value to a reasonable range (0.1 to 2.0) to prevent extreme settings that could break the model.
        set => _toneTemperature = value.HasValue ? Math.Clamp(value.Value, 0.1f, 2.0f) : null;
    }

    /// <summary>
    /// Per-request override of the Low-Pass Filter Q-Factor (resonance/roll-off curve).
    /// Only applies if Voice Cloning is active and the low-pass filter is enabled on the server.
    /// 0.577 = Bessel curve (smooth analog warmth, recommended).
    /// 0.707 = Butterworth curve (brighter, sharper cutoff).
    /// </summary>
    private float? _lowPassQFactor;
    [JsonPropertyName("low_pass_q_factor")]
    public float? LowPassQFactor
    {
        get => _lowPassQFactor;
        // Clamp to a safe and physically meaningful range for this specific anti-aliasing application.
        // Values above 1.0 start introducing resonant peaks (ringing), which defeats the purpose of smoothing.
        set => _lowPassQFactor = value.HasValue ? Math.Clamp(value.Value, 0.1f, 1.0f) : null;
    }
}
