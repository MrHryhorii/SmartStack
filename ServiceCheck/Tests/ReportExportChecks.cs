using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Profiles;
using Tsubaki.ServiceCheck.Core.Reports;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>
/// Verifies canonical JSON, offline HTML, and artifact publication.
/// </summary>
internal static class ReportExportChecks
{
    private const string CookieValue = "FixtureCookie_27R";

    /// <summary>Returns report export regression checks.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks()
    {
        return
        [
            ("Canonical report JSON verified", VerifyJsonContractAsync),
            ("Complete offline artifacts published",
                () => WithOutputAsync(VerifyPublicationAsync)),
            ("Embedded WAV survives base64 chunk boundaries", VerifyEmbeddedWavAsync),
            ("Hostile report text encoded", VerifyHostileTextAsync),
            ("Disabled retention prevents audio export",
                () => WithOutputAsync(VerifyDisabledRetentionAsync)),
            ("Invalid and unsupported audio excluded from artifacts",
                () => WithOutputAsync(VerifyUnsupportedRetentionAsync)),
            ("Existing run artifacts preserved",
                () => WithOutputAsync(VerifyCollisionAsync)),
            ("Unsafe run identifiers rejected",
                () => WithOutputAsync(VerifyRunIdsAsync)),
            ("Modified audio rejected before publication",
                () => WithOutputAsync(VerifyModifiedAudioAsync)),
            ("Local output failure reported",
                () => WithOutputAsync(VerifyOutputFailureAsync)),
            ("Diagnostic cancellation separated from publication",
                () => WithOutputAsync(VerifyCancellationAsync)),
            ("Secrets and input absent from exported files",
                () => WithOutputAsync(VerifyPrivateExportsAsync))
        ];
    }

    /// <summary>Verifies enum strings, UTC timestamps, nulls, and payload exclusion.</summary>
    private static async Task VerifyJsonContractAsync()
    {
        var result = await CreateRunAsync();
        var offset = TimeSpan.FromHours(2);

        result = result with
        {
            ExecutionState = DiagnosticExecutionState.TimedOut,
            Outcome = DiagnosticOutcome.Incomplete,
            StartedAtUtc = result.StartedAtUtc.ToOffset(offset),
            FinishedAtUtc = result.FinishedAtUtc.ToOffset(offset),
            Timings = result.Timings with { SpeechHeadersMs = null },
            Checks = result.Checks.Select(check => check with
            {
                ObservedAtUtc = check.ObservedAtUtc.ToOffset(offset)
            }).ToArray()
        };

        var bytes = ReportJson.Serialize(result);

        Require(
            !bytes.AsSpan().StartsWith("\uFEFF"u8),
            "Canonical JSON must not contain a UTF-8 BOM.");

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        Require(
            root.GetProperty("schemaVersion").GetInt32() == 1 &&
            root.GetProperty("mode").GetString() == "check" &&
            root.GetProperty("executionState").GetString() == "timed_out" &&
            root.GetProperty("outcome").GetString() == "incomplete",
            "Canonical property names or enum values are incorrect.");

        Require(
            root.GetProperty("startedAtUtc").GetString()!.EndsWith('Z') &&
            root.GetProperty("startedAtUtc").GetDateTimeOffset().Offset ==
                TimeSpan.Zero &&
            root.GetProperty("checks")[0]
                .GetProperty("observedAtUtc")
                .GetDateTimeOffset().Offset == TimeSpan.Zero,
            "Report timestamps were not normalized to UTC.");

        Require(
            root.GetProperty("timings")
                .GetProperty("speechHeadersMs").ValueKind == JsonValueKind.Null,
            "Unavailable measurements must remain explicit null values.");

        Require(
            result.AudioPayload is { Length: > 0 } &&
            !root.TryGetProperty("audioPayload", out _),
            "Runtime audio bytes leaked into canonical JSON.");

        var silent = await CreateRunAsync(CreateWav(silent: true));

        using var silentDocument = JsonDocument.Parse(
            ReportJson.Serialize(silent));

        var inspection = silentDocument.RootElement
            .GetProperty("audio")
            .GetProperty("inspection");

        Require(
            inspection.GetProperty("peakDbfs").ValueKind == JsonValueKind.Null &&
            inspection.GetProperty("rmsDbfs").ValueKind == JsonValueKind.Null &&
            inspection.GetProperty("rms").GetDouble() == 0,
            "Silent audio levels must serialize without infinity or NaN.");
    }

