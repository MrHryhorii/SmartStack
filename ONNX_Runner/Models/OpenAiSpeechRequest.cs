using System.Text.Json.Serialization;

namespace ONNX_Runner.Models;

/// <summary>
/// Represents an incoming text-to-speech request.
/// Designed to be 100% compatible with the official OpenAI TTS API schema, 
/// while adding custom extensions for advanced Piper/VITS configurations and audio effects.
/// </summary>
public class OpenAiSpeechRequest
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
    /// If empty or "alloy", it defaults to the base Piper voice.
    /// </summary>
    [JsonPropertyName("voice")]
    public string Voice { get; set; } = "alloy";

    /// <summary>
    /// The format of the returned audio. 
    /// Supported formats: "wav", "mp3", "opus", "pcm". Defaults to "mp3".
    /// </summary>
    [JsonPropertyName("response_format")]
    public string ResponseFormat { get; set; } = "mp3";

    /// <summary>
    /// Generation speed multiplier. Ranges from 0.25 to 4.0. Default is 1.0.
    /// </summary>
    [JsonPropertyName("speed")]
    public float Speed { get; set; } = 1.0f;

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
    [JsonPropertyName("noise_scale")]
    public float? NoiseScale { get; set; }

    /// <summary>
    /// Variance of phoneme duration (Rhythm/Pacing). Typically ranges from 0.0 to 1.0.
    /// </summary>
    [JsonPropertyName("noise_w")]
    public float? NoiseW { get; set; }

    /// <summary>
    /// Specifies an artistic DSP effect to apply (e.g., "Overdrive", "Telephone").
    /// </summary>
    [JsonPropertyName("effect")]
    public string? Effect { get; set; }

    /// <summary>
    /// Controls the intensity of the chosen effect. Ranges from 0.0 (bypass) to 1.0 (maximum).
    /// </summary>
    [JsonPropertyName("effect_intensity")]
    public float? EffectIntensity { get; set; }
}