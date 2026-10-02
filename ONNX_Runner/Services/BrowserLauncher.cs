using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ONNX_Runner.Services;

public static class BrowserLauncher
{
    public static bool TryOpen(string url, out string? error)
    {
        error = null;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }) != null;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return TryOpenLinux(url, out error);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var startInfo = new ProcessStartInfo("open")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add(url);
                return Process.Start(startInfo) != null;
            }

            error = "Automatic browser launch is not supported on this operating system.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryOpenLinux(string url, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            error = "No graphical Linux session was detected.";
            return false;
        }

        string? setsid = FindExecutable("setsid");
        string? xdgOpen = FindExecutable("xdg-open");
        if (setsid == null || xdgOpen == null)
        {
            error = "Linux browser launch requires setsid and xdg-open.";
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            "exec \"$TSUBAKI_SETSID\" -f \"$TSUBAKI_XDG_OPEN\" \"$TSUBAKI_BROWSER_URL\" </dev/null >/dev/null 2>&1");
        startInfo.Environment["TSUBAKI_SETSID"] = setsid;
        startInfo.Environment["TSUBAKI_XDG_OPEN"] = xdgOpen;
        startInfo.Environment["TSUBAKI_BROWSER_URL"] = url;

        return Process.Start(startInfo) != null;
    }

    private static string? FindExecutable(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
