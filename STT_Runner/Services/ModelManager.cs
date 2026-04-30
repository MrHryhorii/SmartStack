using System.Diagnostics;

namespace STT_Runner.Services;

/// <summary>
/// Responsible for verifying the existence of required machine learning models
/// and automatically downloading them from Hugging Face if they are missing.
/// Supports absolute exact paths, custom model directories, and automatic self-healing.
/// </summary>
public static class ModelManager
{
    /// <summary>
    /// Validates the presence of Whisper and VAD models based on configuration priorities.
    /// Downloads them automatically if configured to do so.
    /// </summary>
    /// <param name="config">The application configuration.</param>
    /// <returns>A tuple containing the absolute paths to the verified Whisper and VAD models.</returns>
    /// <exception cref="FileNotFoundException">Thrown if a required model is missing and AutoDownload is disabled.</exception>
    public static async Task<(string WhisperPath, string VadPath)> EnsureModelsExistAsync(IConfiguration config)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string modelsDir = config["SttSettings:ModelDirectory"] ?? "Models";

        // Resolve Base Model Directory
        // Ensure the path is absolute to prevent issues when the app is launched from different working directories.
        if (!Path.IsPathRooted(modelsDir))
        {
            modelsDir = Path.GetFullPath(Path.Combine(baseDir, modelsDir));
        }

        // Create the default directory if it doesn't exist. This prevents CrashLoopBackOff 
        // in Docker containers and ensures a smooth first-run experience.
        if (!Directory.Exists(modelsDir))
        {
            Directory.CreateDirectory(modelsDir);
            Console.WriteLine($"[SYSTEM] Created model directory at: {modelsDir}");
        }

        // Read configuration values
        string? exactWhisper = config["SttSettings:ExactWhisperFilePath"];
        string? exactVad = config["SttSettings:ExactVadFilePath"];
        string whisperName = config["SttSettings:WhisperModelName"] ?? "ggml-base.bin";
        string vadName = config["SttSettings:VadModelName"] ?? "silero_vad.onnx";

        // ==========================================
        // WHISPER MODEL RESOLUTION
        // ==========================================
        string finalWhisperPath;

        // Priority 1: Exact Path (If specified and exists)
        if (!string.IsNullOrWhiteSpace(exactWhisper) && File.Exists(exactWhisper))
        {
            finalWhisperPath = Path.GetFullPath(exactWhisper);
            Console.WriteLine($"[SYSTEM] Using EXACT Whisper path: {finalWhisperPath}");
        }
        else
        {
            // Priority 2 & 3: Directory Scan & Auto-Download
            finalWhisperPath = Path.Combine(modelsDir, whisperName);
            if (!File.Exists(finalWhisperPath))
            {
                await HandleMissingFileAsync(config, finalWhisperPath, "Whisper GGML");
            }
            else
            {
                Console.WriteLine($"[SYSTEM] Found Whisper model at: {finalWhisperPath}");
            }
        }

        // ==========================================
        // SILERO VAD MODEL RESOLUTION
        // ==========================================
        string finalVadPath;

        // Priority 1: Exact Path (If specified and exists)
        if (!string.IsNullOrWhiteSpace(exactVad) && File.Exists(exactVad))
        {
            finalVadPath = Path.GetFullPath(exactVad);
            Console.WriteLine($"[SYSTEM] Using EXACT VAD path: {finalVadPath}");
        }
        else
        {
            // Priority 2 & 3: Directory Scan & Auto-Download
            finalVadPath = Path.Combine(modelsDir, vadName);
            if (!File.Exists(finalVadPath))
            {
                await HandleMissingFileAsync(config, finalVadPath, "Silero VAD");
            }
            else
            {
                Console.WriteLine($"[SYSTEM] Found VAD model at: {finalVadPath}");
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("=========================================");
        Console.WriteLine("        STT MODELS READY                 ");
        Console.WriteLine("=========================================");
        Console.ResetColor();

        return (finalWhisperPath, finalVadPath);
    }

    /// <summary>
    /// Handles the scenario where a required model file is not found at the expected location.
    /// Initiates a download if AutoDownload is enabled, otherwise throws an exception.
    /// </summary>
    private static async Task HandleMissingFileAsync(IConfiguration config, string destinationPath, string modelName)
    {
        bool autoDownload = bool.Parse(config["AutoDownload:Enable"] ?? "true");

        if (autoDownload)
        {
            string repoUrl = config["AutoDownload:RepositoryUrl"]!;
            string fileName = Path.GetFileName(destinationPath);
            // Append the filename to the base repository URL
            string downloadUrl = repoUrl.EndsWith('/') ? $"{repoUrl}{fileName}" : $"{repoUrl}/{fileName}";

            await DownloadFileAsync(downloadUrl, destinationPath, modelName);
        }
        else
        {
            throw new FileNotFoundException($"[FATAL] {modelName} not found at {destinationPath} and AutoDownload is disabled.");
        }
    }

    /// <summary>
    /// Downloads a file over HTTP with a progress timer and graceful error handling.
    /// Uses ResponseHeadersRead to stream data directly to disk without loading the entire file into RAM.
    /// </summary>
    private static async Task DownloadFileAsync(string url, string destinationPath, string modelName)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[INFO] {modelName} is missing locally. Downloading from Hugging Face...");
        Console.WriteLine($"       -> URL: {url}");
        Console.ResetColor();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new HttpClient();
            // Using ResponseHeadersRead allows us to start processing the response 
            // as soon as headers are received, enabling streaming large files directly to disk
            //  without consuming large amounts of memory.
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            // Write stream directly to disk
            using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fs);

            stopwatch.Stop();

            var fileInfo = new FileInfo(destinationPath);
            double sizeMb = fileInfo.Length / (1024.0 * 1024.0);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[SUCCESS] Downloaded {modelName} ({sizeMb:F1} MB) in {stopwatch.Elapsed.TotalSeconds:F1} seconds.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            // If download fails (e.g., no internet), clean up the potentially corrupted partial file
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            throw new Exception($"Failed to download {modelName} from {url}. Error: {ex.Message}", ex);
        }
    }
}