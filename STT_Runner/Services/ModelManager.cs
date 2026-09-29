using System.Diagnostics;
using System.Security.Cryptography;

namespace STT_Runner.Services;

/// <summary>
/// Resolves the configured Whisper and VAD files before the server starts.
/// Missing files are downloaded only when AutoDownload is enabled.
/// </summary>
public static class ModelManager
{
    /// <summary>
    /// Uses explicit file paths first, then names within the configured model directory.
    /// </summary>
    /// <param name="config">The application configuration.</param>
    /// <returns>A tuple containing the absolute paths to the verified Whisper and VAD models.</returns>
    /// <exception cref="FileNotFoundException">Thrown if a required model is missing and AutoDownload is disabled.</exception>
    public static async Task<(string WhisperPath, string VadPath)> EnsureModelsExistAsync(IConfiguration config)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string modelsDir = config["SttSettings:ModelDirectory"] ?? "Models";

        // Anchor a relative model directory to the application, not the shell's cwd.
        if (!Path.IsPathRooted(modelsDir))
        {
            modelsDir = Path.GetFullPath(Path.Combine(baseDir, modelsDir));
        }

        // Create the directory before a possible first-run download.
        if (!Directory.Exists(modelsDir))
        {
            Directory.CreateDirectory(modelsDir);
            Console.WriteLine($"[SYSTEM] Created model directory at: {modelsDir}");
        }

        string? exactWhisper = config["SttSettings:ExactWhisperFilePath"];
        string? exactVad = config["SttSettings:ExactVadFilePath"];
        string whisperName = config["SttSettings:WhisperModelName"] ?? "ggml-base.bin";
        string vadName = config["SttSettings:VadModelName"] ?? "silero_vad.onnx";
        string whisperHash = GetExpectedHash(config, "AutoDownload:WhisperSha256");
        string vadHash = GetExpectedHash(config, "AutoDownload:VadSha256");

        // An invalid explicit path is a configuration error, not a download hint.
        string finalWhisperPath;

        if (!string.IsNullOrWhiteSpace(exactWhisper) && !File.Exists(exactWhisper))
            throw new FileNotFoundException($"Configured Whisper model does not exist: {exactWhisper}");

        if (!string.IsNullOrWhiteSpace(exactWhisper) && File.Exists(exactWhisper))
        {
            finalWhisperPath = Path.GetFullPath(exactWhisper);
            Console.WriteLine($"[SYSTEM] Using EXACT Whisper path: {finalWhisperPath}");
        }
        else
        {
            // Otherwise use the configured filename and download only if absent.
            finalWhisperPath = Path.Combine(modelsDir, whisperName);
            if (!File.Exists(finalWhisperPath))
            {
                await HandleMissingFileAsync(config, finalWhisperPath, "Whisper GGML", whisperHash);
            }
            else
            {
                Console.WriteLine($"[SYSTEM] Found Whisper model at: {finalWhisperPath}");
            }
            await VerifyFileHashAsync(finalWhisperPath, whisperHash);
        }

        // Apply the same precedence to the VAD model.
        string finalVadPath;

        if (!string.IsNullOrWhiteSpace(exactVad) && !File.Exists(exactVad))
            throw new FileNotFoundException($"Configured VAD model does not exist: {exactVad}");

        if (!string.IsNullOrWhiteSpace(exactVad) && File.Exists(exactVad))
        {
            finalVadPath = Path.GetFullPath(exactVad);
            Console.WriteLine($"[SYSTEM] Using EXACT VAD path: {finalVadPath}");
        }
        else
        {
            finalVadPath = Path.Combine(modelsDir, vadName);
            if (!File.Exists(finalVadPath))
            {
                await HandleMissingFileAsync(config, finalVadPath, "Silero VAD", vadHash);
            }
            else
            {
                Console.WriteLine($"[SYSTEM] Found VAD model at: {finalVadPath}");
            }
            await VerifyFileHashAsync(finalVadPath, vadHash);
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("=========================================");
        Console.WriteLine("        STT MODELS READY                 ");
        Console.WriteLine("=========================================");
        Console.ResetColor();

        return (finalWhisperPath, finalVadPath);
    }

