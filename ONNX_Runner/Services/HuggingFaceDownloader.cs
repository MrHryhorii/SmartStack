using System.Diagnostics;

namespace ONNX_Runner.Services;

/// <summary>
/// A universal utility for automatically downloading AI models from Hugging Face.
/// Used for fetching both OpenVoice (Tone Extractor/Color) and Piper TTS models.
/// </summary>
public static class HuggingFaceDownloader
{
    // Reusing a single HttpClient instance is a C# best practice to prevent socket exhaustion
    private static readonly HttpClient _httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        // A custom User-Agent identifies your project to the Hugging Face servers
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ONNX-CSharp-Server/1.0");
        return client;
    }

    /// <summary>
    /// Downloads a file asynchronously and displays a live progress bar in the console.
    /// </summary>
    /// <param name="fileUrl">The direct download URL.</param>
    /// <param name="destinationPath">The local path where the file will be saved.</param>
    /// <param name="fileName">The short name of the file (for console logging).</param>
    /// <param name="modelDescription">A brief description (e.g., "Voice Cloner" or "Piper TTS").</param>
    public static async Task DownloadFileAsync(string fileUrl, string destinationPath, string fileName, string modelDescription)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[DOWNLOAD] Fetching {modelDescription} model ({fileName})...");
        Console.ResetColor();

        try
        {
            // CRITICAL ARCHITECTURE NOTE: HttpCompletionOption.ResponseHeadersRead
            // Ensures we start streaming the file chunks directly to the disk, rather than 
            // downloading the entire 100MB+ model into RAM first (which causes Out-Of-Memory crashes).
            using var response = await _httpClient.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var canReportProgress = totalBytes != -1;

            using var contentStream = await response.Content.ReadAsStreamAsync();

            // 8192 (8KB) is the standard buffer size for optimal disk I/O operations
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var totalRead = 0L;
            var buffer = new byte[8192];
            var isMoreToRead = true;
            var stopwatch = Stopwatch.StartNew();

            do
            {
                var read = await contentStream.ReadAsync(buffer);
                if (read == 0)
                {
                    isMoreToRead = false;
                }
                else
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    totalRead += read;

                    // Update the console progress bar every 100ms to avoid console flickering and CPU bottlenecks
                    if (canReportProgress && stopwatch.ElapsedMilliseconds > 100)
                    {
                        DrawProgressBar(totalRead, totalBytes);
                        stopwatch.Restart();
                    }
                }
            }
            while (isMoreToRead);

            // Ensure the progress bar visually reaches 100% when finished
            if (canReportProgress) DrawProgressBar(totalBytes, totalBytes);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[SUCCESS] {fileName} downloaded successfully.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ERROR] Failed to download {fileName}: {ex.Message}");
            Console.ResetColor();

            // Clean up the corrupted/incomplete file to prevent the server from trying to load broken bytes later
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
        }
    }

    private static void DrawProgressBar(long current, long total)
    {
        const int barSize = 50;
        double progress = (double)current / total;
        int filled = (int)(progress * barSize);
        int empty = barSize - filled;

        Console.Write($"\r   Progress: [");
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.Write(new string('#', filled));
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(new string('-', empty));
        Console.ResetColor();

        // Format to show downloaded megabytes
        Console.Write($"] {progress:P1} ({current / 1024 / 1024} MB / {total / 1024 / 1024} MB)");
    }
}