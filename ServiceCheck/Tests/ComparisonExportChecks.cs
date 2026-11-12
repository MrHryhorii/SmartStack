using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Comparison;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Profiles;
using Tsubaki.ServiceCheck.Core.Reports;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>Verifies comparison formats, offline behavior, and safe publication.</summary>
internal static class ComparisonExportChecks
{
    private const string ComparisonId = "20261112T100000Z_abcdef123456";
    private const string CookieValue = "ComparisonCookie_62G";
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 12, 12, 0, 0, TimeSpan.FromHours(2));

    /// <summary>Returns comparison JSON, HTML, privacy, and filesystem checks.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks() =>
    [
        ("Comparison JSON contract and source order verified", VerifyJsonAsync),
        ("Unsupported comparison JSON schema rejected", VerifySchemaAsync),
        ("Hostile comparison text encoded in every rendered context", VerifyEncodingAsync),
        ("Comparison HTML preserves limited context and missing values", VerifyCoverageAsync),
        ("Comparison references remain offline and audio bytes excluded",
            () => WithOutputAsync(VerifyReferencesAsync)),
        ("Comparison publication preserves both source reports",
            () => WithOutputAsync(VerifyPublicationAsync)),
        ("Existing comparison directory preserved",
            () => WithOutputAsync(VerifyDirectoryCollisionAsync)),
        ("Existing comparison file preserved",
            () => WithOutputAsync(VerifyFileCollisionAsync)),
        ("Unsafe comparison identifiers rejected before publication",
            () => WithOutputAsync(VerifyIdentifiersAsync)),
        ("Comparison output failure reported without private exception text",
            () => WithOutputAsync(VerifyOutputFailureAsync)),
        ("Comparison publication honors its cancellation token",
            () => WithOutputAsync(VerifyPublicationCancellationAsync)),
        ("Comparison HTML honors cancellation before and during writing", VerifyHtmlCancellationAsync),
        ("Comparison HTML write errors preserve destination ownership", VerifyHtmlFailureAsync),
        ("Comparison HTML rejects invalid destinations and schema", VerifyHtmlArgumentsAsync),
        ("Comparison exports independent of current culture", VerifyCultureAsync),
        ("Comparison artifacts omit credentials input and cookies",
            () => WithOutputAsync(VerifyPrivacyAsync))
    ];

    /// <summary>Checks stable names, textual enums, typed values, UTC, and explicit source order.</summary>
    private static async Task VerifyJsonAsync()
    {
        var before = await CreateRunAsync(HttpStatusCode.ServiceUnavailable);
        var after = await CreateRunAsync();
        var comparison = Compare(before, after);
        var bytes = ReportJson.Serialize(comparison);
        Require(!bytes.AsSpan().StartsWith("\uFEFF"u8), "Comparison JSON contains a UTF-8 BOM.");

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Require(root.GetProperty("schemaVersion").GetInt32() == 1
            && root.GetProperty("comparisonId").GetString() == ComparisonId
            && root.GetProperty("createdAtUtc").GetString()!.EndsWith('Z')
            && root.GetProperty("createdAtUtc").GetDateTimeOffset() == CreatedAt
            && root.GetProperty("before").GetProperty("runId").GetString() == before.RunId
            && root.GetProperty("after").GetProperty("runId").GetString() == after.RunId,
            "Comparison metadata or explicit source order changed during serialization.");

        Require(root.GetProperty("before").GetProperty("outcome").GetString() == "failed"
            && root.GetProperty("after").GetProperty("outcome").GetString() == "passed_with_warnings"
            && root.GetProperty("before").GetProperty("audio").ValueKind == JsonValueKind.Null
            && root.GetProperty("scope").GetProperty("outcomesComparable").GetBoolean(),
            "Source outcomes, unavailable evidence, or comparison scope lost their types.");

        var speech = root.GetProperty("checks").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "speech.request");
        Require(speech.GetProperty("change").GetString() == "changed"
            && speech.GetProperty("before").GetProperty("status").GetString() == "failed"
            && speech.GetProperty("after").GetProperty("status").GetString() == "passed",
            "A source status transition became an integer enum or an invented outcome.");

        var fields = root.GetProperty("fields").EnumerateArray().ToArray();
        Require(fields.Any(item => item.GetProperty("after").ValueKind == JsonValueKind.Number)
            && fields.Any(item => item.GetProperty("after").ValueKind is JsonValueKind.True or JsonValueKind.False)
            && !root.GetProperty("after").TryGetProperty("audioPayload", out _),
            "Typed measurements became strings or runtime audio entered canonical JSON.");
    }

    /// <summary>Refuses unsupported comparison versions rather than labeling them as version one.</summary>
    private static async Task VerifySchemaAsync()
    {
        var comparison = await CreateComparisonAsync();
        await ExpectAsync<ArgumentException>(() =>
            Task.FromResult(ReportJson.Serialize(comparison with { SchemaVersion = 2 })));
        await ExpectAsync<ArgumentNullException>(() =>
            Task.FromResult(ReportJson.Serialize((RunComparisonResult)null!)));
    }

    /// <summary>Exercises source text, findings, field values, labels, paths, and units.</summary>
    private static async Task VerifyEncodingAsync()
    {
        const string hostile = "</td><script>alert('fixture')</script><img src=x onerror=alert(1)>\"&";
        var source = await CreateRunAsync();
        source = source with
        {
            Profile = source.Profile with { Name = hostile },
            Checks = source.Checks.Select(check => check.Id == "service.status"
                ? check with
                {
                    Summary = hostile,
                    Code = hostile,
                    Suggestions = [hostile],
                    Http = check.Http! with { ErrorExcerpt = hostile, ErrorExcerptTruncated = true }
                }
                : check).ToArray()
        };
        var comparison = Compare(source, source) with
        {
            Tool = new DiagnosticToolInfo(hostile, hostile),
            ComparisonId = hostile,
            Findings = [new ComparisonFinding(ComparisonFindingSeverity.Warning, hostile, hostile)],
            Fields = [new FieldComparisonResult
            {
                Path = hostile, Label = hostile, Unit = hostile,
                Change = ComparisonChangeKind.Changed,
                Before = JsonSerializer.SerializeToElement(hostile),
                After = JsonSerializer.SerializeToElement(hostile)
            }]
        };
        var html = await RenderAsync(comparison);
        Require(html.Contains(HtmlEncoder.Default.Encode(hostile), StringComparison.Ordinal)
            && !html.Contains(hostile, StringComparison.Ordinal)
            && !html.Contains("<script", StringComparison.OrdinalIgnoreCase)
            && !html.Contains("<img", StringComparison.OrdinalIgnoreCase),
            "Recorded comparison text escaped its plain-text context.");
    }

    /// <summary>Keeps unlike tests, absent checks, and explicit-null claims visibly distinct.</summary>
    private static async Task VerifyCoverageAsync()
    {
        var source = await CreateRunAsync();
        var before = source with
        {
            Checks = source.Checks.Where(check => check.Id != "dns.resolve").ToArray()
        };
        var after = source with { Target = source.Target with { Id = "different-configured-target" } };
        var comparison = Compare(before, after);
        comparison = comparison with
        {
            Fields = [new FieldComparisonResult
            {
                Path = "server.statusSnapshot.maxTextLength",
                Label = "Reported text limit",
                Change = ComparisonChangeKind.BeforeMissing,
                Before = null,
                After = JsonSerializer.SerializeToElement<object?>(null)
            }]
        };
        var html = await RenderAsync(comparison);
        Require(html.Contains("different target or test contexts", StringComparison.Ordinal)
            && html.Contains("Not comparable", StringComparison.Ordinal)
            && html.Contains("<em>Not recorded</em>", StringComparison.Ordinal)
            && html.Contains("(explicit claim)", StringComparison.Ordinal)
            && html.Contains("New observation", StringComparison.Ordinal)
            && !html.Contains("Recorded outcomes:", StringComparison.Ordinal),
            "Limited context, missing evidence, or an explicit-null claim became a functional transition.");
    }

    /// <summary>Exports only data even when historical WAV references point to readable files.</summary>
    private static async Task VerifyReferencesAsync(string root)
    {
        const string marker = "ReferencedAudioMustNotBeRead_93B";
        const string samplePath = "sample.wav";
        var reference = Path.Combine(root, samplePath);
        await File.WriteAllTextAsync(reference, marker);
        var source = await CreateRunAsync();
        var payload = source.AudioPayload!;
        source = source with
        {
            Target = source.Target with { BaseUrl = "[REDACTED]" },
            Audio = source.Audio! with { Retained = true, SamplePath = samplePath }
        };
        var comparison = Compare(source, source) with { Before = source, After = source };
        var previousDirectory = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = root;
            var artifacts = await ArtifactWriter.WriteComparisonAsync(Path.Combine(root, "output"), comparison);
            var json = await File.ReadAllTextAsync(artifacts.JsonPath);
            var html = await File.ReadAllTextAsync(artifacts.HtmlPath);
            using var document = JsonDocument.Parse(json);

            Require(source.AudioPayload == payload && payload.Length > 0
                && artifacts.Result.Before.AudioPayload is null && artifacts.Result.After.AudioPayload is null
                && document.RootElement.GetProperty("before").GetProperty("audio")
                    .GetProperty("samplePath").GetString() == samplePath
                && !document.RootElement.GetProperty("before").TryGetProperty("audioPayload", out _)
                && !document.RootElement.GetProperty("after").TryGetProperty("audioPayload", out _)
                && !json.Contains(marker, StringComparison.Ordinal) && !html.Contains(marker, StringComparison.Ordinal)
                && !json.Contains(Convert.ToBase64String(payload), StringComparison.Ordinal)
                && !html.Contains("data:audio/", StringComparison.Ordinal)
                && !html.Contains("src=", StringComparison.OrdinalIgnoreCase)
                && !html.Contains("href=", StringComparison.OrdinalIgnoreCase)
                && Directory.GetFiles(artifacts.ComparisonDirectory).Length == 2
                && await File.ReadAllTextAsync(reference) == marker,
                "Comparison publication loaded audio references, retained runtime bytes, or altered the source.");
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    /// <summary>Publishes failed-to-successful evidence while preserving input bytes and directory contents.</summary>
    private static async Task VerifyPublicationAsync(string root)
    {
        var beforeBytes = ReportJson.Serialize(await CreateRunAsync(HttpStatusCode.ServiceUnavailable));
        var afterBytes = ReportJson.Serialize(await CreateRunAsync());
        var beforePath = Path.Combine(root, "before report.json");
        var afterPath = Path.Combine(root, "after report.json");
        await File.WriteAllBytesAsync(beforePath, beforeBytes);
        await File.WriteAllBytesAsync(afterPath, afterBytes);
        var comparison = Compare(await ReportLoader.LoadAsync(beforePath), await ReportLoader.LoadAsync(afterPath));
        var output = Path.Combine(root, "comparison output with spaces");
        var artifacts = await ArtifactWriter.WriteComparisonAsync(output, comparison);

        Require(Directory.Exists(artifacts.ComparisonDirectory)
            && Path.GetFileName(artifacts.ComparisonDirectory) == ComparisonId
            && Directory.GetDirectories(output).Length == 1
            && Directory.GetFiles(artifacts.ComparisonDirectory).Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(new[] { "comparison.html", "comparison.json" })
            && (await File.ReadAllBytesAsync(beforePath)).AsSpan().SequenceEqual(beforeBytes)
            && (await File.ReadAllBytesAsync(afterPath)).AsSpan().SequenceEqual(afterBytes),
            "Comparison publication modified a source or left incomplete output in its final directory.");

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(artifacts.JsonPath));
        var html = await File.ReadAllTextAsync(artifacts.HtmlPath);
        Require(document.RootElement.GetProperty("before").GetProperty("outcome").GetString() == "failed"
            && html.Contains("Content-Security-Policy", StringComparison.Ordinal)
            && html.Contains("default-src 'none'", StringComparison.Ordinal)
            && html.Contains("</html>", StringComparison.Ordinal),
            "Publication discarded a failed source, document policy, or complete HTML document.");
    }

    /// <summary>Rejects a duplicate comparison ID without changing either published file.</summary>
    private static async Task VerifyDirectoryCollisionAsync(string root)
    {
        var comparison = await CreateComparisonAsync();
        var artifacts = await ArtifactWriter.WriteComparisonAsync(root, comparison);
        var json = await File.ReadAllBytesAsync(artifacts.JsonPath);
        var html = await File.ReadAllBytesAsync(artifacts.HtmlPath);
        await ExpectAsync<ArtifactPublicationException>(() => ArtifactWriter.WriteComparisonAsync(root, comparison));
        Require((await File.ReadAllBytesAsync(artifacts.JsonPath)).AsSpan().SequenceEqual(json)
            && (await File.ReadAllBytesAsync(artifacts.HtmlPath)).AsSpan().SequenceEqual(html)
            && Directory.GetDirectories(root).Length == 1,
            "A colliding comparison replaced output or created an unnecessary staging directory.");
    }

    /// <summary>Preserves an existing file where the final comparison directory would be placed.</summary>
    private static async Task VerifyFileCollisionAsync(string root)
    {
        var comparison = await CreateComparisonAsync();
        var path = Path.Combine(root, comparison.ComparisonId);
        const string marker = "Existing comparison location.";
        await File.WriteAllTextAsync(path, marker);
        await ExpectAsync<ArtifactPublicationException>(() => ArtifactWriter.WriteComparisonAsync(root, comparison));
        Require(await File.ReadAllTextAsync(path) == marker && Directory.GetDirectories(root).Length == 0,
            "A comparison overwrote an existing file or left a staging directory.");
    }

    /// <summary>Rejects traversal, rooted paths, malformed dates, and malformed suffixes before output.</summary>
    private static async Task VerifyIdentifiersAsync(string root)
    {
        var comparison = await CreateComparisonAsync();
        string[] invalid = ["../escape", "..\\escape", Path.Combine(root, "escape"), "comparison.json",
            "20261399T100000Z_abcdef123456", "20261112T100000Z_abcdef12345x", ""];
        foreach (var id in invalid)
            await ExpectAsync<ArgumentException>(() =>
                ArtifactWriter.WriteComparisonAsync(root, comparison with { ComparisonId = id }));
        Require(!Directory.EnumerateFileSystemEntries(root).Any(),
            "An unsafe comparison identifier caused filesystem changes.");
    }

    /// <summary>Reports output failure using safe text and preserves an occupied output root.</summary>
    private static async Task VerifyOutputFailureAsync(string root)
    {
        var occupied = Path.Combine(root, "private-output-location-47Q");
        const string marker = "Preserve the existing output root.";
        await File.WriteAllTextAsync(occupied, marker);
        var comparison = await CreateComparisonAsync();
        var error = await ExpectAsync<ArtifactPublicationException>(() =>
            ArtifactWriter.WriteComparisonAsync(occupied, comparison));
        Require(error.WorkingDirectory is null && error.SurvivingFiles.Count == 0
            && !error.Message.Contains(occupied, StringComparison.Ordinal)
            && await File.ReadAllTextAsync(occupied) == marker,
            "An output error exposed private exception text, invented artifacts, or changed the existing file.");
    }

    /// <summary>Stops a cancelled publication before creating output and permits a fresh publication.</summary>
    private static async Task VerifyPublicationCancellationAsync(string root)
    {
        var comparison = await CreateComparisonAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = Path.Combine(root, "cancelled publication");
        var error = await ExpectAsync<ArtifactPublicationException>(() =>
            ArtifactWriter.WriteComparisonAsync(output, comparison, cancellation.Token));
        Require(error.InnerException is OperationCanceledException && !Directory.Exists(output),
            "Cancelled comparison publication wrote files or lost its cancellation category.");
        var artifacts = await ArtifactWriter.WriteComparisonAsync(output, comparison);
        Require(File.Exists(artifacts.JsonPath) && File.Exists(artifacts.HtmlPath),
            "A cancelled attempt altered the source or prevented a later valid publication.");
    }

    /// <summary>Verifies pre-cancellation and interruption during a real buffered stream write.</summary>
    private static async Task VerifyHtmlCancellationAsync()
    {
        var comparison = await CreateComparisonAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var untouched = new MemoryStream();
        await ExpectAsync<OperationCanceledException>(() =>
            HtmlComparisonWriter.WriteAsync(untouched, comparison, cancellation.Token));
        Require(untouched.Length == 0 && untouched.CanWrite, "Pre-cancelled HTML writing touched its destination.");

        using var interrupted = new CancellationTokenSource();
        using var output = new InterruptingStream(interrupted);
        comparison = comparison with
        {
            Fields = [new FieldComparisonResult
            {
                Path = "fixture.largeValue", Label = "Large recorded value",
                Change = ComparisonChangeKind.Changed,
                Before = JsonSerializer.SerializeToElement(new string('A', 65536)),
                After = JsonSerializer.SerializeToElement(new string('B', 65536))
            }]
        };
        await ExpectAsync<OperationCanceledException>(() =>
            HtmlComparisonWriter.WriteAsync(output, comparison, interrupted.Token));
        Require(interrupted.IsCancellationRequested && output.CanWrite,
            "HTML writing ignored interruption or closed the caller-owned stream.");
    }

    /// <summary>Propagates stream failures while preserving the caller's destination ownership.</summary>
    private static async Task VerifyHtmlFailureAsync()
    {
        var comparison = await CreateComparisonAsync();
        using var output = new FailingStream();
        await ExpectAsync<IOException>(() => HtmlComparisonWriter.WriteAsync(output, comparison));
        Require(output.CanWrite, "A failed HTML write closed its caller-owned destination.");
    }

    /// <summary>Rejects invalid destinations and unsupported schemas before writing bytes.</summary>
    private static async Task VerifyHtmlArgumentsAsync()
    {
        var comparison = await CreateComparisonAsync();
        using var readOnly = new MemoryStream(new byte[1], writable: false);
        await ExpectAsync<ArgumentException>(() => HtmlComparisonWriter.WriteAsync(readOnly, comparison));
        using var output = new MemoryStream();
        await ExpectAsync<ArgumentException>(() =>
            HtmlComparisonWriter.WriteAsync(output, comparison with { SchemaVersion = 2 }));
        Require(output.Length == 0 && output.CanWrite, "Invalid HTML input partially wrote or closed the destination.");
    }

    /// <summary>Keeps the same typed evidence and HTML measurements under different cultures.</summary>
    private static async Task VerifyCultureAsync()
    {
        var comparison = await CreateComparisonAsync();
        var json = ReportJson.Serialize(comparison);
        var html = await RenderAsync(comparison);
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var name in new[] { "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                Require(ReportJson.Serialize(comparison).AsSpan().SequenceEqual(json)
                    && await RenderAsync(comparison) == html,
                    "Comparison export depends on the process culture.");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    /// <summary>Exercises actual diagnostic redaction before JSON and HTML comparison publication.</summary>
    private static async Task VerifyPrivacyAsync(string root)
    {
        const string secret = "ComparisonBearer_18A";
        const string input = "Private comparison input sentence.";
        var variable = $"TSUBAKI_COMPARE_EXPORT_{Guid.NewGuid():N}";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, secret);
        try
        {
            var profile = CreateProfile() with
            {
                Name = $"Private {secret}",
                Target = new ProfileTarget { Id = $"target-{secret}", BaseUrl = $"http://127.0.0.1:5045/{secret}/" },
                Routes = new ProfileRoutes { Speech = $"v1/{secret}/speech" },
                Authentication = new ProfileAuthentication { Type = "bearer", TokenEnvironmentVariable = variable },
                Synthesis = new ProfileSynthesis { Model = $"model-{secret}", Voice = $"voice-{secret}", Input = input }
            };
            var body = JsonSerializer.SerializeToUtf8Bytes(new { error = $"Token {secret}. Input: {input}" });
            var source = await CreateRunAsync(HttpStatusCode.Unauthorized, profile, body);
            var comparison = Compare(source, source);
            var artifacts = await ArtifactWriter.WriteComparisonAsync(root, comparison);
            foreach (var path in new[] { artifacts.JsonPath, artifacts.HtmlPath })
            {
                var text = await File.ReadAllTextAsync(path);
                Require(!text.Contains(secret, StringComparison.Ordinal)
                    && !text.Contains(input, StringComparison.Ordinal)
                    && !text.Contains(CookieValue, StringComparison.Ordinal)
                    && !text.Contains(variable, StringComparison.Ordinal),
                    "A comparison artifact contains credentials, input, a token reference, or cookies.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    /// <summary>Creates two independent fixture runs and compares their recorded data.</summary>
    private static async Task<RunComparisonResult> CreateComparisonAsync() =>
        Compare(await CreateRunAsync(), await CreateRunAsync());

    /// <summary>Supplies fixed publication metadata to the real comparison producer.</summary>
    private static RunComparisonResult Compare(DiagnosticRunResult before, DiagnosticRunResult after) =>
        RunComparer.Compare(before, after, "0.1.0-export-fixture", ComparisonId, CreatedAt);

    /// <summary>Runs controlled HTTP and network fixtures through the actual diagnostic pipeline.</summary>
    private static Task<DiagnosticRunResult> CreateRunAsync(
        HttpStatusCode status = HttpStatusCode.OK, TargetProfile? profile = null, byte[]? payload = null) =>
        DiagnosticRunner.CheckAsync(profile ?? CreateProfile(), "0.1.0-source-fixture",
            new FixtureMetadataHandler(new ResponseHandler(status, payload)),
            networkProbe: FixtureNetworkProbe.Connected);

    /// <summary>Creates a valid local configuration without inference dependencies.</summary>
    private static TargetProfile CreateProfile() => new()
    {
        SchemaVersion = 1, Name = "Comparison export fixture",
        Target = new ProfileTarget { Id = "comparison-export", BaseUrl = "http://127.0.0.1:5045/" }
    };

    /// <summary>Creates a small nonzero PCM16 WAV for actual audio inspection.</summary>
    private static byte[] CreateWav()
    {
        var payload = new byte[60];
        "RIFF"u8.CopyTo(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 52);
        "WAVEfmt "u8.CopyTo(payload.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24), 8000);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(28), 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(34), 16);
        "data"u8.CopyTo(payload.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(40), 16);
        for (var offset = 44; offset < payload.Length; offset += 2)
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(offset), 8192);
        return payload;
    }

    /// <summary>Renders HTML while checking stream ownership and UTF-8 output.</summary>
    private static async Task<string> RenderAsync(RunComparisonResult comparison)
    {
        using var output = new MemoryStream();
        await HtmlComparisonWriter.WriteAsync(output, comparison);
        var bytes = output.ToArray();
        Require(output.CanWrite && !bytes.AsSpan().StartsWith("\uFEFF"u8),
            "HTML writing closed the caller stream or emitted a UTF-8 BOM.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    /// <summary>Provides an isolated filesystem fixture and removes its files after the check.</summary>
    private static async Task WithOutputAsync(Func<string, Task> run)
    {
        var root = Directory.CreateTempSubdirectory("TsubakiCheck-Comparison-").FullName;
        try
        {
            await run(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Requires an operation to fail with the expected error category.</summary>
    private static async Task<TException> ExpectAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException error)
        {
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    /// <summary>Fails a regression check with a focused diagnostic message.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    /// <summary>Provides controlled speech bytes and a cookie that must never be exported.</summary>
    private sealed class ResponseHandler(HttpStatusCode status, byte[]? payload) : HttpMessageHandler
    {
        /// <summary>Returns a fixture response without contacting the configured endpoint.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var successful = status == HttpStatusCode.OK;
            var body = payload ?? (successful ? CreateWav() : "{\"error\":\"Fixture unavailable.\"}"u8.ToArray());
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(successful ? "audio/wav" : "application/json");
            response.Headers.TryAddWithoutValidation("Set-Cookie", $"session={CookieValue}");
            return Task.FromResult(response);
        }
    }

    /// <summary>Cancels the operation after its first actual stream write.</summary>
    private sealed class InterruptingStream(CancellationTokenSource cancellation) : MemoryStream
    {
        /// <summary>Supports writers using the array-based stream overload.</summary>
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var write = base.WriteAsync(buffer, offset, count, cancellationToken);
            cancellation.Cancel();
            return write;
        }

        /// <summary>Completes one buffered write before triggering cancellation.</summary>
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var write = base.WriteAsync(buffer, cancellationToken);
            cancellation.Cancel();
            return write;
        }
    }

    /// <summary>Supplies a real stream boundary that refuses asynchronous writes.</summary>
    private sealed class FailingStream : MemoryStream
    {
        /// <summary>Reports the same failure through the array-based stream overload.</summary>
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("Controlled comparison write failure."));

        /// <summary>Reports a controlled filesystem-style write failure.</summary>
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Controlled comparison write failure."));
    }
}
