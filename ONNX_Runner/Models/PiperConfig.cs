using System.Text.Json.Serialization;

namespace ONNX_Runner.Models;

/// <summary>
/// Represents the official Piper TTS JSON configuration schema (e.g., voice.onnx.json).
/// </summary>
public class PiperConfig
{
    [JsonPropertyName("audio")]
    public AudioConfig Audio { get; set; } = new();

    [JsonPropertyName("espeak")]
    public EspeakConfig Espeak { get; set; } = new();

    [JsonPropertyName("inference")]
    public InferenceConfig Inference { get; set; } = new();

    /// <summary>
    /// The phoneme dictionary mapping. 
    /// Key: Phoneme string (e.g., "a", "t͡ʃ"). Value: Array of corresponding integer IDs.
    /// </summary>
    [JsonPropertyName("phoneme_id_map")]
    public Dictionary<string, int[]> PhonemeIdMap { get; set; } = [];
}

public class AudioConfig
{
    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; set; } = 22050; // Fallback default if missing
}

public class EspeakConfig
{
    /// <summary>
    /// The native eSpeak-ng voice/dialect code used to generate the phonetic transcription.
    /// </summary>
    [JsonPropertyName("voice")]
    public string Voice { get; set; } = "en";
}

public class InferenceConfig
{
    [JsonPropertyName("noise_scale")]
    public float NoiseScale { get; set; } = 0.667f;

    [JsonPropertyName("length_scale")]
    public float LengthScale { get; set; } = 1.0f;

    [JsonPropertyName("noise_w")]
    public float NoiseW { get; set; } = 0.8f;
}