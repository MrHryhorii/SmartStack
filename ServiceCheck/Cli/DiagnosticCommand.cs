using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Audio;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Profiles;
using Tsubaki.ServiceCheck.Core.Reports;

namespace Tsubaki.ServiceCheck.Cli;

/// <summary>
/// Executes check and inspection commands and publishes their reports.
/// </summary>
internal static class DiagnosticCommand
{
    private const int SuccessExitCode = 0;
    private const int FailedCheckExitCode = 1;
    private const int InvalidArgumentsExitCode = 2;
    private const int ExecutionErrorExitCode = 3;
    private const int CancelledExitCode = 130;

    /// <summary>Parses command options and manages caller cancellation.</summary>
    public static async Task<int> ExecuteAsync(
        string[] args,
        string toolVersion,
        DiagnosticMode mode)
    {
        if (mode is not (DiagnosticMode.Check or DiagnosticMode.Inspect))
        {
            return ShowArgumentError("Unsupported diagnostic mode.");
        }

        DiagnosticOptions? options;

        try
        {
            options = ParseOptions(args);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return ShowArgumentError("Invalid profile or output path.");
        }

        if (options is null)
        {
            return ShowArgumentError("Invalid diagnostic arguments.");
        }

        using var cancellation = new CancellationTokenSource();

        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += onCancel;

        try
        {
            return await RunAsync(options, toolVersion, mode, cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>Loads a profile, executes diagnostics, and saves available results.</summary>
    private static async Task<int> RunAsync(
        DiagnosticOptions options,
        string toolVersion,
        DiagnosticMode mode,
        CancellationToken cancellationToken)
    {
        TargetProfile profile;

        try
        {
            profile = await ProfileLoader.LoadAsync(
                options.ProfilePath,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(
                "The command was cancelled before diagnostics started.");

            return CancelledExitCode;
        }
        catch (Exception exception) when (
            exception is JsonException or
            ArgumentException or
            IOException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            Console.Error.WriteLine(
                "Could not load a valid profile. Check the file path, JSON fields, " +
                "schema version, settings, and the 64 KiB size limit.");

            return InvalidArgumentsExitCode;
        }

        Console.WriteLine(mode == DiagnosticMode.Inspect
            ? "Running service inspection..."
            : "Running service and speech check...");

        DiagnosticRunResult result;

        try
        {
            result = mode switch
            {
                DiagnosticMode.Check => await DiagnosticRunner.CheckAsync(
                    profile,
                    toolVersion,
                    cancellationToken: cancellationToken),

                DiagnosticMode.Inspect => await DiagnosticRunner.InspectAsync(
                    profile,
                    toolVersion,
                    cancellationToken: cancellationToken),

                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(
                "The command was cancelled before a diagnostic result was available.");

            return CancelledExitCode;
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine(
                "Could not start diagnostics. Check the profile and the environment " +
                "variable referenced by bearer authentication.");

            return InvalidArgumentsExitCode;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine(
                "The utility could not complete the diagnostic run.");

            return ExecutionErrorExitCode;
        }

        PrintDiagnostics(result);

        ReportArtifacts artifacts;

        try
        {
            artifacts = await ArtifactWriter.WriteAsync(
                options.OutputRoot,
                result);
        }
        catch (ArtifactPublicationException exception)
        {
            Console.Error.WriteLine(exception.Message);

            if (exception.WorkingDirectory is { } directory)
            {
                Console.Error.WriteLine($"Working directory: {directory}");
            }

            foreach (var path in exception.SurvivingFiles)
            {
                Console.Error.WriteLine($"Surviving file: {path}");
            }

            return ExecutionErrorExitCode;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine(
                "The utility could not publish the diagnostic reports.");

            return ExecutionErrorExitCode;
        }

        Console.WriteLine();
        Console.WriteLine($"JSON: {artifacts.JsonPath}");
        Console.WriteLine($"HTML: {artifacts.HtmlPath}");

        if (artifacts.AudioPath is { } audioPath)
        {
            Console.WriteLine($"WAV:  {audioPath}");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CancelledExitCode;
        }

        if (options.OpenReport)
        {
            TryOpenReport(artifacts.HtmlPath);
        }

        return GetExitCode(result);
    }

    /// <summary>Accepts each supported option once, in any order.</summary>
    private static DiagnosticOptions? ParseOptions(string[] args)
    {
        string? profilePath = null;
        string? outputRoot = null;
        var openReport = false;

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];

            if (option == "--open")
            {
                if (openReport)
                {
                    return null;
                }

                openReport = true;
                continue;
            }

            if (index + 1 >= args.Length)
            {
                return null;
            }

            var value = args[++index];

            if (string.IsNullOrWhiteSpace(value) ||
                value.StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }

            if (option == "--profile" && profilePath is null)
            {
                profilePath = value;
                continue;
            }

            if (option == "--output" && outputRoot is null)
            {
                outputRoot = value;
                continue;
            }

            return null;
        }

        if (profilePath is null || outputRoot is null)
        {
            return null;
        }

        return new DiagnosticOptions(
            Path.GetFullPath(profilePath),
            Path.GetFullPath(outputRoot),
            openReport);
    }

    /// <summary>Prints sanitized observations and client-measured audio information.</summary>
    private static void PrintDiagnostics(DiagnosticRunResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"{result.Tool.Name} {result.Tool.Version}");
        Console.WriteLine($"Target: {result.Target.Id}");
        Console.WriteLine($"URL: {result.Target.BaseUrl}");
        Console.WriteLine($"Mode: {GetModeLabel(result.Mode)}");

        if (result.Server is { } server)
        {
            Console.WriteLine($"Reported service: {server.Service ?? "unknown"}");
            Console.WriteLine($"Reported version: {server.Version ?? "unknown"}");
            Console.WriteLine($"Reported readiness: {GetReadinessLabel(server.Readiness)}");
        }
        Console.WriteLine();

        foreach (var check in result.Checks)
        {
            Console.WriteLine(
                $"{GetStatusLabel(check.Status),-7} " +
                $"{check.Id} [{check.Code}] {check.Summary}");
        }

        Console.WriteLine();
        Console.WriteLine(GetSummary(result));

        Console.WriteLine(
            $"Execution: {GetExecutionLabel(result.ExecutionState)}; " +
            $"outcome: {GetOutcomeLabel(result.Outcome)}.");

        Console.WriteLine(
            "Client-observed run duration: " +
            result.Timings.TotalMs.ToString(CultureInfo.InvariantCulture) +
            " ms.");

        if (result.Audio?.Inspection is
            { ContainerState: WavContainerState.Valid } audio)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Audio: {audio.DurationSeconds:0.######} s; " +
                $"{audio.SampleRateHz} Hz; {audio.Channels} channel(s); " +
                $"{audio.PayloadBytes} bytes."));
        }
    }

