using System.Diagnostics;
using System.Runtime.InteropServices;
using Xabe.FFmpeg.Downloader;

namespace WhisperTiny_STT.Services;

/// <summary>
/// Ensures FFmpeg is available on the host system.
/// Automatically downloads the correct static build (Windows/Linux/macOS)
/// and configures execution permissions on Unix systems.
/// </summary>
public static class FfmpegManager
{
    public static async Task EnsureInitializedAsync()
    {
        Console.WriteLine("[SYSTEM] Checking FFmpeg installation...");

        if (IsFfmpegAvailable())
        {
            Console.WriteLine("[SYSTEM] FFmpeg is already installed and available.");
            return;
        }

        Console.WriteLine("[SYSTEM] FFmpeg not found. Downloading the latest static build...");

        // Xabe.FFmpeg.Downloader automatically detects the OS and architecture and downloads the appropriate version.
        await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, AppDomain.CurrentDomain.BaseDirectory);

        // After downloading, ensure the local FFmpeg has execute permissions on Unix systems.
        EnsureUnixExecutePermissions();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[SYSTEM] FFmpeg downloaded and configured successfully.");
        Console.ResetColor();
    }

    /// <summary>
    /// Returns the correct path to the local FFmpeg file depending on the OS.
    /// This method is made public so that AudioProcessor can use it.
    /// </summary>
    public static string GetLocalFfmpegPath()
    {
        string ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"ffmpeg{ext}");
    }

    private static bool IsFfmpegAvailable()
    {
        try
        {
            // First, try to run "ffmpeg -version" to see if it's globally available in the PATH.
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            // If it's not available globally, check if it exists locally in the application directory
            string localPath = GetLocalFfmpegPath();
            return File.Exists(localPath);
        }
    }

    /// <summary>
    /// On Linux and macOS, the downloaded file doesn't have execute permissions by default.
    /// This method calls the system command chmod +x.
    /// </summary>
    private static void EnsureUnixExecutePermissions()
    {
        // If this is Windows, execute permissions are not needed
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        string localPath = GetLocalFfmpegPath();

        if (File.Exists(localPath))
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{localPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                process?.WaitForExit();
                Console.WriteLine("[SYSTEM] Unix execute permissions (chmod +x) granted to local FFmpeg.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[WARNING] Failed to set execute permissions for FFmpeg: {ex.Message}");
                Console.ResetColor();
            }
        }
    }
}