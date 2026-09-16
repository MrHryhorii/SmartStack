using System.Text.Json.Serialization;

namespace ONNX_Runner.Models;

/// <summary>
/// Represents an incoming OpenAI-compatible text-to-speech request.
/// Tsubaki accepts the standard OpenAI TTS fields it can meaningfully support, plus the
/// optional de facto <c>stream</c> extension used by many AI clients and frontends.
/// Tsubaki-specific DSP/cloning controls live on <see cref="TsubakiSpeechRequest"/> instead.
/// </summary>
public class OpenAiSpeechRequest
{
    /// <summary>
    /// The model to use (e.g., "tts-1").
    /// Currently ignored because the server uses the locally loaded Piper model.
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "tts-1";

    /// <summary>
    /// The text to synthesize into audio.
    /// </summary>
    [JsonPropertyName("input")]
    public required string Input { get; set; }

    /// <summary>
    /// Voice selector. The canonical form is a string voice ID.
    /// Compatibility forms are normalized by VoiceIdJsonConverter.
    /// </summary>
    [JsonPropertyName("voice")]
    [JsonConverter(typeof(VoiceIdJsonConverter))]
    public string Voice { get; set; } = "piper_base";

    /// <summary>
    /// The requested response representation.
    /// Tsubaki currently supports "wav", "mp3", "opus", "pcm", and the optional
    /// Tsubaki extension "b64_json". AAC and FLAC are not implemented yet.
    /// </summary>
    [JsonPropertyName("response_format")]
    public string ResponseFormat { get; set; } = "mp3";

    /// <summary>
    /// Optional OpenAI model instruction for speaking style.
    /// Accepted for request compatibility but intentionally ignored because Piper/OpenVoice
    /// do not expose an equivalent natural-language style-control input.
    /// </summary>
    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    /// <summary>
    /// Generation speed multiplier. OpenAI defines a range from 0.25 to 4.0.
    /// </summary>
    private float _speed = 1.0f;

    [JsonPropertyName("speed")]
    public float Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 0.25f, 4.0f);
    }

    /// <summary>
    /// Official OpenAI streaming representation selector.
    /// "audio" is accepted and uses Tsubaki's normal audio response path.
    /// "sse" is parsed for compatibility but rejected by the adapter until SSE framing
    /// is implemented, rather than silently returning the wrong wire format.
    /// </summary>
    [JsonPropertyName("stream_format")]
    public string? StreamFormat { get; set; }

    /// <summary>
    /// Overrides Tsubaki's server-side streaming default.
    /// This is not part of the official OpenAI TTS request schema, but is widely used
    /// as a de facto extension by AI agents and frontends.
    /// </summary>
    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }
}
