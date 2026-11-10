using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tsubaki.ServiceCheck.Core.Audio;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Profiles;
using Tsubaki.ServiceCheck.Core.Reports;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>Verifies offline report import and malformed-input handling.</summary>
internal static class ReportImportChecks
{
    private const int ReportSizeLimit = 8 * 1024 * 1024;
    private const int TextLimit = 65_536;
    private static readonly Lazy<Task<byte[]>> CanonicalJson = new(CreateCanonicalJsonAsync);

    /// <summary>Returns canonical import, compatibility, and failure regression checks.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks()
    {
        return
        [
            ("Active report imports with canonical evidence", VerifyActiveAsync),
            ("Inspection report imports without speech evidence", VerifyInspectionAsync),
            ("Earlier limited-coverage report imports", VerifyEarlierReportAsync),
            ("Failed and unsupported report outcomes preserved", VerifyUnsuccessfulRunsAsync),
            ("Interrupted reports preserve completed evidence", VerifyInterruptedAsync),
            ("Report UTF-8 BOM accepted", VerifyBomAsync),
            ("Exact report size limit accepted", VerifyExactSizeAsync),
            ("Oversized report rejected", VerifyOversizedAsync),
            ("Malformed report JSON and roots rejected", VerifyMalformedAsync),
            ("Unsupported report schemas rejected", VerifySchemasAsync),
            ("Unknown report properties rejected", VerifyUnknownPropertiesAsync),
            ("Duplicate and escaped duplicate report properties rejected", VerifyDuplicatesAsync),
            ("Missing required report data rejected", VerifyRequiredDataAsync),
            ("Missing exported settings do not acquire defaults", VerifyMissingSettingsAsync),
            ("Null report collection entries rejected", VerifyNullEntriesAsync),
            ("Unknown and numeric report enums rejected", VerifyEnumsAsync),
            ("Report text bound and malformed UTF-8 verified", VerifyTextAsync),
            ("Report evidence collections bounded", VerifyCollectionsAsync),
            ("Excessively nested report JSON rejected", VerifyDepthAsync),
            ("Imported timestamp offsets normalized to UTC", VerifyTimestampOffsetsAsync),
            ("Ambiguous and invalid report timestamps rejected", VerifyTimestampsAsync),
            ("Duplicate check IDs and incorrect roles rejected", VerifyCheckIdentitiesAsync),
            ("Inconsistent imported outcomes rejected", VerifyOutcomesAsync),
            ("Missing checks preserved without fabricated evidence", VerifyMissingChecksAsync),
            ("Passed checks require their recorded evidence", VerifyRequiredEvidenceAsync),
            ("Inconsistent imported audio measurements rejected", VerifyAudioAsync),
            ("Invalid imported profile and request snapshots rejected", VerifySnapshotsAsync),
            ("Stored addresses and audio references remain data", VerifyReferencesAsync),
            ("Runtime payloads and unsafe sample paths rejected", VerifyForbiddenPayloadsAsync),
            ("Cancelled report import stops before opening a file", VerifyCancellationAsync),
            ("Missing report file reported", VerifyMissingFileAsync),
            ("Import preserves source bytes and releases file handles", VerifySourceFileAsync)
        ];
    }

    /// <summary>Round-trips the actual writer output without restoring runtime audio.</summary>
    private static async Task VerifyActiveAsync()
    {
        var bytes = await CanonicalJson.Value;
        var loaded = await ImportAsync(bytes);

        Require(loaded.Mode == DiagnosticMode.Check
            && loaded.Outcome == DiagnosticOutcome.Passed
            && loaded.Request is not null && loaded.Network is not null
            && loaded.Server?.Observations.Count == 3
            && loaded.Audio?.Inspection.SignalState == AudioSignalState.Nonzero
            && loaded.AudioPayload is null,
            "Active import lost canonical evidence or restored runtime audio.");
        Require(ReportJson.Serialize(loaded).AsSpan().SequenceEqual(bytes),
            "The canonical report changed during a write/read round trip.");
    }

