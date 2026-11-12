using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Comparison;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Reports;

namespace Tsubaki.ServiceCheck.Cli;

/// <summary>Compares saved diagnostic reports without contacting a server.</summary>
internal static class CompareCommand
{
    private const int SuccessExitCode = 0;
    private const int InvalidArgumentsExitCode = 2;
    private const int ExecutionErrorExitCode = 3;
    private const int CancelledExitCode = 130;

    /// <summary>Parses comparison options and manages caller cancellation.</summary>
    public static async Task<int> ExecuteAsync(string[] args, string toolVersion)
    {
        ComparisonOptions? options;
        try
        {
            options = ParseOptions(args);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ShowArgumentError("Invalid report or output path.");
        }

        if (options is null)
            return ShowArgumentError("Invalid comparison arguments.");

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        try
        {
            return await RunAsync(options, toolVersion, cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>Loads two bounded inputs, compares their evidence, and publishes the result.</summary>
    private static async Task<int> RunAsync(
        ComparisonOptions options, string toolVersion, CancellationToken cancellationToken)
    {
        Console.WriteLine("Loading saved diagnostic reports...");
        DiagnosticRunResult before;
        DiagnosticRunResult after;
        var inputRole = "before";

        try
        {
            before = await ReportLoader.LoadAsync(options.BeforePath, cancellationToken);
            inputRole = "after";
            after = await ReportLoader.LoadAsync(options.AfterPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("The comparison was cancelled while loading its inputs.");
            return CancelledExitCode;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or IOException
                or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine(
                $"Could not load the {inputRole} report. Check its path, read permissions, " +
                "version 1 diagnostic schema, JSON fields, and the 8 MiB size limit.");
            return InvalidArgumentsExitCode;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine("The utility could not load the comparison inputs.");
            return ExecutionErrorExitCode;
        }

        RunComparisonResult result;
        try
        {
            var created = DateTimeOffset.UtcNow;
            var id = created.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
                + "_" + Guid.NewGuid().ToString("N")[..12];
            result = RunComparer.Compare(before, after, toolVersion, id, created, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("The comparison was cancelled before publication.");
            return CancelledExitCode;
        }
        catch (JsonException)
        {
            Console.Error.WriteLine("The source reports contain unsupported or inconsistent diagnostic evidence.");
            return InvalidArgumentsExitCode;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine("The utility could not compare the recorded evidence.");
            return ExecutionErrorExitCode;
        }

        PrintComparison(result);
        ComparisonArtifacts artifacts;

        try
        {
            artifacts = await ArtifactWriter.WriteComparisonAsync(options.OutputRoot, result, cancellationToken);
        }
        catch (ArtifactPublicationException exception)
        {
            Console.Error.WriteLine(exception.Message);

            if (exception.WorkingDirectory is { } directory)
                Console.Error.WriteLine($"Working directory: {Plain(directory)}");

            foreach (var path in exception.SurvivingFiles)
                Console.Error.WriteLine($"Surviving file: {Plain(path)}");

            return cancellationToken.IsCancellationRequested ? CancelledExitCode : ExecutionErrorExitCode;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("The comparison was cancelled during publication.");
            return CancelledExitCode;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine("The utility could not publish the comparison reports.");
            return ExecutionErrorExitCode;
        }

        Console.WriteLine();
        Console.WriteLine("Offline comparison published.");
        Console.WriteLine($"JSON: {Plain(artifacts.JsonPath)}");
        Console.WriteLine($"HTML: {Plain(artifacts.HtmlPath)}");

        if (cancellationToken.IsCancellationRequested)
            return CancelledExitCode;

        if (options.OpenReport)
            TryOpenReport(artifacts.HtmlPath);

        return SuccessExitCode;
    }

    /// <summary>Accepts each supported option exactly once and preserves explicit source roles.</summary>
    private static ComparisonOptions? ParseOptions(string[] args)
    {
        string? before = null;
        string? after = null;
        string? output = null;
        var open = false;

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option == "--open")
            {
                if (open)
                    return null;
                open = true;
                continue;
            }

            if (index + 1 >= args.Length)
                return null;

            var value = args[++index];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                return null;

            switch (option)
            {
                case "--before" when before is null:
                    before = value;
                    break;
                case "--after" when after is null:
                    after = value;
                    break;
                case "--output" when output is null:
                    output = value;
                    break;
                default:
                    return null;
            }
        }

        if (before is null || after is null || output is null)
            return null;

        return new ComparisonOptions(Path.GetFullPath(before), Path.GetFullPath(after),
            Path.GetFullPath(output), open);
    }

    /// <summary>Prints source outcomes as recorded data rather than a comparison pass/fail score.</summary>
    private static void PrintComparison(RunComparisonResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"{Plain(result.Tool.Name)} {Plain(result.Tool.Version)}");
        Console.WriteLine($"Before: {Plain(result.Before.RunId)}; " +
            $"mode: {Label(result.Before.Mode)}; outcome: {Label(result.Before.Outcome)}.");
        Console.WriteLine($"After:  {Plain(result.After.RunId)}; " +
            $"mode: {Label(result.After.Mode)}; outcome: {Label(result.After.Outcome)}.");
        Console.WriteLine($"Before URL: {Plain(result.Before.Target.BaseUrl)}");
        Console.WriteLine($"After URL:  {Plain(result.After.Target.BaseUrl)}");
        Console.WriteLine(result.Scope.OutcomesComparable
            ? "Source outcomes share a configured target and comparable test context."
            : "Source outcomes have different target or test contexts; review them side by side.");
        Console.WriteLine();

        foreach (var finding in result.Findings)
            Console.WriteLine($"{Label(finding.Severity).ToUpperInvariant()} " +
                $"[{Plain(finding.Code)}] {Plain(finding.Summary)}");

        foreach (var check in result.Checks)
            Console.WriteLine($"{Label(check.Change).ToUpperInvariant(),-14} " +
                $"{Plain(check.Id)}: {Plain(check.Summary)}");
    }

    /// <summary>Opens only the newly published report without changing comparison success.</summary>
    private static void TryOpenReport(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine("The report could not be opened automatically. Open the HTML path shown above.");
        }
    }

    /// <summary>Formats canonical enum labels consistently with exported JSON.</summary>
    private static string Label<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>Keeps recorded control characters from acting as terminal instructions.</summary>
    private static string Plain(string value) =>
        string.Concat(value.Select(character => char.IsControl(character) ? ' ' : character));

    /// <summary>Reports invalid syntax without echoing argument values.</summary>
    private static int ShowArgumentError(string message)
    {
        Console.Error.WriteLine($"{message} Use 'TsubakiCheck --help' for usage.");
        return InvalidArgumentsExitCode;
    }

    /// <summary>Stores normalized paths and the optional opening preference.</summary>
    private sealed record ComparisonOptions(string BeforePath, string AfterPath, string OutputRoot, bool OpenReport);
}
