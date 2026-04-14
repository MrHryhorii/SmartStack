using System.Text.Json;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

public static class ModelLoader
{
    public static (string OnnxFilePath, PiperConfig Config) LoadFromDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Directory '{directoryPath}' not found.");
        }

        // Шукаємо файли
        var onnxFiles = Directory.GetFiles(directoryPath, "*.onnx");
        var jsonFiles = Directory.GetFiles(directoryPath, "*.json");

        if (onnxFiles.Length == 0)
        {
            throw new FileNotFoundException($"No .onnx model found in '{directoryPath}'.");
        }

        if (jsonFiles.Length == 0)
        {
            throw new FileNotFoundException($"No .json config found in '{directoryPath}'.");
        }

        // Беремо перший знайдений ONNX файл як базовий
        string onnxPath = onnxFiles[0];

        // Витягуємо назви для пошуку конфігу
        string fileNameWithExt = Path.GetFileName(onnxPath); // наприклад: "voice.onnx"
        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(onnxPath); // наприклад: "voice"

        // Перевіряємо офіційний стандарт Piper (voice.onnx -> voice.onnx.json)
        string expectedJson1 = Path.Combine(directoryPath, fileNameWithExt + ".json");

        // Перевіряємо користувацький стандарт (voice.onnx -> voice.json)
        string expectedJson2 = Path.Combine(directoryPath, fileNameWithoutExt + ".json");

        string jsonPath;

        // РОЗУМНИЙ ПОШУК КОНФІГУ
        if (File.Exists(expectedJson1))
        {
            jsonPath = expectedJson1;
        }
        else if (File.Exists(expectedJson2))
        {
            jsonPath = expectedJson2;
        }
        else
        {
            // Fallback (Остання надія): беремо перший знайдений JSON у папці
            jsonPath = jsonFiles[0];
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[WARNING] Exact config match not found for '{fileNameWithExt}'. Falling back to '{Path.GetFileName(jsonPath)}'.");
            Console.ResetColor();
        }

        // Читаємо і десеріалізуємо JSON конфіг
        string jsonContent = File.ReadAllText(jsonPath);
        var config = JsonSerializer.Deserialize<PiperConfig>(jsonContent)
                    ?? throw new InvalidOperationException($"Failed to parse {Path.GetFileName(jsonPath)}");

        return (onnxPath, config);
    }
}