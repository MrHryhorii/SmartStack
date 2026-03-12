using System.Text.Json.Serialization;

namespace ONNX_Runner.Models;

public class PiperConfig
{
    [JsonPropertyName("audio")]
    public AudioConfig Audio { get; set; } = new();

    [JsonPropertyName("espeak")]
    public EspeakConfig Espeak { get; set; } = new();

    [JsonPropertyName("inference")]
    public InferenceConfig Inference { get; set; } = new();

    // Карта фонем: ключ - символ (рядок), значення - масив ID (зазвичай з 1 елемента)
    [JsonPropertyName("phoneme_id_map")]
    public Dictionary<string, int[]> PhonemeIdMap { get; set; } = new();
}

public class AudioConfig
{
    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; set; } = 22050; // Дефолтне значення, якщо раптом немає
}

public class EspeakConfig
{
    [JsonPropertyName("voice")]
    public string Voice { get; set; } = "en"; // Голос для espeak
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