    /// <summary>Describes the requested scope and its mode-specific result.</summary>
    private static string GetSummary(DiagnosticRunResult result)
    {
        if (result.ExecutionState != DiagnosticExecutionState.Completed)
        {
            var message = result.ExecutionState switch
            {
                DiagnosticExecutionState.Cancelled =>
                    "The run was cancelled. Available diagnostic observations were collected.",
                DiagnosticExecutionState.TimedOut =>
                    "The run timed out. Available diagnostic observations were collected.",
                _ => "The diagnostic run could not be completed."
            };

            return result.Mode == DiagnosticMode.Inspect
                ? message + " Speech generation was not tested."
                : message;
        }

        if (result.Mode == DiagnosticMode.Inspect)
        {
            return result.Outcome switch
            {
                DiagnosticOutcome.Passed or DiagnosticOutcome.PassedWithWarnings =>
                    "The server reported the expected identity and that synthesis was ready. Speech generation was not tested.",
                DiagnosticOutcome.Failed =>
                    "Inspection failed a required metadata check. Speech generation was not tested.",
                _ =>
                    "Inspection could not establish the required metadata. Speech generation was not tested."
            };
        }

        return result.Outcome switch
        {
            DiagnosticOutcome.Passed or DiagnosticOutcome.PassedWithWarnings =>
                "Speech generation verified from this client.",
            DiagnosticOutcome.Failed =>
                "The active speech check failed. Review the required checks above.",
            _ => "Speech generation could not be fully verified."
        };
    }

