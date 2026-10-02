using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Loads or downloads the reserved Piper fallback model after normal model loading fails.
/// </summary>
public static class PiperModelBootstrapper
{
    private const string FallbackDirectoryName = "Fallback";
    private const string FallbackModelFileName = "fallback.onnx";
    private const string FallbackConfigFileName = "fallback.onnx.json";

    public static async Task<(string? OnnxFilePath, PiperConfig? Config)> TryLoadFallbackAsync(ModelSettings settings)
    {
        string fallbackDirectory = Path.Combine(ModelLoader.GetModelDirectoryPath(settings), FallbackDirectoryName);
        string modelPath = Path.Combine(fallbackDirectory, FallbackModelFileName);
        string configPath = Path.Combine(fallbackDirectory, FallbackConfigFileName);

        if (File.Exists(modelPath) && File.Exists(configPath))
        {
            return TryLoad(modelPath, configPath);
        }

        if (string.IsNullOrWhiteSpace(settings.FallbackModelUrl) ||
            string.IsNullOrWhiteSpace(settings.FallbackConfigUrl))
        {
            return (null, null);
        }

        if (!ShouldDownload(settings))
        {
            return (null, null);
        }

        Directory.CreateDirectory(fallbackDirectory);

        if (!File.Exists(modelPath))
        {
            await HuggingFaceDownloader.DownloadFileAsync(
                settings.FallbackModelUrl,
                modelPath,
                FallbackModelFileName,
                "Piper fallback");
        }

        if (!File.Exists(configPath))
        {
            await HuggingFaceDownloader.DownloadFileAsync(
                settings.FallbackConfigUrl,
                configPath,
                FallbackConfigFileName,
                "Piper fallback");
        }

        if (!File.Exists(modelPath) || !File.Exists(configPath))
        {
            return (null, null);
        }

        return TryLoad(modelPath, configPath);
    }

    private static bool ShouldDownload(ModelSettings settings)
    {
        if (settings.AutoDownloadFallbackModel)
        {
            return true;
        }

        if (Console.IsInputRedirected)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                "[WARNING] No Piper model was found. Set ModelSettings:AutoDownloadFallbackModel=true " +
                "to allow fallback download in a non-interactive environment.");
            Console.ResetColor();
            return false;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[WARNING] No usable Piper model was found.");
        Console.ResetColor();
        Console.Write("Download the fallback English voice model? [Y/N]: ");

        string? answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static (string? OnnxFilePath, PiperConfig? Config) TryLoad(string modelPath, string configPath)
    {
        try
        {
            var fallbackSettings = new ModelSettings
            {
                ExactModelFilePath = modelPath,
                ExactConfigFilePath = configPath
            };

            var (onnxPath, config) = ModelLoader.LoadFromDirectory(fallbackSettings);
            return (onnxPath, config);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] Failed to load fallback Piper model: {ex.Message}");
            Console.ResetColor();
            return (null, null);
        }
    }
}
