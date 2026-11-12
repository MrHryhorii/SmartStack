using System.Globalization;
using System.Security.Cryptography;
using Tsubaki.ServiceCheck.Core.Audio;
using Tsubaki.ServiceCheck.Core.Comparison;
using Tsubaki.ServiceCheck.Core.Diagnostics;

namespace Tsubaki.ServiceCheck.Core.Reports;

/// <summary>
/// Describes published artifacts and their canonical diagnostic result.
/// </summary>
public sealed record ReportArtifacts(
    DiagnosticRunResult Result,
    string RunDirectory,
    string JsonPath,
    string HtmlPath,
    string? AudioPath);

/// <summary>Describes published comparison artifacts and their source evidence.</summary>
public sealed record ComparisonArtifacts(
    RunComparisonResult Result,
    string ComparisonDirectory,
    string JsonPath,
    string HtmlPath);

/// <summary>
/// Preserves available artifact paths when publication fails.
/// </summary>
public sealed class ArtifactPublicationException : IOException
{
    public string? WorkingDirectory { get; }

    public IReadOnlyList<string> SurvivingFiles { get; }

    /// <summary>Stores publication failure details and surviving file paths.</summary>
    public ArtifactPublicationException(
        string message,
        string? workingDirectory,
        IReadOnlyList<string> survivingFiles,
        Exception innerException)
        : base(message, innerException)
    {
        WorkingDirectory = workingDirectory;
        SurvivingFiles = survivingFiles;
    }
}

/// <summary>
/// Publishes diagnostic and comparison artifacts through staging directories.
/// </summary>
public static class ArtifactWriter
{
    private const string JsonFileName = "report.json";
    private const string HtmlFileName = "report.html";
    private const string AudioFileName = "sample.wav";
    private const string ComparisonJsonFileName = "comparison.json";
    private const string ComparisonHtmlFileName = "comparison.html";

    private static readonly TimeSpan PublicationTimeout =
        TimeSpan.FromSeconds(10);

    /// <summary>
    /// Writes artifacts and publishes a new run directory without overwriting.
    /// </summary>
    public static async Task<ReportArtifacts> WriteAsync(
        string outputRoot,
        DiagnosticRunResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(result);

        ValidateOutputId(result.RunId, "runId");

        var rootDirectory = Path.GetFullPath(outputRoot);
        var finalDirectory = Path.Combine(rootDirectory, result.RunId);

        string? workingDirectory = null;

        using var deadline = new CancellationTokenSource(PublicationTimeout);

        using var publicationCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, deadline.Token);

        var token = publicationCancellation.Token;

