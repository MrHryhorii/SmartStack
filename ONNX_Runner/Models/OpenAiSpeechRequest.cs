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
    /// Нам поки що найпростіше віддавати wav (через NAudio).
    /// </summary>
    [JsonPropertyName("response_format")]
    public string ResponseFormat { get; set; } = "mp3";

    /// <summary>
    /// Швидкість генерації від 0.25 до 4.0. За замовчуванням 1.0.
    /// </summary>
    [JsonPropertyName("speed")]
    public float Speed { get; set; } = 1.0f;

    // --- НАШІ РОЗШИРЕНІ ПАРАМЕТРИ (Piper/VITS) ---

    /// <summary>
    /// [Custom Extension] Перевизначає налаштування сервера.
    /// Якщо true - сервер повертатиме потік chunked. Якщо false - цілий файл.
    /// Якщо null - використовується налаштування сервера.
    /// </summary>
    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    /// <summary>
    /// [Необов'язково] Варіативність висоти тону (експресія). Зазвичай від 0 до 1.
    /// </summary>
    [JsonPropertyName("noise_scale")]
    public float? NoiseScale { get; set; }

    /// <summary>
    /// [Необов'язково] Варіативність тривалості фонем (ритмічність). Зазвичай від 0 до 1.
    /// </summary>
    [JsonPropertyName("noise_w")]
    public float? NoiseW { get; set; }

    /// <summary>
    /// [Необов'язково] Параметр, що контролює інтенсивність застосування ефекту. Від 0.0 (немає ефекту) до 1.0 (повний ефект).
    /// </summary>
    public string? Effect { get; set; }         // Наприклад: "Overdrive", "Robot"
    public float? EffectIntensity { get; set; } // Від 0.0 до 1.0
}