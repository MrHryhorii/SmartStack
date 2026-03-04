using System.Text.Json.Serialization;

namespace ONNX_Runner.Models;

public class OpenAiSpeechRequest
{
    /// <summary>
    /// Модель для синтезу (наприклад, tts-1). Поки ігноруємо, бо маємо лише одну локальну модель.
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "tts-1";

    /// <summary>
    /// Текст, який потрібно озвучити. Максимум 4096 символів в оригінальному API.
    /// </summary>
    [JsonPropertyName("input")]
    public required string Input { get; set; }

    /// <summary>
    /// Голос (alloy, echo, fable, onyx, nova, shimmer). Поки ігноруємо.
    /// </summary>
    [JsonPropertyName("voice")]
    public string Voice { get; set; } = "alloy";

    /// <summary>
    /// Формат аудіо. OpenAI за замовчуванням віддає mp3. 
    /// Нам поки що найпростіше віддавати wav (через NAudio), але пізніше можна додати конвертацію в mp3, якщо клієнт вимагатиме саме його.
    /// </summary>
    [JsonPropertyName("response_format")]
    public string ResponseFormat { get; set; } = "wav";

    /// <summary>
    /// Швидкість генерації від 0.25 до 4.0. За замовчуванням 1.0.
    /// </summary>
    [JsonPropertyName("speed")]
    public float Speed { get; set; } = 1.0f;
}