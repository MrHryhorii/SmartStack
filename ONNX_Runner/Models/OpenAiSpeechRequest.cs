using System.Text.Json.Serialization;

namespace ONNX_Runner.Models;

/// <summary>
/// Represents an incoming text-to-speech request.
/// Matches the official OpenAI TTS API schema, with the addition of the widely accepted 
/// 'stream' parameter used by the broader AI ecosystem.
/// For Tsubaki's extended parameters (DSP effects, cloning tuning, etc.),
/// see <see cref="TsubakiSpeechRequest"/> and the <c>/tsbk/audio/speech</c> endpoint instead.
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

    /// <summary>
    /// Overrides the server's default streaming behavior.
    /// Not in official OpenAI TTS spec, but widely used as a de facto standard by AI agents and frontends.
    /// </summary>
    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }
}