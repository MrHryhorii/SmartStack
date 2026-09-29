using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace STT_Runner.Services;

/// <summary>Displays usable local endpoints after the server begins listening.</summary>
public static class StartupLinks
{
    private static readonly Regex WildcardHost = new(
        @"(?<=://)(?:\+|\*|0\.0\.0\.0|\[::\])(?=:\d+|/|$)", RegexOptions.Compiled);

    public static void ShowAndOpen(WebApplication app)
    {
        string[] listeners = GetListeners(app);
        foreach (string listener in listeners) Console.WriteLine($"[SERVER] Listening: {listener}");

        Uri? origin = listeners.Select(ToLocalOrigin).FirstOrDefault(uri => uri is not null);
        if (origin is null)
        {
            Console.WriteLine("[SERVER] Could not determine a local address; check the listening log above.");
            return;
        }

        string baseUrl = origin.AbsoluteUri.TrimEnd('/');
        Console.WriteLine($"[SERVER] Browser: {baseUrl}/");
        Console.WriteLine($"[SERVER] OpenAI API base URL: {baseUrl}/v1");
        Console.WriteLine($"[SERVER] Transcriptions: POST {baseUrl}/v1/audio/transcriptions");
        Console.WriteLine($"[SERVER] Translations: POST {baseUrl}/v1/audio/translations");
        Console.WriteLine("[SERVER] Model: whisper-1");

        if (!app.Configuration.GetValue("ServerSettings:OpenBrowserOnStart", true)) return;
        TryOpenBrowser(new Uri(baseUrl + "/"));
    }

    private static string[] GetListeners(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        string[] bound = server.Features.Get<IServerAddressesFeature>()?.Addresses.ToArray() ?? [];
        if (bound.Length > 0) return bound;

        // Kestrel endpoints configured in JSON may not appear in the addresses feature.
        string[] configured = app.Configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Select(section => section["Url"])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
        if (configured.Length > 0) return configured;
        return app.Urls.ToArray();
    }

    private static Uri? ToLocalOrigin(string listener)
    {
        string local = WildcardHost.Replace(listener, "localhost");
        if (!Uri.TryCreate(local, UriKind.Absolute, out Uri? uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static void TryOpenBrowser(Uri page)
    {
        if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Console.WriteLine("[BROWSER] No desktop session detected. Open the browser URL manually.");
            return;
        }

        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                start = new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true };
            }
            else
            {
                start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add(page.AbsoluteUri);
            }

            using Process? process = Process.Start(start);
            if (process is null) Console.WriteLine("[BROWSER] Could not start the default browser.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BROWSER] Could not open the default browser: {ex.Message}");
        }
    }
}
