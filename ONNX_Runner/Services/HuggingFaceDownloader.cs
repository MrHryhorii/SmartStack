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
    /// The completed file is moved into place only after the download succeeds.
    /// </summary>
    public static async Task DownloadFileAsync(string fileUrl, string destinationPath, string fileName, string modelDescription)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[DOWNLOAD] Fetching {modelDescription} model ({fileName})...");
        Console.ResetColor();

        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        string partialPath = destinationPath + ".partial";

        try
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            // CRITICAL ARCHITECTURE NOTE: HttpCompletionOption.ResponseHeadersRead
            // Ensures we start streaming the file chunks directly to the disk, rather than
            // downloading the entire model into RAM first.
            using var response = await _httpClient.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1L;
            bool canReportProgress = totalBytes > 0;

            long totalRead = 0L;
            var buffer = new byte[8192];
            var stopwatch = Stopwatch.StartNew();

            await using (var contentStream = await response.Content.ReadAsStreamAsync())
            await using (var fileStream = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                8192,
                useAsync: true))
            {
                while (true)
                {
                    int read = await contentStream.ReadAsync(buffer);
                    if (read == 0)
                    {
                        break;
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    totalRead += read;

                    if (canReportProgress && stopwatch.ElapsedMilliseconds > 100)
                    {
                        DrawProgressBar(totalRead, totalBytes);
                        stopwatch.Restart();
                    }
                }

                await fileStream.FlushAsync();
            }

            if (canReportProgress)
            {
                DrawProgressBar(totalBytes, totalBytes);
            }

            File.Move(partialPath, destinationPath, overwrite: true);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[SUCCESS] {fileName} downloaded successfully.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ERROR] Failed to download {fileName}: {ex.Message}");
            Console.ResetColor();

            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    private static void DrawProgressBar(long current, long total)
    {
        const int barSize = 50;
        double progress = Math.Clamp((double)current / total, 0.0, 1.0);
        int filled = (int)(progress * barSize);
        int empty = barSize - filled;

        Console.Write("\r   Progress: [");
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.Write(new string('#', filled));
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(new string('-', empty));
        Console.ResetColor();
        Console.Write($"] {progress:P1} ({current / 1024 / 1024} MB / {total / 1024 / 1024} MB)");
    }
}
