using System.Diagnostics;

namespace WhisperTiny_STT.Services;

/// <summary>
/// Responsible for verifying the existence of required machine learning models
/// and automatically downloading them from Hugging Face if they are missing.
/// Supports absolute exact paths, custom model directories, and automatic self-healing.
/// Updated for Whisper-based Sherpa ONNX models requiring Encoder, Decoder, and Tokens.
/// </summary>
public static class ModelManager
{
    /// <summary>
    /// Validates the presence of Whisper ONNX and VAD models based on configuration priorities.
    /// Downloads them automatically if configured to do so.
    /// </summary>
    /// <param name="config">The application configuration.</param>
    /// <returns>A tuple containing the absolute paths to the verified Encoder, Decoder, Tokens, and VAD model.</returns>
    /// <exception cref="FileNotFoundException">Thrown if a required model is missing and AutoDownload is disabled.</exception>
    public static async Task<(string EncoderPath, string DecoderPath, string TokensPath, string VadPath)> EnsureModelsExistAsync(IConfiguration config)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string modelsDir = config["SttSettings:ModelDirectory"] ?? "models";

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

        // Read configuration values for Exact Paths
        string? exactEncoder = config["SttSettings:ExactEncoderFilePath"];
        string? exactDecoder = config["SttSettings:ExactDecoderFilePath"];
        string? exactTokens = config["SttSettings:ExactTokensFilePath"];
        string? exactVad = config["SttSettings:ExactVadFilePath"];

        // Read configuration values for File Names
        string encoderName = config["SttSettings:WhisperEncoderName"] ?? "tiny-encoder.fp16.onnx";
        string decoderName = config["SttSettings:WhisperDecoderName"] ?? "tiny-decoder.fp16.onnx";
        string tokensName = config["SttSettings:WhisperTokensName"] ?? "tiny-tokens.txt";
        string vadName = config["SttSettings:VadModelName"] ?? "silero_vad.onnx";

        // Read Repositories (Auto-correcting 'tree' to 'resolve' to ensure raw file downloads)
        string whisperRepoUrl = config["AutoDownload:WhisperRepositoryUrl"]!.Replace("/tree/main", "/resolve/main");
        string vadRepoUrl = config["AutoDownload:VadRepositoryUrl"]!.Replace("/tree/main", "/resolve/main");

        // ==========================================
        // WHISPER ENCODER RESOLUTION
        // ==========================================
        string finalEncoderPath = await ResolveAndDownloadAsync(exactEncoder, modelsDir, encoderName, whisperRepoUrl, "Whisper Encoder", config);

        // ==========================================
        // WHISPER DECODER RESOLUTION
        // ==========================================
        string finalDecoderPath = await ResolveAndDownloadAsync(exactDecoder, modelsDir, decoderName, whisperRepoUrl, "Whisper Decoder", config);

        // ==========================================
        // WHISPER TOKENS RESOLUTION
        // ==========================================
        string finalTokensPath = await ResolveAndDownloadAsync(exactTokens, modelsDir, tokensName, whisperRepoUrl, "Whisper Tokens", config);

        // ==========================================
        // SILERO VAD MODEL RESOLUTION
        // ==========================================
        string finalVadPath = await ResolveAndDownloadAsync(exactVad, modelsDir, vadName, vadRepoUrl, "Silero VAD", config);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("=========================================");
        Console.WriteLine("        STT MODELS READY                 ");
        Console.WriteLine("=========================================");
        Console.ResetColor();

        return (finalEncoderPath, finalDecoderPath, finalTokensPath, finalVadPath);
    }

    private static async Task<string> ResolveAndDownloadAsync(string? exactPath, string modelsDir, string fileName, string repoUrl, string logName, IConfiguration config)
    {
        string finalPath;

        // Priority 1: Exact Path (If specified and exists)
        if (!string.IsNullOrWhiteSpace(exactPath) && File.Exists(exactPath))
        {
            finalPath = Path.GetFullPath(exactPath);
            Console.WriteLine($"[SYSTEM] Using EXACT {logName} path: {finalPath}");
        }
        else
        {
            // Priority 2 & 3: Directory Scan & Auto-Download
            finalPath = Path.Combine(modelsDir, fileName);
            if (!File.Exists(finalPath))
            {
                await HandleMissingFileAsync(config, finalPath, logName, repoUrl);
            }
            else
            {
                Console.WriteLine($"[SYSTEM] Found {logName} at: {finalPath}");
            }
        }

        return finalPath;
    }

    /// <summary>
    /// Handles the scenario where a required model file is not found at the expected location.
    /// Initiates a download if AutoDownload is enabled, otherwise throws an exception.
    /// </summary>
    private static async Task HandleMissingFileAsync(IConfiguration config, string destinationPath, string modelName, string repoUrl)
    {
        bool autoDownload = bool.Parse(config["AutoDownload:Enable"] ?? "true");

        if (autoDownload)
        {
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
    /// Downloads a file over HTTP and displays a dynamic progress bar in the console.
    /// </summary>
    private static async Task DownloadFileAsync(string url, string destinationPath, string modelName)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[INFO] {modelName} is missing locally. Downloading from Hugging Face...");
        Console.ResetColor();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new HttpClient();
            // We use ResponseHeadersRead to start processing the stream as soon as headers are received, 
            // allowing us to show progress.
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            // Try to get total file size for percentage calculation
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var totalRead = 0L;
            var buffer = new byte[8192];
            var isMoreToRead = true;

            do
            {
                var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    isMoreToRead = false;
                }
                else
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    totalRead += read;

                    // Update the console progress bar only if we know the total size
                    if (totalBytes != -1)
                    {
                        DrawProgressBar(modelName, totalRead, totalBytes);
                    }
                }
            }
            while (isMoreToRead);

            stopwatch.Stop();
            Console.WriteLine(); // Add a new line after the progress bar is done

            var fileInfo = new FileInfo(destinationPath);
            double sizeMb = fileInfo.Length / (1024.0 * 1024.0);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[SUCCESS] Downloaded {modelName} ({sizeMb:F1} MB) in {stopwatch.Elapsed.TotalSeconds:F1} seconds.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            throw new Exception($"Failed to download {modelName} from {url}. Error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Draws an inline progress bar in the console using \r to overwrite the current line.
    /// </summary>
    private static void DrawProgressBar(string modelName, long current, long total)
    {
        int progressLength = 30; // Length of the progress bar UI
        double percentage = (double)current / total;
        int filled = (int)(progressLength * percentage);

        string bar = new string('#', filled).PadRight(progressLength, '-');
        double currentMb = current / (1024.0 * 1024.0);
        double totalMb = total / (1024.0 * 1024.0);

        // \r moves the cursor to the beginning of the line, allowing us to overwrite it
        Console.Write($"\r   -> [{bar}] {percentage:P0} ({currentMb:F1}/{totalMb:F1} MB) ");
    }
}