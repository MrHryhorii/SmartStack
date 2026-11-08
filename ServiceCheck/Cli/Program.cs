using System.Reflection;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Cli;

internal static class Program
{
    private const int SuccessExitCode = 0;
    private const int InvalidArgumentsExitCode = 2;
    private const int ExecutionErrorExitCode = 3;

    /// <summary>Dispatches supported commands and returns the process exit code.</summary>
    private static async Task<int> Main(string[] args) => args switch
    {
        [] or ["--help"] or ["-h"] => ShowHelp(),
        ["--version"] => ShowVersion(),
        ["init", "--output", var path] => await InitializeProfileAsync(path),
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
              TsubakiCheck init --output <path>
              TsubakiCheck --help
              TsubakiCheck --version

            Commands:
              init             Write an example target profile.

            Options:
              --output <path>  Set the destination file for init.
              -h, --help       Show usage information.
              --version        Show the utility version.
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

    /// <summary>Writes an example profile and reports its outcome.</summary>
    private static async Task<int> InitializeProfileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ShowArgumentError("The output path must not be empty.");

        string outputPath;

        try
        {
            outputPath = Path.GetFullPath(path);
            await ProfileWriter.WriteExampleAsync(outputPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            return ShowArgumentError("Invalid output path.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Could not create the profile: {exception.Message}");

            return ExecutionErrorExitCode;
        }

        Console.WriteLine($"Profile created: {outputPath}");

        return SuccessExitCode;
    }

    /// <summary>Writes an argument error to standard error and returns its exit code.</summary>
    private static int ShowArgumentError(string message = "Invalid arguments.")
    {
        Console.Error.WriteLine(
            $"{message} Use 'TsubakiCheck --help' for usage.");

        return InvalidArgumentsExitCode;
    }
}