    /// <summary>Imports the actual inspection result with its deliberate speech omissions.</summary>
    private static async Task VerifyInspectionAsync()
    {
        var result = await CreateRunAsync(DiagnosticMode.Inspect);
        var loaded = await ImportAsync(ReportJson.Serialize(result));

        Require(loaded.Mode == DiagnosticMode.Inspect
            && loaded.Outcome == DiagnosticOutcome.Passed
            && loaded.Request is null && loaded.Audio is null && loaded.AudioPayload is null
            && loaded.Timings.SpeechTotalMs is null
            && loaded.Checks.Single(check => check.Id == "speech.request").Code == "not_requested",
            "Inspection import invented speech results or lost its operation scope.");
    }

    /// <summary>Accepts the first-phase shape with explicitly unavailable supporting coverage.</summary>
    private static async Task VerifyEarlierReportAsync()
    {
        var root = await CreateRootAsync();
        root["network"] = null;
        root["server"] = null;
        root["outcome"] = "passed_with_warnings";

        foreach (var node in root["checks"]!.AsArray())
        {
            var check = node!.AsObject();

            if (check["role"]!.GetValue<string>() != "supporting")
                continue;

            check["status"] = "skipped";
            check["code"] = "coverage_unavailable";
            check["summary"] = "Supporting coverage was not collected.";
            check["http"] = null;
            check["durationMs"] = null;
        }

        var loaded = await ImportAsync(ToBytes(root));

        Require(loaded.Network is null && loaded.Server is null
            && loaded.Outcome == DiagnosticOutcome.PassedWithWarnings
            && loaded.Audio?.Inspection.ContainerState == WavContainerState.Valid,
            "Earlier supporting gaps were rejected or replaced with invented evidence.");
    }

    /// <summary>Preserves request failure, silent audio, and unsupported-format distinctions.</summary>
    private static async Task VerifyUnsuccessfulRunsAsync()
    {
        var failed = await CreateRunAsync(speechStatus: HttpStatusCode.ServiceUnavailable);
        var silent = await CreateRunAsync(payload: CreateWav(silent: true));
        var unsupported = await CreateRunAsync(payload: CreateWav(unsupported: true));

        foreach (var result in new[] { failed, silent, unsupported })
        {
            var loaded = await ImportAsync(ReportJson.Serialize(result));
            Require(loaded.Outcome == result.Outcome
                && loaded.ExecutionState == result.ExecutionState
                && loaded.Audio?.Inspection.ContainerState == result.Audio?.Inspection.ContainerState
                && loaded.Audio?.Inspection.SignalState == result.Audio?.Inspection.SignalState,
                "An unsuccessful report changed meaning during import.");
        }

        Require(failed.Outcome == DiagnosticOutcome.Failed
            && silent.Outcome == DiagnosticOutcome.Failed
            && unsupported.Outcome == DiagnosticOutcome.Incomplete,
            "The unsuccessful import fixtures did not exercise distinct outcomes.");
    }

    /// <summary>Keeps observed successes when the final execution state is interrupted.</summary>
    private static async Task VerifyInterruptedAsync()
    {
        foreach (var state in new[] { "cancelled", "timed_out", "error" })
        {
            var root = await CreateRootAsync();
            root["executionState"] = state;
            root["outcome"] = "incomplete";
            var loaded = await ImportAsync(ToBytes(root));

            Require(loaded.Outcome == DiagnosticOutcome.Incomplete
                && loaded.ExecutionState != DiagnosticExecutionState.Completed
                && loaded.Checks.Any(check => check.Status == DiagnosticCheckStatus.Passed),
                "Interrupted import discarded observations or reported a completed success.");
        }
    }

