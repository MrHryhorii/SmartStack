using System.Reflection;

namespace Tsubaki.ServiceCheck.Cli;

internal static class Program
{
    private const int SuccessExitCode = 0;
    private const int InvalidArgumentsExitCode = 2;

    /// <summary>Dispatches supported options and returns the process exit code.</summary>
    private static int Main(string[] args) => args switch
    {
        [] or ["--help"] or ["-h"] => ShowHelp(),
        ["--version"] => ShowVersion(),
        _ => ShowArgumentError()
    };

    /// <summary>Writes usage information to standard output.</summary>
    private static int ShowHelp()
    {
        Console.WriteLine(
            """
            Tsubaki Service Check
            Diagnostic utility for local and remote Tsubaki TTS servers.

            Usage:
              TsubakiCheck --help
              TsubakiCheck --version

            Options:
              -h, --help  Show usage information.
              --version   Show the utility version.
            """);

        return SuccessExitCode;
    }

    /// <summary>Writes the utility version from assembly metadata.</summary>
    private static int ShowVersion()
    {
        var assembly = typeof(Program).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        Console.WriteLine($"TsubakiCheck {version}");

        return SuccessExitCode;
    }

    /// <summary>Writes an argument error to standard error and returns its exit code.</summary>
    private static int ShowArgumentError()
    {
        Console.Error.WriteLine(
            "Invalid arguments. Use 'TsubakiCheck --help' for usage.");

        return InvalidArgumentsExitCode;
    }
}