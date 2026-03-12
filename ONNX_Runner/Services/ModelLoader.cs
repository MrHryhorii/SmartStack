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

        // Беремо перші знайдені файли (як ми і домовлялися, що модель одна)
        string onnxPath = onnxFiles[0];
        string jsonPath = jsonFiles[0];

        // Читаємо і десеріалізуємо JSON конфіг
        string jsonContent = File.ReadAllText(jsonPath);
        var config = JsonSerializer.Deserialize<PiperConfig>(jsonContent)
                     ?? throw new InvalidOperationException("Failed to parse config.json");

        return (onnxPath, config);
    }
}