    /// <summary>Accepts a single leading UTF-8 BOM.</summary>
    private static async Task VerifyBomAsync()
    {
        var bytes = await CanonicalJson.Value;
        var withBom = new byte[bytes.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        bytes.CopyTo(withBom, 3);
        _ = await ImportAsync(withBom);
    }

    /// <summary>Accepts a complete report padded to the byte ceiling.</summary>
    private static async Task VerifyExactSizeAsync()
    {
        _ = await ImportAsync(await PaddedReportAsync(ReportSizeLimit));
    }

    /// <summary>Rejects a file one byte above the import ceiling.</summary>
    private static async Task VerifyOversizedAsync()
    {
        await ExpectRejectedAsync(await PaddedReportAsync(ReportSizeLimit + 1));
    }

    /// <summary>Rejects invalid syntax and roots that cannot represent a diagnostic run.</summary>
    private static async Task VerifyMalformedAsync()
    {
        foreach (var text in new[] { "", "{", "null", "[]", "true", "\"report\"" })
            await ExpectRejectedAsync(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Rejects absent, mistyped, and unsupported schema identifiers.</summary>
    private static Task VerifySchemasAsync() => VerifyRejectedMutationsAsync(
        root => root.Remove("schemaVersion"),
        root => root["schemaVersion"] = 2,
        root => root["schemaVersion"] = "1",
        root => root["schemaVersion"] = null);

    /// <summary>Rejects unknown members at multiple contract levels.</summary>
    private static Task VerifyUnknownPropertiesAsync() => VerifyRejectedMutationsAsync(
        root => root["unexpected"] = true,
        root => root["target"]!["unexpected"] = true,
        root => root["request"]!["unexpected"] = true);

    /// <summary>Rejects duplicate names, including their escaped JSON spelling.</summary>
    private static async Task VerifyDuplicatesAsync()
    {
        var text = Encoding.UTF8.GetString(await CanonicalJson.Value);

        foreach (var property in new[] { "\"schemaVersion\":1,", "\"\\u0073chemaVersion\":1," })
            await ExpectRejectedAsync(Encoding.UTF8.GetBytes(text.Insert(1, property)));

        const string marker = "\"target\": {";
        Require(text.Contains(marker, StringComparison.Ordinal),
            "The duplicate-name fixture could not locate its target object.");
        await ExpectRejectedAsync(Encoding.UTF8.GetBytes(text.Replace(marker,
            marker + "\"id\":\"duplicate\",", StringComparison.Ordinal)));
    }

    /// <summary>Requires explicit report fields and positional-constructor values.</summary>
    private static Task VerifyRequiredDataAsync() => VerifyRejectedMutationsAsync(
        root => root.Remove("runId"),
        root => root.Remove("mode"),
        root => root["tool"]!.AsObject().Remove("version"),
        root => root["timings"]!.AsObject().Remove("totalMs"),
        root => root["target"] = null,
        root => root["checks"] = null);

    /// <summary>Rejects missing settings that would otherwise receive initializer defaults.</summary>
    private static Task VerifyMissingSettingsAsync() => VerifyRejectedMutationsAsync(
        root => root["profile"]!["routes"]!.AsObject().Remove("speech"),
        root => root["profile"]!["timeoutsMs"]!.AsObject().Remove("dns"),
        root => root["profile"]!["limits"]!.AsObject().Remove("maxAudioBytes"));

    /// <summary>Rejects null elements hidden inside non-null collection properties.</summary>
    private static Task VerifyNullEntriesAsync() => VerifyRejectedMutationsAsync(
        root => root["checks"]!.AsArray().Add((JsonNode?)null),
        root => root["network"]!["connectionAttempts"]!.AsArray().Add((JsonNode?)null),
        root => root["server"]!["advertisedVoices"]!.AsArray().Add((JsonNode?)null),
        root => root["server"]!["observations"]![0]!["warnings"]!.AsArray().Add((JsonNode?)null),
        root => root["audio"]!["inspection"]!["warnings"]!.AsArray().Add((JsonNode?)null),
        root => root["checks"]![0]!["suggestions"]!.AsArray().Add((JsonNode?)null));

    /// <summary>Rejects unknown labels and numeric enum representations.</summary>
    private static Task VerifyEnumsAsync() => VerifyRejectedMutationsAsync(
        root => root["mode"] = "scan",
        root => root["mode"] = 1,
        root => root["mode"] = "1",
        root => root["checks"]![0]!["status"] = "successful",
        root => root["server"]!["readiness"] = "maybe");

    /// <summary>Checks decoded text bounds and rejects malformed UTF-8 bytes.</summary>
    private static async Task VerifyTextAsync()
    {
        var root = await CreateRootAsync();
        root["profile"]!["name"] = new string('x', TextLimit);
        _ = await ImportAsync(ToBytes(root));

        root["profile"]!["name"] = new string('x', TextLimit + 1);
        await ExpectRejectedAsync(ToBytes(root));

        var malformed = (await CanonicalJson.Value).ToArray();
        var position = malformed.AsSpan().IndexOf("Tsubaki Service Check"u8);
        Require(position >= 0, "The UTF-8 fixture could not locate its text value.");
        malformed[position] = 0xFF;
        await ExpectRejectedAsync(malformed);
    }

    /// <summary>Rejects oversized evidence lists with otherwise usable entries.</summary>
    private static Task VerifyCollectionsAsync() => VerifyRejectedMutationsAsync(
        root => AddSupportingChecks(root, 257),
        root => root["network"]!["resolvedAddresses"] = CreateStrings(33),
        root =>
        {
            var attempts = new JsonArray();
            var template = root["network"]!["connectionAttempts"]![0]!;

            for (var index = 0; index < 33; index++)
                attempts.Add(template.DeepClone());

            root["network"]!["connectionAttempts"] = attempts;
        },
        root => root["server"]!["advertisedVoices"] = CreateStrings(513),
        root => root["server"]!["observations"]![0]!["warnings"] = CreateWarnings(33),
        root => root["audio"]!["inspection"]!["warnings"] = CreateWarnings(33),
        root => root["checks"]![0]!["suggestions"] = CreateStrings(33));

    /// <summary>Rejects deeply nested data before treating it as a report model.</summary>
    private static async Task VerifyDepthAsync()
    {
        var text = Encoding.UTF8.GetString(await CanonicalJson.Value);
        var nested = new string('[', 40) + "0" + new string(']', 40);
        await ExpectRejectedAsync(Encoding.UTF8.GetBytes(
            text.Insert(1, "\"unexpected\":" + nested + ",")));
    }

    /// <summary>Normalizes explicit offsets in root and nested timestamps.</summary>
    private static async Task VerifyTimestampOffsetsAsync()
    {
        var root = await CreateRootAsync();
        root["startedAtUtc"] = "2026-11-10T02:00:00+02:00";
        root["finishedAtUtc"] = "2026-11-10T02:00:01+02:00";
        root["server"]!["readinessObservedAtUtc"] = "2026-11-10T02:00:00+02:00";
        root["server"]!["observations"]![0]!["observedAtUtc"] = "2026-11-10T02:00:00+02:00";
        var loaded = await ImportAsync(ToBytes(root));
        var expected = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var server = loaded.Server
            ?? throw new InvalidOperationException("Imported server evidence is missing.");

        Require(loaded.StartedAtUtc == expected && loaded.StartedAtUtc.Offset == TimeSpan.Zero
            && server.ReadinessObservedAtUtc == expected
            && server.Observations[0].ObservedAtUtc.Offset == TimeSpan.Zero,
            "Imported timestamps changed their instant or retained a local offset.");
    }

    /// <summary>Rejects invalid dates and timestamps without an explicit offset.</summary>
    private static Task VerifyTimestampsAsync() => VerifyRejectedMutationsAsync(
        root => root["startedAtUtc"] = "2026-11-10T00:00:00",
        root => root["startedAtUtc"] = "tomorrow",
        root => root["startedAtUtc"] = null,
        root => root["checks"]![0]!["observedAtUtc"] = "2026-11-10T00:00:00");

    /// <summary>Rejects duplicate IDs and known checks assigned the wrong role.</summary>
    private static Task VerifyCheckIdentitiesAsync() => VerifyRejectedMutationsAsync(
        root =>
        {
            root["checks"]!.AsArray().Add(root["checks"]![0]!.DeepClone());
            root["outcome"] = "incomplete";
        },
        root =>
        {
            FindCheck(root, "speech.request")["role"] = "supporting";
            root["outcome"] = "incomplete";
        });

    /// <summary>Rejects outcomes that contradict execution or required checks.</summary>
    private static Task VerifyOutcomesAsync() => VerifyRejectedMutationsAsync(
        root => root["outcome"] = "failed",
        root => root["executionState"] = "timed_out",
        root => root["checks"]!.AsArray().Remove(FindCheck(root, "audio.signal")));

    /// <summary>Preserves missing evidence instead of synthesizing pass records.</summary>
    private static async Task VerifyMissingChecksAsync()
    {
        foreach (var id in new[] { "audio.signal", "service.status" })
        {
            var root = await CreateRootAsync();
            root["checks"]!.AsArray().Remove(FindCheck(root, id));
            root["outcome"] = id == "audio.signal" ? "incomplete" : "passed";
            var loaded = await ImportAsync(ToBytes(root));

            Require(loaded.Checks.All(check => check.Id != id)
                && loaded.Outcome == (id == "audio.signal"
                    ? DiagnosticOutcome.Incomplete : DiagnosticOutcome.Passed),
                "Import fabricated a missing check or changed its coverage meaning.");
        }
    }

    /// <summary>Rejects passing claims whose HTTP, audio, or metadata evidence is absent.</summary>
    private static Task VerifyRequiredEvidenceAsync() => VerifyRejectedMutationsAsync(
        root => root["audio"] = null,
        root => FindCheck(root, "speech.request")["http"] = null,
        root => FindCheck(root, "speech.request")["http"]!["statusCode"] = 204,
        root => root["server"] = null,
        root =>
        {
            var inspection = root["audio"]!["inspection"]!;
            inspection["signalState"] = "silent";
            inspection["peakAbsolute"] = 0;
            inspection["rms"] = 0;
        });

    /// <summary>Rejects impossible frame counts, non-finite levels, and inconsistent duration.</summary>
    private static Task VerifyAudioAsync() => VerifyRejectedMutationsAsync(
        root => root["audio"]!["inspection"]!["durationSeconds"] = 1,
        root => root["audio"]!["inspection"]!["frameCount"] = 1,
        root => root["audio"]!["inspection"]!["dataBytes"] = 1,
        root => root["audio"]!["inspection"]!["rms"] = 2,
        root => root["audio"]!["inspection"]!["rms"] = JsonNode.Parse("1e999"),
        root => root["audio"]!["inspection"]!["bitsPerSample"] = 8,
        root => root["audio"]!["inspection"]!["payloadSha256"] = "invalid");

    /// <summary>Rejects invalid snapshots without resolving stored addresses.</summary>
    private static Task VerifySnapshotsAsync() => VerifyRejectedMutationsAsync(
        root => root["target"]!["id"] = "",
        root => root["profile"]!["fingerprint"] = "invalid",
        root => root["profile"]!["timeoutsMs"]!["dns"] = 0,
        root => root["profile"]!["limits"]!["maxAudioBytes"] = 0,
        root => root["request"]!["inputLength"] = 0,
        root => root["request"]!["route"] = "different/speech",
        root => root["request"]!["responseFormat"] = "mp3",
        root => root["request"]!["stream"] = true,
        root => root["request"]!["speed"] = 5);

    /// <summary>Preserves redacted text and accepts a retained sample that is not present.</summary>
    private static async Task VerifyReferencesAsync()
    {
        var root = await CreateRootAsync();
        root["target"]!["baseUrl"] = "[REDACTED]";
        root["profile"]!["routes"]!["speech"] = "[REDACTED]";
        root["request"]!["route"] = "[REDACTED]";
        root["profile"]!["name"] = "<script>fixture</script> R\u00E9sum\u00E9 fixture";
        root["audio"]!["retained"] = true;
        root["audio"]!["samplePath"] = "sample.wav";

        await WithInputAsync(ToBytes(root), async path =>
        {
            var loaded = await ReportLoader.LoadAsync(path);
            Require(loaded.Target.BaseUrl == "[REDACTED]"
                && loaded.Request?.Route == "[REDACTED]"
                && loaded.Profile.Name == "<script>fixture</script> R\u00E9sum\u00E9 fixture"
                && loaded.Audio?.Retained == true && loaded.AudioPayload is null
                && !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "sample.wav"))
                && Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1,
                "Import followed a stored reference or changed its retained data.");
        });
    }

    /// <summary>Rejects embedded runtime bytes and noncanonical audio paths.</summary>
    private static async Task VerifyForbiddenPayloadsAsync()
    {
        await VerifyRejectedMutationsAsync(
            root => root["audioPayload"] = "SGVsbG8=",
            root => root["audioPayload"] = null,
            root => root["audio"]!["samplePath"] = "sample.wav");

        foreach (var path in new[] { "../sample.wav", "C:\\sample.wav", "https://example.test/sample.wav" })
        {
            var root = await CreateRootAsync();
            root["audio"]!["retained"] = true;
            root["audio"]!["samplePath"] = path;
            await ExpectRejectedAsync(ToBytes(root));
        }
    }

    /// <summary>Honors cancellation before a missing path can be opened.</summary>
    private static async Task VerifyCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await WithInputAsync(await CanonicalJson.Value, path =>
            ExpectExceptionAsync<OperationCanceledException>(() =>
                ReportLoader.LoadAsync(path + ".missing", cancellation.Token)));
    }

