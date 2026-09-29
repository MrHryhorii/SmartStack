using System.Diagnostics;

namespace STT_Runner.Services;

/// <summary>Resolves a usable FFmpeg executable once at startup.</summary>
public static class FfmpegManager
{
    public static string ExecutablePath { get; private set; } = "ffmpeg";

    public static async Task EnsureInitializedAsync()
    {
        // Published archives carry FFmpeg; local development may use PATH.
        string localPath = GetLocalFfmpegPath();
        if (File.Exists(localPath))
        {
            try { EnsureUnixExecutePermissions(localPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (await CanRunAsync(localPath))
            {
                ExecutablePath = localPath;
                return;
            }
        }

        if (await CanRunAsync("ffmpeg"))
        {
            ExecutablePath = "ffmpeg";
            return;
        }

        throw new FileNotFoundException(
            "FFmpeg was not found. Use a packaged release or provide ffmpeg on PATH for development.");
    }

    public static string GetLocalFfmpegPath()
    {
        string extension = OperatingSystem.IsWindows() ? ".exe" : "";
        return Path.Combine(AppContext.BaseDirectory, $"ffmpeg{extension}");
    }

    private static async Task<bool> CanRunAsync(string path)
    {
        // Existence alone does not prove that a binary can run on this host.
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "-version" }
            });
            if (process is null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void EnsureUnixExecutePermissions(string path)
    {
        // Downloaded binaries may arrive without the executable permission bit.
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
    }
}
