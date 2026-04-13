namespace ONNX_Runner.Models;

public class ClonerSettings
{
    // Глобальний рубильник: якщо false, сервер ігнорує будь-які запити на клонування 
    // і миттєво віддає базовий голос Piper, заощаджуючи ресурси.
    public bool EnableCloning { get; set; } = true;

    // 1.0 = Точна копія (стандарт)
    // 0.5 = 50% від бази, 50% від цілі
    // 1.5 = Перебільшення особливостей цільового голосу
    public float CloneIntensity { get; set; } = 1.0f;

    // Параметр Tau для регулювання різноманітності тону (1.0 = стандарт, <1.0 = більш консервативно, >1.0 = більш різноманітно)
    public float ToneTemperature { get; set; } = 1.0f;
}