    /// <summary>
    /// Downloads a missing model or fails startup when automatic downloads are disabled.
    /// </summary>
    private static async Task HandleMissingFileAsync(IConfiguration config, string destinationPath, string modelName, string expectedHash)
    {
        bool autoDownload = bool.Parse(config["AutoDownload:Enable"] ?? "true");

        if (autoDownload)
        {
            string repoUrl = config["AutoDownload:RepositoryUrl"]!;
            string fileName = Path.GetFileName(destinationPath);
            // The repository URL is a base path shared by both model filenames.
            string downloadUrl = repoUrl.EndsWith('/') ? $"{repoUrl}{fileName}" : $"{repoUrl}/{fileName}";

            await DownloadFileAsync(downloadUrl, destinationPath, modelName, expectedHash);
        }
        else
        {
            throw new FileNotFoundException($"[FATAL] {modelName} not found at {destinationPath} and AutoDownload is disabled.");
        }
    }

    /// <summary>
    /// Streams a model into a temporary file and replaces the target only on success.
    /// </summary>
    private static async Task DownloadFileAsync(string url, string destinationPath, string modelName, string expectedHash)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[INFO] {modelName} is missing locally. Downloading from Hugging Face...");
        Console.ResetColor();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new HttpClient();
            // Do not buffer the entire model in HttpClient before copying it to disk.
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            // A missing Content-Length disables percentage progress, not the download.
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            string temporaryPath = destinationPath + ".download";
            using var fileStream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);

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

                    // Update the console progress bar
                    if (totalBytes != -1)
                    {
                        DrawProgressBar(modelName, totalRead, totalBytes);
                    }
                }
            }
            while (isMoreToRead);

            // Move only a complete download into the path used by model loading.
            await fileStream.FlushAsync();
            fileStream.Close();
            if (totalBytes >= 0 && totalRead != totalBytes)
                throw new InvalidDataException($"Incomplete {modelName} download: expected {totalBytes} bytes, received {totalRead}.");

            await VerifyFileHashAsync(temporaryPath, expectedHash);
            File.Move(temporaryPath, destinationPath, overwrite: true);
            stopwatch.Stop();
            Console.WriteLine(); // Finish the in-place progress line.

            var fileInfo = new FileInfo(destinationPath);
            double sizeMb = fileInfo.Length / (1024.0 * 1024.0);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[SUCCESS] Downloaded {modelName} ({sizeMb:F1} MB) in {stopwatch.Elapsed.TotalSeconds:F1} seconds.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            if (File.Exists(destinationPath + ".download")) File.Delete(destinationPath + ".download");
            throw new Exception($"Failed to download {modelName} from {url}. Error: {ex.Message}", ex);
        }
    }

    /// <summary>Rejects invalid or missing checksums before loading downloaded model weights.</summary>
    private static string GetExpectedHash(IConfiguration config, string key)
    {
        string hash = config[key]?.Trim() ?? "";
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidOperationException($"{key} must be a 64-character SHA-256 value.");
        return hash;
    }

    /// <summary>Checks local and newly downloaded managed models against the pinned digest.</summary>
    private static async Task VerifyFileHashAsync(string path, string expectedHash)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 mismatch for {path}. Expected {expectedHash}, got {actualHash}.");
    }

    /// <summary>
    /// Updates one console line while the response body is being copied.
    /// </summary>
    private static void DrawProgressBar(string modelName, long current, long total)
    {
        int progressLength = 30;
        double percentage = (double)current / total;
        int filled = (int)(progressLength * percentage);

        string bar = new string('#', filled).PadRight(progressLength, '-');
        double currentMb = current / (1024.0 * 1024.0);
        double totalMb = total / (1024.0 * 1024.0);

        // A carriage return keeps repeated updates on the same console line.
        Console.Write($"\r   -> [{bar}] {percentage:P0} ({currentMb:F1}/{totalMb:F1} MB) ");
    }
}
