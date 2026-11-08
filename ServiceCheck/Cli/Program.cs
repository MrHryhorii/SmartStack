using System.Reflection;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Cli;

/// <summary>
/// Dispatches supported utility commands.
/// </summary>
internal static class Program
{
    private const int SuccessExitCode = 0;
    private const int InvalidArgumentsExitCode = 2;
    private const int ExecutionErrorExitCode = 3;

    /// <summary>Dispatches arguments to the selected command.</summary>
    private static async Task<int> Main(string[] args)
    {
        return args switch
        {
            [] or ["--help"] or ["-h"] => ShowHelp(),
            ["--version"] => ShowVersion(),
            ["init", "--help"] or ["init", "-h"] => ShowHelp(),
            ["check", "--help"] or ["check", "-h"] => ShowHelp(),
            ["init", "--output", var path] =>
                await InitializeProfileAsync(path),
            ["check", .. var checkArguments] =>
                await CheckCommand.ExecuteAsync(checkArguments, GetVersion()),
            _ => ShowArgumentError()
        };
    }

    /// <summary>Shows available commands, options, and exit codes.</summary>
    private static int ShowHelp()
    {
        Console.WriteLine("""
            Tsubaki Service Check
            Diagnostic utility for local and remote Tsubaki TTS servers.

            Usage:
              TsubakiCheck --help
              TsubakiCheck --version
              TsubakiCheck init --output <profile.json>
              TsubakiCheck check --profile <profile.json> --output <directory> [--open]

            Commands:
              init   Create an editable target profile.
              check  Send one non-streaming speech request, inspect WAV audio,
                     and save JSON and offline HTML reports.

            Options:
              -h, --help       Show usage information.
              --version        Show the utility version.
              --profile <path> Select the target profile for a check.
              --output <path>  Select the profile file or report output directory.
              --open           Open the newly published HTML report.

            Exit codes:
              0    Required checks passed; supporting warnings are allowed.
              1    A required diagnostic check failed.
              2    Invalid arguments, profile, or configured authentication.
              3    Incomplete diagnostics or report publication failure.
              130  User cancellation.

            Quote paths that contain spaces.
            """);

        return SuccessExitCode;
    }

    /// <summary>Shows the actual informational version of the CLI assembly.</summary>
    private static int ShowVersion()
    {
        Console.WriteLine($"TsubakiCheck {GetVersion()}");

        return SuccessExitCode;
    }

    /// <summary>Returns the utility version and available source revision metadata.</summary>
    private static string GetVersion()
    {
        var assembly = typeof(Program).Assembly;

        return assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    /// <summary>Creates an example profile using a new output file.</summary>
    private static async Task<int> InitializeProfileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ShowArgumentError("An output path is required.");
        }

        string absolutePath;

        try
        {
            absolutePath = Path.GetFullPath(path);

            await ProfileWriter.WriteExampleAsync(absolutePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return ShowArgumentError("Invalid output path.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                "Could not create the profile. Check write permissions, " +
                "the parent directory, and whether the file already exists.");

            return ExecutionErrorExitCode;
        }

        Console.WriteLine($"Profile created: {absolutePath}");

        return SuccessExitCode;
    }

    /// <summary>Reports invalid syntax without echoing argument values.</summary>
    private static int ShowArgumentError(string message = "Invalid arguments.")
    {
        Console.Error.WriteLine(
            $"{message} Use 'TsubakiCheck --help' for usage.");

        return InvalidArgumentsExitCode;
    }
}