        try
        {
            token.ThrowIfCancellationRequested();

            var payload = GetRetainablePayload(result);

            var prepared = result with
            {
                Audio = result.Audio is null
                    ? null
                    : result.Audio with
                    {
                        Retained = payload is not null,
                        SamplePath = payload is null ? null : AudioFileName
                    },
                AudioPayload = payload
            };

            var json = ReportJson.Serialize(prepared);

            token.ThrowIfCancellationRequested();

            Directory.CreateDirectory(rootDirectory);

            if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
            {
                throw new IOException(
                    "The run output location already exists.");
            }

            var stagingDirectory = Path.Combine(
                rootDirectory,
                $".tsubaki-check-{Guid.NewGuid():N}.tmp");

            Directory.CreateDirectory(stagingDirectory);
            workingDirectory = stagingDirectory;

            if (payload is not null)
            {
                await WriteFileAsync(
                    stagingDirectory,
                    AudioFileName,
                    async (stream, writeToken) =>
                        await stream.WriteAsync(payload.AsMemory(), writeToken),
                    token);
            }

            await WriteFileAsync(
                stagingDirectory,
                JsonFileName,
                async (stream, writeToken) =>
                    await stream.WriteAsync(json.AsMemory(), writeToken),
                token);

            await WriteFileAsync(
                stagingDirectory,
                HtmlFileName,
                (stream, writeToken) =>
                    HtmlReportWriter.WriteAsync(stream, prepared, writeToken),
                token);

            token.ThrowIfCancellationRequested();

            Directory.Move(stagingDirectory, finalDirectory);
            workingDirectory = finalDirectory;

            return new ReportArtifacts(
                prepared with { AudioPayload = null },
                finalDirectory,
                Path.Combine(finalDirectory, JsonFileName),
                Path.Combine(finalDirectory, HtmlFileName),
                payload is null
                    ? null
                    : Path.Combine(finalDirectory, AudioFileName));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var message = GetFailureMessage(exception, finalDirectory);

            throw new ArtifactPublicationException(
                message,
                workingDirectory,
                FindSurvivingFiles(workingDirectory),
                exception);
        }
    }

    /// <summary>Publishes an offline comparison without copying referenced audio files.</summary>
    public static async Task<ComparisonArtifacts> WriteComparisonAsync(
        string outputRoot,
        RunComparisonResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(result);

        ValidateOutputId(result.ComparisonId, "comparisonId");

        var rootDirectory = Path.GetFullPath(outputRoot);
        var finalDirectory = Path.Combine(rootDirectory, result.ComparisonId);
        string? workingDirectory = null;

        using var deadline = new CancellationTokenSource(PublicationTimeout);
        using var publicationCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = publicationCancellation.Token;

        try
        {
            token.ThrowIfCancellationRequested();

            var prepared = result with
            {
                Before = result.Before with { AudioPayload = null },
                After = result.After with { AudioPayload = null }
            };
            var json = ReportJson.Serialize(prepared);

            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(rootDirectory);

            if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
                throw new IOException("The comparison output location already exists.");

            var stagingDirectory = Path.Combine(
                rootDirectory, $".tsubaki-compare-{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(stagingDirectory);
            workingDirectory = stagingDirectory;

            await WriteFileAsync(
                stagingDirectory,
                ComparisonJsonFileName,
                async (stream, writeToken) =>
                    await stream.WriteAsync(json.AsMemory(), writeToken),
                token);
            await WriteFileAsync(
                stagingDirectory,
                ComparisonHtmlFileName,
                (stream, writeToken) => HtmlComparisonWriter.WriteAsync(stream, prepared, writeToken),
                token);

            token.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, finalDirectory);
            workingDirectory = finalDirectory;

            return new ComparisonArtifacts(
                prepared,
                finalDirectory,
                Path.Combine(finalDirectory, ComparisonJsonFileName),
                Path.Combine(finalDirectory, ComparisonHtmlFileName));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new ArtifactPublicationException(
                GetFailureMessage(exception, finalDirectory, "Comparison"),
                workingDirectory,
                FindSurvivingFiles(workingDirectory),
                exception);
        }
    }

    /// <summary>Requires a UTC timestamp and hexadecimal suffix before constructing a path.</summary>
    private static void ValidateOutputId(string? identifier, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(identifier) ||
            identifier.Length != 29 ||
            identifier[16] != '_' ||
            !DateTimeOffset.TryParseExact(
                identifier[..16],
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _) ||
            !identifier[17..].All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException(
                "The output identifier has an unsupported format.",
                parameterName);
        }
    }

    /// <summary>
    /// Returns supported audio only when its size and hash match the inspection.
    /// </summary>
    private static byte[]? GetRetainablePayload(DiagnosticRunResult result)
    {
        if (result.Audio is not
            {
                Inspection:
                {
                    ContainerState: WavContainerState.Valid
                }
            } audio ||
            result.AudioPayload is not { Length: > 0 } payload)
        {
            return null;
        }

        if (payload.Length > result.Profile.Limits.MaxAudioBytes ||
            payload.LongLength != audio.Inspection.PayloadBytes)
        {
            throw new InvalidDataException(
                "Retained audio size does not match the diagnostic evidence.");
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(payload)).ToLowerInvariant();

        if (!string.Equals(
            hash,
            audio.Inspection.PayloadSha256,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Retained audio hash does not match the diagnostic evidence.");
        }

        return payload;
    }

    /// <summary>
    /// Completes one temporary file before publishing its controlled filename.
    /// </summary>
    private static async Task WriteFileAsync(
        string directory,
        string fileName,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(directory, fileName + ".tmp");
        var finalPath = Path.Combine(directory, fileName);

        await using (var stream = new FileStream(
            temporaryPath,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 16384,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }))
        {
            await write(stream, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        File.Move(temporaryPath, finalPath, overwrite: false);
    }

    /// <summary>
    /// Finds only utility-controlled completed and temporary artifact files.
    /// </summary>
    private static string[] FindSurvivingFiles(string? directory)
    {
        if (directory is null)
        {
            return [];
        }

        string[] names =
        [
            JsonFileName,
            HtmlFileName,
            AudioFileName,
            ComparisonJsonFileName,
            ComparisonHtmlFileName,
            JsonFileName + ".tmp",
            HtmlFileName + ".tmp",
            AudioFileName + ".tmp",
            ComparisonJsonFileName + ".tmp",
            ComparisonHtmlFileName + ".tmp"
        ];

        return names
            .Select(name => Path.Combine(directory, name))
            .Where(File.Exists)
            .ToArray();
    }

    /// <summary>
    /// Describes publication failures without exporting raw exception messages.
    /// </summary>
    private static string GetFailureMessage(
        Exception exception,
        string finalDirectory,
        string artifactKind = "Diagnostic")
    {
        if (exception is OperationCanceledException)
        {
            return "Report publication was interrupted before completion.";
        }

        if (exception is UnauthorizedAccessException)
        {
            return "The output location could not be accessed.";
        }

        if (exception is InvalidDataException)
        {
            return "Retained audio does not match the diagnostic evidence.";
        }

        if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
        {
            return "An output with the same identifier already occupies the output location.";
        }

        return $"{artifactKind} artifacts could not be published.";
    }
}
