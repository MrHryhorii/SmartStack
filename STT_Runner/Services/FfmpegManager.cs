using System.Diagnostics;
using System.Runtime.InteropServices;
using Xabe.FFmpeg.Downloader;

namespace STT_Runner.Services;

public static class FfmpegManager
{
    public static string ExecutablePath { get; private set; } = "ffmpeg";

    public static async Task EnsureInitializedAsync()
    {
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

        await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, AppContext.BaseDirectory);
        EnsureUnixExecutePermissions(localPath);
        if (!await CanRunAsync(localPath))
            throw new InvalidOperationException("FFmpeg could not be started after download.");

        ExecutablePath = localPath;
    }

    public static string GetLocalFfmpegPath()
    {
        string extension = OperatingSystem.IsWindows() ? ".exe" : "";
        return Path.Combine(AppContext.BaseDirectory, $"ffmpeg{extension}");
    }

    private static async Task<bool> CanRunAsync(string path)
    {
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
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
    }
}