    /// <summary>Publishes all artifacts beneath an output path containing spaces.</summary>
    private static async Task VerifyPublicationAsync(string root)
    {
        var output = Path.Combine(root, "output with spaces");
        var payload = CreateWav();
        var result = await CreateRunAsync(payload);

        var artifacts = await ArtifactWriter.WriteAsync(output, result);

        var audioPath = artifacts.AudioPath
            ?? throw new InvalidOperationException(
                "The retained audio path is missing.");

        Require(
            Directory.Exists(artifacts.RunDirectory) &&
            File.Exists(artifacts.JsonPath) &&
            File.Exists(artifacts.HtmlPath) &&
            File.Exists(audioPath),
            "The published run is missing an artifact.");

        var actualAudio = await File.ReadAllBytesAsync(audioPath);

        Require(
            actualAudio.AsSpan().SequenceEqual(payload),
            "The published WAV differs from the received payload.");

        using var document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(artifacts.JsonPath));

        var audio = document.RootElement.GetProperty("audio");

        Require(
            audio.GetProperty("retained").GetBoolean() &&
            audio.GetProperty("samplePath").GetString() == "sample.wav",
            "Canonical audio retention information is incorrect.");

        var names = Directory.GetFiles(artifacts.RunDirectory)
            .Select(path => Path.GetFileName(path))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Require(
            names.SequenceEqual(
                new[] { "report.html", "report.json", "sample.wav" }),
            "The published run contains unexpected or temporary files.");

        Require(
            Directory.GetDirectories(output).Length == 1 &&
            Path.GetFileName(artifacts.RunDirectory) == result.RunId,
            "Successful publication left a staging directory.");

        var html = await File.ReadAllTextAsync(artifacts.HtmlPath);