    /// <summary>Reports an unavailable source without creating it.</summary>
    private static async Task VerifyMissingFileAsync()
    {
        await WithInputAsync(await CanonicalJson.Value, path =>
            ExpectExceptionAsync<FileNotFoundException>(() =>
                ReportLoader.LoadAsync(path + ".missing")));
    }

    /// <summary>Leaves source bytes intact and releases handles after success and failure.</summary>
    private static async Task VerifySourceFileAsync()
    {
        var bytes = await CanonicalJson.Value;

        await WithInputAsync(bytes, async path =>
        {
            _ = await ReportLoader.LoadAsync(path);
            var unchanged = await File.ReadAllBytesAsync(path);
            Require(unchanged.AsSpan().SequenceEqual(bytes), "Import changed its source report.");

            await File.WriteAllBytesAsync(path, "{"u8.ToArray());
            await ExpectExceptionAsync<JsonException>(() => ReportLoader.LoadAsync(path));
            File.Move(path, path + ".moved");
        });
    }

    /// <summary>Applies each invalid mutation to an independent valid report.</summary>
    private static async Task VerifyRejectedMutationsAsync(params Action<JsonObject>[] mutations)
    {
        foreach (var mutate in mutations)
        {
            var root = await CreateRootAsync();
            mutate(root);
            await ExpectRejectedAsync(ToBytes(root));
        }
    }

