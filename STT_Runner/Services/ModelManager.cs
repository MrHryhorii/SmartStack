using System.Diagnostics;
using System.Security.Cryptography;

namespace STT_Runner.Services;

/// <summary>Resolves local weights and downloads missing models before startup.</summary>
public static class ModelManager
{
    // Pin both the revision and digest of the bundled defaults, independently of local filenames.
    private const string DefaultWhisperUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small.bin";
    private const string DefaultVadUrl = "https://huggingface.co/Hinotsuba/silero_vad_ggml-base/resolve/ee6290b4dde18d884258a108a809daffb6ca11cb/silero_vad.onnx";
    private const string DefaultWhisperHash = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b";
    private const string DefaultVadHash = "a4a068cd6cf1ea8355b84327595838ca748ec29a25bc91fc82e6c299ccdc5808";

    /// <summary>Prefers explicit paths, otherwise uses the configured model directory and URLs.</summary>
    public static async Task<(string WhisperPath, string VadPath)> EnsureModelsExistAsync(IConfiguration config)
    {
        string modelsDir = ApplicationPath(ValueOrDefault(config["SttSettings:ModelDirectory"], "Models"));
        string whisper = await ResolveAsync(config, modelsDir, "Whisper", "Whisper GGML",
            "ggml-small.bin", DefaultWhisperUrl, DefaultWhisperHash);
        string vad = await ResolveAsync(config, modelsDir, "Vad", "Silero VAD",
            "silero_vad.onnx", DefaultVadUrl, DefaultVadHash);
        Console.WriteLine("[SYSTEM] STT models ready.");
        return (whisper, vad);
    }

    /// <summary>Anchors relative paths to the application rather than the launching shell.</summary>
    private static string ApplicationPath(string path) =>
        Path.GetFullPath(path, AppDomain.CurrentDomain.BaseDirectory);

    private static string ValueOrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>Local names control storage only; each remote URL identifies the complete source file.</summary>
    private static async Task<string> ResolveAsync(IConfiguration config, string modelsDir, string key,
        string description, string defaultName, string defaultUrl, string defaultHash)
    {
        string? exact = config[$"SttSettings:Exact{key}FilePath"];
        if (!string.IsNullOrWhiteSpace(exact))
        {
            string path = ApplicationPath(exact.Trim());
            if (!File.Exists(path))
                throw new FileNotFoundException($"Configured {description} model does not exist: {path}");
            Console.WriteLine($"[SYSTEM] Using explicit {description} path: {path}");
            return path;
        }

        string name = ValueOrDefault(config[$"SttSettings:{key}ModelName"], defaultName);
        string destination = Path.GetFullPath(Path.Combine(modelsDir, name));
        string url = ValueOrDefault(config[$"AutoDownload:{key}Url"], defaultUrl);
        string? hash = ExpectedHash(config, key, url, defaultUrl, defaultHash);
        if (File.Exists(destination))
        {
            await VerifyFileHashAsync(destination, hash);
            Console.WriteLine($"[SYSTEM] Found {description} model at: {destination}");
            return destination;
        }

        if (!config.GetValue("AutoDownload:Enable", true))
            throw new FileNotFoundException($"{description} not found at {destination} and AutoDownload is disabled.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var source) ||
            (source.Scheme != Uri.UriSchemeHttps && source.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException($"AutoDownload:{key}Url must be a complete HTTP or HTTPS file URL.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await DownloadFileAsync(source, destination, description, hash);
        return destination;
    }

    /// <summary>Custom checksums are optional; the pinned default URLs always retain digest verification.</summary>
    private static string? ExpectedHash(IConfiguration config, string key, string url, string defaultUrl, string defaultHash)
    {
        string hash = config[$"AutoDownload:{key}Sha256"]?.Trim() ?? "";
        if (hash.Length == 0) return url == defaultUrl ? defaultHash : null;
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidOperationException($"AutoDownload:{key}Sha256 must be empty or a 64-character SHA-256 value.");
        return hash;
    }

    /// <summary>Streams into a temporary file; incomplete or invalid downloads never become model files.</summary>
    private static async Task DownloadFileAsync(Uri url, string destination, string description, string? hash)
    {
        Console.WriteLine($"[INFO] Downloading {description} from its configured URL...");
        var stopwatch = Stopwatch.StartNew();
        string temporary = destination + ".download";
        try
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;
            long received = 0;
            long lastProgress = 0;
            await using var content = await response.Content.ReadAsStreamAsync();
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    // Throttle console writes while retaining streaming, bounded-memory downloads.
                    if (total > 0 && stopwatch.ElapsedMilliseconds - lastProgress >= 250)
                    {
                        Console.Write($"\r[DOWNLOAD] {description}: {received / (double)total:P0}");
                        lastProgress = stopwatch.ElapsedMilliseconds;
                    }
                }
                await output.FlushAsync();
            }
            if (received == 0 || (total >= 0 && received != total))
                throw new InvalidDataException($"Incomplete {description} download: expected {total} bytes, received {received}.");
            await VerifyFileHashAsync(temporary, hash);
            File.Move(temporary, destination, overwrite: true);
            Console.WriteLine($"\n[SUCCESS] Downloaded {description} ({received / 1048576d:F1} MB) in {stopwatch.Elapsed.TotalSeconds:F1} seconds.");
        }
        catch (Exception ex)
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw new IOException($"Failed to download {description}. Error: {ex.Message}", ex);
        }
    }

    /// <summary>Checks managed local and newly downloaded weights when a digest is available.</summary>
    private static async Task VerifyFileHashAsync(string path, string? expected)
    {
        if (expected is null) return;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 mismatch for {path}. Expected {expected}, got {actual}.");
    }
}
