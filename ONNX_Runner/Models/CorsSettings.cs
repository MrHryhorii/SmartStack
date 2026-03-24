namespace ONNX_Runner.Models;

public class CorsSettings
{
    // Якщо true - дозволяємо запити звідусіль (включаючи локальні HTML файли).
    // Якщо false - сервер прийматиме запити ТІЛЬКИ з доменів зі списку AllowedOrigins.
    public bool AllowAnyOrigin { get; set; } = true;

    // Список дозволених доменів (працює тільки якщо AllowAnyOrigin = false)
    public string[] AllowedOrigins { get; set; } = [];
}