        Require(
            html.Contains("<audio controls", StringComparison.Ordinal) &&
            html.Contains("Content-Security-Policy", StringComparison.Ordinal),
            "The offline report lacks playback or its document policy.");
    }

    /// <summary>Verifies embedded audio across multiple base64 write chunks.</summary>
    private static async Task VerifyEmbeddedWavAsync()
    {
        var payload = CreateWav(sampleCount: 24577);
        var result = await CreateRunAsync(payload);

        var audio = result.Audio
            ?? throw new InvalidOperationException(
                "Fixture audio inspection is missing.");

        result = result with
        {
            Audio = audio with
            {
                Retained = true,
                SamplePath = "sample.wav"
            }
        };

        var html = await RenderHtmlAsync(result);

        const string marker = "src=\"data:audio/wav;base64,";
        var start = html.IndexOf(marker, StringComparison.Ordinal);

        Require(start >= 0, "Embedded WAV playback is missing.");

        start += marker.Length;

        var end = html.IndexOf('"', start);

        Require(end > start, "The embedded WAV source is incomplete.");

        var decoded = Convert.FromBase64String(html[start..end]);

        Require(
            decoded.AsSpan().SequenceEqual(payload),
            "Chunked base64 encoding changed the embedded WAV.");
    }

    /// <summary>Renders hostile labels and response text as encoded content.</summary>
    private static async Task VerifyHostileTextAsync()
    {
        const string hostile =
            "<script>alert(\"fixture\")</script>" +
            "<img src=\"https://example.test/pixel\">";

        var profile = CreateProfile() with { Name = hostile };

        var result = await CreateRunAsync(
            Encoding.UTF8.GetBytes(hostile),
            profile,
            HttpStatusCode.InternalServerError,
            "text/html");

        var html = await RenderHtmlAsync(result);

        Require(
            html.Contains(
                HtmlEncoder.Default.Encode(hostile),
                StringComparison.Ordinal),
            "Hostile text was not preserved as encoded content.");

        Require(
            !html.Contains("<script", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("<img", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("src=\"http", StringComparison.OrdinalIgnoreCase),
            "Remote text introduced executable or externally loaded markup.");

        Require(
            html.Contains("default-src 'none'", StringComparison.Ordinal),
            "The restrictive document policy is missing.");
    }

    /// <summary>Exports measurements while omitting retained audio entirely.</summary>
    private static async Task VerifyDisabledRetentionAsync(string root)
    {
        var profile = CreateProfile() with
        {
            Output = new ProfileOutput { RetainAudio = false }
        };

        var result = await CreateRunAsync(profile: profile);
        var artifacts = await ArtifactWriter.WriteAsync(root, result);
        var html = await File.ReadAllTextAsync(artifacts.HtmlPath);

        using var document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(artifacts.JsonPath));

        var audio = document.RootElement.GetProperty("audio");

        Require(
            artifacts.AudioPath is null &&
            !File.Exists(Path.Combine(artifacts.RunDirectory, "sample.wav")) &&
            !html.Contains("data:audio/wav;base64,", StringComparison.Ordinal),
            "Disabled retention still exported audio bytes.");

        Require(
            !audio.GetProperty("retained").GetBoolean() &&
            audio.GetProperty("samplePath").ValueKind == JsonValueKind.Null &&
            audio.GetProperty("inspection")
                .GetProperty("signalState").GetString() == "nonzero",
            "Disabled retention lost measurements or reported a retained file.");
    }

    /// <summary>Rejects raw invalid and unsupported payloads as WAV artifacts.</summary>
    private static async Task VerifyUnsupportedRetentionAsync(string root)
    {
        (byte[] Payload, string Outcome)[] cases =
        [
            (
                Encoding.UTF8.GetBytes("Fixture response without WAV audio."),
                "failed"
            ),
            (CreateWav(bitsPerSample: 8), "incomplete")
        ];

        foreach (var (payload, outcome) in cases)
        {
            var result = await CreateRunAsync(payload);

            result = result with { AudioPayload = payload };

            var artifacts = await ArtifactWriter.WriteAsync(root, result);
            var html = await File.ReadAllTextAsync(artifacts.HtmlPath);

            using var document = JsonDocument.Parse(
                await File.ReadAllBytesAsync(artifacts.JsonPath));

            Require(
                artifacts.AudioPath is null &&
                !File.Exists(Path.Combine(artifacts.RunDirectory, "sample.wav")) &&
                !html.Contains("data:audio/wav;base64,", StringComparison.Ordinal),
                "Invalid or unsupported bytes became a WAV artifact.");

            Require(
                document.RootElement.GetProperty("outcome").GetString() == outcome,
                "Publication changed the diagnostic outcome.");
        }
    }

    /// <summary>Preserves existing artifacts when the run identifier collides.</summary>
    private static async Task VerifyCollisionAsync(string root)
    {
        var result = await CreateRunAsync();
        var artifacts = await ArtifactWriter.WriteAsync(root, result);
        var before = await File.ReadAllBytesAsync(artifacts.JsonPath);

        await ExpectExceptionAsync<ArtifactPublicationException>(
            () => ArtifactWriter.WriteAsync(root, result));

        var after = await File.ReadAllBytesAsync(artifacts.JsonPath);

        Require(
            before.AsSpan().SequenceEqual(after) &&
            Directory.GetDirectories(root).Length == 1,
            "A colliding publication changed the existing run.");
    }

    /// <summary>Rejects path traversal before creating artifact files.</summary>
    private static async Task VerifyRunIdsAsync(string root)
    {
        var result = await CreateRunAsync();

        string[] invalidIds = ["../escape", "..\\escape", "report.json"];

        foreach (var runId in invalidIds)
        {
            var invalid = result with { RunId = runId };

            await ExpectExceptionAsync<ArgumentException>(
                () => ArtifactWriter.WriteAsync(root, invalid));
        }

        Require(
            !Directory.EnumerateFileSystemEntries(root).Any(),
            "An unsafe run identifier created output files.");
    }

    /// <summary>Rejects payload bytes changed after their audio inspection.</summary>
    private static async Task VerifyModifiedAudioAsync(string root)
    {
        var result = await CreateRunAsync();

        result.AudioPayload![^1] ^= 0x01;

        var exception = await ExpectExceptionAsync<ArtifactPublicationException>(
            () => ArtifactWriter.WriteAsync(root, result));

        Require(
            exception.InnerException is InvalidDataException &&
            !Directory.EnumerateFileSystemEntries(root).Any(),
            "Modified audio was published or not classified as an integrity failure.");
    }

    /// <summary>Reports a blocked output location without altering its file.</summary>
    private static async Task VerifyOutputFailureAsync(string root)
    {
        var occupied = Path.Combine(root, "occupied-output");
        const string original = "Existing output fixture.";

        await File.WriteAllTextAsync(occupied, original);

        var result = await CreateRunAsync();

        var exception = await ExpectExceptionAsync<ArtifactPublicationException>(
            () => ArtifactWriter.WriteAsync(occupied, result));

        Require(
            exception.WorkingDirectory is null &&
            exception.SurvivingFiles.Count == 0 &&
            await File.ReadAllTextAsync(occupied) == original,
            "Output failure altered the occupied location or invented artifacts.");
    }

    /// <summary>Publishes cancelled results using an independent writing token.</summary>
    private static async Task VerifyCancellationAsync(string root)
    {
        var result = await CreateRunAsync();

        result = result with
        {
            ExecutionState = DiagnosticExecutionState.Cancelled,
            Outcome = DiagnosticOutcome.Incomplete
        };

        var artifacts = await ArtifactWriter.WriteAsync(root, result);

        using var document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(artifacts.JsonPath));

        Require(
            document.RootElement.GetProperty("executionState").GetString() ==
                "cancelled" &&
            document.RootElement.GetProperty("outcome").GetString() ==
                "incomplete",
            "The cancelled diagnostic result could not be preserved.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelledOutput = Path.Combine(root, "cancelled-publication");

        var exception = await ExpectExceptionAsync<ArtifactPublicationException>(
            () => ArtifactWriter.WriteAsync(
                cancelledOutput,
                result,
                cancellation.Token));

        Require(
            exception.InnerException is OperationCanceledException &&
            !Directory.Exists(cancelledOutput),
            "Publication ignored its own cancellation token.");
    }

    /// <summary>Excludes bearer values, private input, and cookies from exports.</summary>
    private static async Task VerifyPrivateExportsAsync(string root)
    {
        const string secret = "FixtureBearer_73a82";
        const string input = "Private report fixture sentence.";

        var variableName = $"TSUBAKI_CHECK_EXPORT_{Guid.NewGuid():N}";
        var previous = Environment.GetEnvironmentVariable(variableName);

        Environment.SetEnvironmentVariable(variableName, secret);

        try
        {
            var profile = CreateProfile() with
            {
                Name = $"Report fixture {secret}",
                Target = new ProfileTarget
                {
                    Id = $"fixture-{secret}",
                    BaseUrl = $"http://127.0.0.1:5045/{secret}/"
                },
                Routes = new ProfileRoutes
                {
                    Speech = $"v1/{secret}/speech"
                },
                Authentication = new ProfileAuthentication
                {
                    Type = "bearer",
                    TokenEnvironmentVariable = variableName
                },
                Synthesis = new ProfileSynthesis
                {
                    Model = $"model-{secret}",
                    Voice = $"voice-{secret}",
                    Input = input
                }
            };

            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                error = $"Fixture rejection. Token: {secret}. Input: {input}"
            });

            var result = await CreateRunAsync(
                body,
                profile,
                HttpStatusCode.Unauthorized,
                "application/json");

            var artifacts = await ArtifactWriter.WriteAsync(root, result);

            string[] exports =
            [
                await File.ReadAllTextAsync(artifacts.JsonPath),
                await File.ReadAllTextAsync(artifacts.HtmlPath)
            ];

            foreach (var text in exports)
            {
                Require(
                    !text.Contains(secret, StringComparison.Ordinal) &&
                    !text.Contains(input, StringComparison.Ordinal) &&
                    !text.Contains(CookieValue, StringComparison.Ordinal),
                    "An exported file contains a secret, original input, or cookie.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
        }
    }

    /// <summary>Runs a controlled response through the actual diagnostic runner.</summary>
    private static Task<DiagnosticRunResult> CreateRunAsync(
        byte[]? payload = null,
        TargetProfile? profile = null,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string mediaType = "audio/wav")
    {
        return DiagnosticRunner.CheckAsync(
            profile ?? CreateProfile(),
            "0.1.0-fixture",
            new ResponseHandler(payload ?? CreateWav(), statusCode, mediaType));
    }

    /// <summary>Creates a valid local profile for export fixtures.</summary>
    private static TargetProfile CreateProfile()
    {
        return new TargetProfile
        {
            SchemaVersion = 1,
            Name = "Report fixture",
            Target = new ProfileTarget
            {
                Id = "report-fixture",
                BaseUrl = "http://127.0.0.1:5045/"
            }
        };
    }

    /// <summary>Creates mono PCM8 or PCM16 WAV bytes at 8000 Hz.</summary>
    private static byte[] CreateWav(
        int sampleCount = 4,
        bool silent = false,
        ushort bitsPerSample = 16)
    {
        var blockAlign = bitsPerSample / 8;
        var dataLength = checked(sampleCount * blockAlign);
        var length = checked(44 + dataLength + (dataLength & 1));
        var payload = new byte[length];

        "RIFF"u8.CopyTo(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(4), (uint)(length - 8));

        "WAVEfmt "u8.CopyTo(payload.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24), 8000);

        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(28), (uint)(8000 * blockAlign));

        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(32), (ushort)blockAlign);

        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(34), bitsPerSample);

        "data"u8.CopyTo(payload.AsSpan(36));

        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(40), (uint)dataLength);

        for (var index = 0; index < sampleCount; index++)
        {
            if (bitsPerSample == 16)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    payload.AsSpan(44 + index * 2, 2),
                    silent ? (short)0 : (short)8192);

                continue;
            }

            payload[44 + index] = silent ? (byte)128 : (byte)160;
        }

        return payload;
    }

    /// <summary>Renders HTML and verifies that the destination remains open.</summary>
    private static async Task<string> RenderHtmlAsync(DiagnosticRunResult result)
    {
        using var output = new MemoryStream();

        await HtmlReportWriter.WriteAsync(output, result);

        Require(output.CanWrite, "The HTML writer closed its destination stream.");

        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>Provides an isolated output root and removes it after the check.</summary>
    private static async Task WithOutputAsync(Func<string, Task> run)
    {
        var root = Directory.CreateTempSubdirectory(
            "TsubakiCheck-Reports-").FullName;

        try
        {
            await run(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Requires an operation to fail with the expected exception type.</summary>
    private static async Task<TException> ExpectExceptionAsync<TException>(
        Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            $"Expected exception {typeof(TException).Name}.");
    }

    /// <summary>Fails the current check when its condition is false.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>Supplies one response without contacting an external server.</summary>
    private sealed class ResponseHandler(
        byte[] payload,
        HttpStatusCode statusCode,
        string mediaType) : HttpMessageHandler
    {
        /// <summary>Creates the controlled response and a non-exportable cookie.</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(payload)
            };

            response.Content.Headers.ContentType =
                new MediaTypeHeaderValue(mediaType);

            response.Headers.TryAddWithoutValidation(
                "Set-Cookie",
                $"session={CookieValue}");

            return Task.FromResult(response);
        }
    }
}