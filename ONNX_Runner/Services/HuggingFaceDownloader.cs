using System.Diagnostics;

namespace ONNX_Runner.Services;

/// <summary>
/// Універсальна утиліта для автоматичного завантаження моделей з Hugging Face.
/// Підходить як для OpenVoice, так і для Piper TTS.
/// </summary>
public static class HuggingFaceDownloader
{
    private static readonly HttpClient _httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        // Універсальний User-Agent для всього вашого проекту
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ONNX-CSharp-Server/1.0");
        return client;
    }

    // Параметр modelDescription (наприклад, "Voice Cloner" або "Piper TTS")
    public static async Task DownloadFileAsync(string fileUrl, string destinationPath, string fileName, string modelDescription)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[DOWNLOAD] Fetching {modelDescription} model ({fileName})...");
        Console.ResetColor();

        try
        {
            using var response = await _httpClient.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var canReportProgress = totalBytes != -1;

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var totalRead = 0L;
            var buffer = new byte[8192];
            var isMoreToRead = true;
            var stopwatch = Stopwatch.StartNew();

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

                    if (canReportProgress && stopwatch.ElapsedMilliseconds > 100)
                    {
                        DrawProgressBar(totalRead, totalBytes);
                        stopwatch.Restart();
                    }
                }
            }
            while (isMoreToRead);

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
        Console.Write($"] {progress:P1} ({current / 1024 / 1024} MB / {total / 1024 / 1024} MB)");
    }
}