    /// <summary>Maps diagnostic completion to the documented process exit codes.</summary>
    private static int GetExitCode(DiagnosticRunResult result)
    {
        if (result.ExecutionState == DiagnosticExecutionState.Cancelled)
        {
            return CancelledExitCode;
        }

        if (result.ExecutionState != DiagnosticExecutionState.Completed)
        {
            return ExecutionErrorExitCode;
        }

        return result.Outcome switch
        {
            DiagnosticOutcome.Passed or DiagnosticOutcome.PassedWithWarnings =>
                SuccessExitCode,
            DiagnosticOutcome.Failed => FailedCheckExitCode,
            _ => ExecutionErrorExitCode
        };
    }

    /// <summary>Returns the canonical operation-mode label.</summary>
    private static string GetModeLabel(DiagnosticMode mode)
    {
        return mode switch
        {
            DiagnosticMode.Check => "check",
            DiagnosticMode.Inspect => "inspect",
            _ => "unknown"
        };
    }

    /// <summary>Labels readiness as a server-reported observation.</summary>
    private static string GetReadinessLabel(DiagnosticReadiness readiness)
    {
        return readiness switch
        {
            DiagnosticReadiness.Ready => "ready",
            DiagnosticReadiness.NotReady => "not_ready",
            _ => "unknown"
        };
    }

    /// <summary>Returns a plain text status suitable for redirected output.</summary>
    private static string GetStatusLabel(DiagnosticCheckStatus status)
    {
        return status switch
        {
            DiagnosticCheckStatus.Passed => "PASS",
            DiagnosticCheckStatus.Warning => "WARN",
            DiagnosticCheckStatus.Failed => "FAIL",
            DiagnosticCheckStatus.Skipped => "SKIPPED",
            _ => "ERROR"
        };
    }

    /// <summary>Returns the canonical execution-state label.</summary>
    private static string GetExecutionLabel(DiagnosticExecutionState state)
    {
        return state switch
        {
            DiagnosticExecutionState.Completed => "completed",
            DiagnosticExecutionState.Cancelled => "cancelled",
            DiagnosticExecutionState.TimedOut => "timed_out",
            _ => "error"
        };
    }

    /// <summary>Returns the canonical aggregate-outcome label.</summary>
    private static string GetOutcomeLabel(DiagnosticOutcome outcome)
    {
        return outcome switch
        {
            DiagnosticOutcome.Passed => "passed",
            DiagnosticOutcome.PassedWithWarnings => "passed_with_warnings",
            DiagnosticOutcome.Failed => "failed",
            _ => "incomplete"
        };
    }

    /// <summary>Opens the published HTML without changing diagnostic success.</summary>
    private static void TryOpenReport(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine(
                "The report could not be opened automatically. " +
                "Open the HTML path shown above.");
        }
    }

    /// <summary>Reports invalid command syntax without echoing argument values.</summary>
    private static int ShowArgumentError(string message)
    {
        Console.Error.WriteLine(
            $"{message} Use 'TsubakiCheck --help' for usage.");

        return InvalidArgumentsExitCode;
    }

    /// <summary>Stores normalized paths and the optional report-opening preference.</summary>
    private sealed record DiagnosticOptions(
        string ProfilePath,
        string OutputRoot,
        bool OpenReport);
}