    /// <summary>Parses a fresh editable copy of the shared canonical fixture.</summary>
    private static async Task<JsonObject> CreateRootAsync()
    {
        return JsonNode.Parse(await CanonicalJson.Value)!.AsObject();
    }

    /// <summary>Encodes an edited JSON document without changing its value types.</summary>
    private static byte[] ToBytes(JsonObject root)
    {
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    /// <summary>Imports one fixture from a temporary path containing spaces.</summary>
    private static async Task<DiagnosticRunResult> ImportAsync(byte[] bytes)
    {
        DiagnosticRunResult? result = null;
        await WithInputAsync(bytes, async path =>
        {
            result = await ReportLoader.LoadAsync(path);
        });
        return result ?? throw new InvalidOperationException("Import did not return a result.");
    }

    /// <summary>Requires invalid report content to produce a JSON validation error.</summary>
    private static Task ExpectRejectedAsync(byte[] bytes)
    {
        return WithInputAsync(bytes,
            path => ExpectExceptionAsync<JsonException>(() => ReportLoader.LoadAsync(path)));
    }

    /// <summary>Creates and removes an isolated input directory.</summary>
    private static async Task WithInputAsync(byte[] bytes, Func<string, Task> verify)
    {
        var directory = Directory.CreateTempSubdirectory("TsubakiCheck-Import-").FullName;

        try
        {
            var path = Path.Combine(directory, "report with spaces.json");
            await File.WriteAllBytesAsync(path, bytes);
            await verify(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Requires the selected exception category from an asynchronous operation.</summary>
    private static async Task ExpectExceptionAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    /// <summary>Creates a valid report followed by JSON whitespace up to the selected size.</summary>
    private static async Task<byte[]> PaddedReportAsync(int length)
    {
        var source = await CanonicalJson.Value;
        Require(source.Length <= length, "The padded fixture is smaller than its report.");
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)' ');
        source.CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>Finds an editable check by its stable identifier.</summary>
    private static JsonObject FindCheck(JsonObject root, string id)
    {
        return root["checks"]!.AsArray().Select(node => node!.AsObject())
            .Single(check => check["id"]!.GetValue<string>() == id);
    }

    /// <summary>Builds independent string entries for collection-bound fixtures.</summary>
    private static JsonArray CreateStrings(int count)
    {
        var values = new JsonArray();

        for (var index = 0; index < count; index++)
            values.Add($"entry-{index}");

        return values;
    }

    /// <summary>Builds warning entries with valid nonblank codes and summaries.</summary>
    private static JsonArray CreateWarnings(int count)
    {
        var values = new JsonArray();

        for (var index = 0; index < count; index++)
            values.Add(new JsonObject { ["code"] = "fixture_warning", ["summary"] = "Fixture warning." });

        return values;
    }

    /// <summary>Extends a valid report with distinct passing supporting observations.</summary>
    private static void AddSupportingChecks(JsonObject root, int count)
    {
        var checks = root["checks"]!.AsArray();

        while (checks.Count < count)
        {
            checks.Add(new JsonObject
            {
                ["id"] = $"fixture.extra.{checks.Count}",
                ["role"] = "supporting",
                ["status"] = "passed",
                ["code"] = "fixture_passed",
                ["summary"] = "Fixture supporting observation.",
                ["observedAtUtc"] = "2026-11-10T00:00:00Z",
                ["durationMs"] = null,
                ["http"] = null,
                ["suggestions"] = new JsonArray()
            });
        }
    }

    /// <summary>Creates canonical JSON from the actual complete diagnostic pipeline.</summary>
    private static async Task<byte[]> CreateCanonicalJsonAsync()
    {
        var result = await CreateRunAsync();
        Require(result.Outcome == DiagnosticOutcome.Passed && result.AudioPayload is { Length: > 0 },
            "The canonical import fixture did not produce verified runtime audio.");
        return ReportJson.Serialize(result);
    }

    /// <summary>Runs controlled metadata and speech exchanges without native network access.</summary>
    private static async Task<DiagnosticRunResult> CreateRunAsync(
        DiagnosticMode mode = DiagnosticMode.Check,
        byte[]? payload = null,
        HttpStatusCode speechStatus = HttpStatusCode.OK)
    {
        using var handler = new ReportHandler(payload ?? CreateWav(), speechStatus);
        var profile = new TargetProfile
        {
            SchemaVersion = 1,
            Name = "Import fixture",
            Target = new ProfileTarget
            {
                Id = "import-fixture",
                BaseUrl = "http://fixture.example.test:5045/"
            },
            TimeoutsMs = new ProfileTimeouts { Metadata = 5000, Speech = 5000, Run = 10000 }
        };

        var pending = mode == DiagnosticMode.Inspect
            ? DiagnosticRunner.InspectAsync(profile, "0.1.0-fixture", handler,
                networkProbe: FixtureNetworkProbe.Connected)
            : DiagnosticRunner.CheckAsync(profile, "0.1.0-fixture", handler,
                networkProbe: FixtureNetworkProbe.Connected);

        return await pending.WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>Creates a short mono WAV with PCM16 silence, signal, or unsupported float format.</summary>
    private static byte[] CreateWav(bool silent = false, bool unsupported = false)
    {
        const int dataBytes = 64;
        var bytes = new byte[44 + dataBytes];
        var alignment = (ushort)(unsupported ? 4 : 2);
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), (ushort)(unsupported ? 3 : 1));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 8000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)(8000 * alignment));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), alignment);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), (ushort)(unsupported ? 32 : 16));
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), dataBytes);

        for (var offset = 0; offset < dataBytes; offset += 2)
        {
            var sample = (short)(silent ? 0 : offset % 4 == 0 ? 8192 : -8192);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + offset), sample);
        }

        return bytes;
    }

    /// <summary>Fails a regression check with an actionable fixture message.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    /// <summary>Supplies recognized metadata and one configurable speech response.</summary>
    private sealed class ReportHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        private readonly HttpStatusCode _speechStatus;

        /// <summary>Stores the controlled speech body and HTTP status.</summary>
        public ReportHandler(byte[] payload, HttpStatusCode speechStatus)
        {
            _payload = payload;
            _speechStatus = speechStatus;
        }

        /// <summary>Returns fixed route responses without opening a connection.</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(_speechStatus)
                {
                    Content = new ByteArrayContent(_speechStatus == HttpStatusCode.OK
                        ? _payload : "{\"error\":\"Fixture speech failure.\"}"u8.ToArray())
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(
                    _speechStatus == HttpStatusCode.OK ? "audio/wav" : "application/json");
                return Task.FromResult(response);
            }

            var json = request.RequestUri!.AbsolutePath.TrimStart('/') switch
            {
                "health" or "v1/health" =>
                    """{"service":"Tsubaki TTS Engine","version":"1.0.10","synthesisReady":true}""",
                "tsbk/server/status" =>
                    """{"service":"Tsubaki TTS Engine","version":"1.0.10","synthesisReady":true,"formats":{"wav":true},"availableVoices":["piper_base"]}""",
                "tsbk/audio/voices" => """{"voices":["piper_base"]}""",
                _ => throw new InvalidOperationException("An import fixture requested an unexpected route.")
            };

            var metadata = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json))
            };
            metadata.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(metadata);
        }
    }
}
