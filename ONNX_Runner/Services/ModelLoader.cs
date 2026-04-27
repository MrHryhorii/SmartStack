using System.Text.Json;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Utility class responsible for safely loading the TTS model and its configuration from a directory.
/// It features an intelligent "Smart Discovery" mechanism to find the matching JSON config file, 
/// even if naming conventions vary between different Piper model distributions.
/// It also supports loading exact file paths directly from configuration overrides.
/// </summary>
public static class ModelLoader
{
    /// <summary>
    /// Scans the directory, identifies the .onnx model, and finds the most appropriate .json configuration,
    /// or loads exact files if specified in the settings.
    /// </summary>
    /// <param name="settings">Model settings containing directory or exact file paths.</param>
    /// <returns>A tuple containing the ONNX model file path and the deserialized PiperConfig object.</returns>
    public static (string OnnxFilePath, PiperConfig Config) LoadFromDirectory(ModelSettings settings)
    {
        string onnxPath;
        string jsonPath;

        // --- EXACT FILE PATH LOGIC ---
        // If the user explicitly specified both the model and config file paths via appsettings or command line
        if (!string.IsNullOrWhiteSpace(settings.ExactModelFilePath) &&
            !string.IsNullOrWhiteSpace(settings.ExactConfigFilePath))
        {
            // Validate and use the exact file paths provided by the user
            string absoluteModelPath = Path.GetFullPath(settings.ExactModelFilePath);
            string absoluteConfigPath = Path.GetFullPath(settings.ExactConfigFilePath);

            if (!File.Exists(absoluteModelPath))
                throw new FileNotFoundException($"Exact model file not found at: {absoluteModelPath}");

            if (!File.Exists(absoluteConfigPath))
                throw new FileNotFoundException($"Exact config file not found at: {absoluteConfigPath}");

            onnxPath = absoluteModelPath;
            jsonPath = absoluteConfigPath;

            // Log the usage of exact paths for transparency
            Console.WriteLine($"[SYSTEM] Using exact model path: {onnxPath}");
        }
        else
        {
            // --- DIRECTORY SCANNING LOGIC (STANDARD BEHAVIOR) ---
            string directoryPath = settings.ModelDirectory;

            // Bulletproof relative paths: ensures the folder is searched relative to the .exe file,
            // not the terminal's current working directory (prevents bugs with shortcuts).
            if (!Path.IsPathRooted(directoryPath))
            {
                directoryPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, directoryPath));
            }

            // Fail fast if the primary model directory is missing
            if (!Directory.Exists(directoryPath))
            {
                throw new DirectoryNotFoundException($"Directory '{directoryPath}' not found.");
            }

            // Scan for all available model and config files
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

            // We use the first found ONNX file as the primary model
            onnxPath = onnxFiles[0];

            // Extract naming patterns to intelligently match the config file
            string fileNameWithExt = Path.GetFileName(onnxPath); // e.g., "voice.onnx"
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(onnxPath); // e.g., "voice"

            // 1. Check for the official Piper standard naming (voice.onnx -> voice.onnx.json)
            string expectedJson1 = Path.Combine(directoryPath, fileNameWithExt + ".json");

            // 2. Check for the simplified user-friendly naming (voice.onnx -> voice.json)
            string expectedJson2 = Path.Combine(directoryPath, fileNameWithoutExt + ".json");

            // --- SMART CONFIG DISCOVERY LOGIC ---
            if (File.Exists(expectedJson1))
            {
                // Priority 1: Exact match including .onnx extension
                jsonPath = expectedJson1;
            }
            else if (File.Exists(expectedJson2))
            {
                // Priority 2: Match based on the filename only
                jsonPath = expectedJson2;
            }
            else
            {
                // FALLBACK: If no naming convention matches, we take the first JSON file in the folder 
                // as a "last resort". This allows the server to start even with completely custom file names.
                jsonPath = jsonFiles[0];
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[WARNING] Exact config match not found for '{fileNameWithExt}'. Falling back to '{Path.GetFileName(jsonPath)}'.");
                Console.ResetColor();
            }
        }

        // Read and deserialize the JSON content into the PiperConfig model
        string jsonContent = File.ReadAllText(jsonPath);
        var config = JsonSerializer.Deserialize<PiperConfig>(jsonContent)
                    ?? throw new InvalidOperationException($"Failed to parse {Path.GetFileName(jsonPath)}");

        // Return the validated model path and the successfully parsed config
        return (onnxPath, config);
    }
}