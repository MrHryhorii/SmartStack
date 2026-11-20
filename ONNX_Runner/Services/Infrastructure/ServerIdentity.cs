using System.Reflection;

namespace ONNX_Runner.Services.Infrastructure;

/// <summary>Shares the product identity between startup output and API metadata.</summary>
internal static class ServerIdentity
{
    public const string Name = "Tsubaki TTS Engine";
    public static readonly string Version =
        typeof(ServerIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+', 2)[0]
        ?? typeof(ServerIdentity).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    /// <summary>Prints the application identity before any initialization diagnostics.</summary>
    public static void WriteBanner()
    {
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine($"{Name} v{Version}");
            return;
        }

        ConsoleColor previousColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"{Name} v{Version}");
        }
        finally
        {
            Console.ForegroundColor = previousColor;
        }
    }
}
