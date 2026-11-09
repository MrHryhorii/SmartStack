using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Audio;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Profiles;
using Tsubaki.ServiceCheck.Core.Reports;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>Verifies complete check and inspection runs with controlled HTTP exchanges.</summary>
internal static class DiagnosticModeChecks
{
    private const string ToolVersion = "0.1.0-fixture";

    /// <summary>Returns mode-specific diagnostic pipeline regression checks.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks()
    {
        return
        [
            ("Active run collects metadata before one speech POST", VerifyCheckAsync),
            ("Inspection exports metadata without speech or audio evidence", VerifyInspectAsync),
            ("Restricted metadata preserves successful active synthesis", VerifyRestrictedMetadataAsync),
            ("Successful audio preserves earlier unready claims", VerifyUnreadyCheckAsync),
            ("Unready inspection fails without requesting speech", VerifyUnreadyInspectAsync),
            ("Run interruption during metadata prevents speech in both modes", VerifyInterruptionAsync),
            ("Remote metadata text encoded in diagnostic HTML", VerifyMetadataEncodingAsync)
        ];
    }

    /// <summary>Verifies the complete active path with successful metadata and audio.</summary>
    private static async Task VerifyCheckAsync()
    {
        using var handler = new PipelineHandler();

        var result = await RunAsync(DiagnosticMode.Check, handler);

        Require(
            handler.Requests.SequenceEqual(new[]
            {
                "GET health",
                "GET tsbk/server/status",
                "GET tsbk/audio/voices",
                "POST v1/audio/speech"
            }),
            "The active run changed metadata order or repeated synthesis.");

        Require(
            result.Mode == DiagnosticMode.Check &&
            result.ExecutionState == DiagnosticExecutionState.Completed &&
            result.Outcome == DiagnosticOutcome.Passed &&
            result.Request is not null &&
            result.Server?.Readiness == DiagnosticReadiness.Ready &&
            result.Server.Observations.Count == 3 &&
            result.Audio?.Inspection.ContainerState == WavContainerState.Valid &&
            result.Audio.Inspection.SignalState == AudioSignalState.Nonzero &&
            result.AudioPayload is { Length: > 0 } &&
            result.Timings.SpeechTotalMs is not null,
            "The active result lost metadata, request, or verified audio evidence.");

        string[] required = ["speech.request", "audio.container", "audio.signal"];

        foreach (var id in required)
        {
            var check = FindCheck(result, id);

            Require(
                check.Role == DiagnosticCheckRole.Required &&
                check.Status == DiagnosticCheckStatus.Passed,
                "An active speech requirement did not pass.");
        }

        Require(
            FindCheck(result, "service.identity").Role ==
                DiagnosticCheckRole.Supporting &&
            FindCheck(result, "service.readiness").Role ==
                DiagnosticCheckRole.Supporting,
            "Active metadata conclusions became required checks.");

        RequireUniqueIds(result);
    }

    /// <summary>Verifies deliberate speech omission and the inspection export contract.</summary>
    private static async Task VerifyInspectAsync()
    {
        using var handler = new PipelineHandler();

        var result = await RunAsync(DiagnosticMode.Inspect, handler);

        Require(
            handler.Requests.SequenceEqual(new[]
            {
                "GET health",
                "GET tsbk/server/status",
                "GET tsbk/audio/voices"
            }) &&
            result.Mode == DiagnosticMode.Inspect &&
            result.ExecutionState == DiagnosticExecutionState.Completed &&
            result.Outcome == DiagnosticOutcome.Passed &&
            result.Request is null &&
            result.Audio is null &&
            result.AudioPayload is null &&
            result.Timings.SpeechHeadersMs is null &&
            result.Timings.SpeechBodyMs is null &&
            result.Timings.SpeechTotalMs is null,
            "Inspection requested synthesis or created unobserved speech evidence.");

        string[] required = ["service.identity", "service.readiness"];

        foreach (var id in required)
        {
            var check = FindCheck(result, id);

            Require(
                check.Role == DiagnosticCheckRole.Required &&
                check.Status == DiagnosticCheckStatus.Passed,
                "A recognized healthy inspection requirement did not pass.");
        }

        RequireSpeechOmitted(result, "not_requested");
        RequireUniqueIds(result);

        using var document = JsonDocument.Parse(ReportJson.Serialize(result));

        Require(
            document.RootElement.GetProperty("mode").GetString() == "inspect" &&
            document.RootElement.GetProperty("request").ValueKind ==
                JsonValueKind.Null &&
            document.RootElement.GetProperty("audio").ValueKind ==
                JsonValueKind.Null &&
            document.RootElement.GetProperty("server")
                .GetProperty("observations").GetArrayLength() == 3,
            "Canonical inspection JSON included a speech request or lost metadata.");

        var html = await RenderHtmlAsync(result);

        Require(
            html.Contains(
                "Speech generation was not tested.",
                StringComparison.Ordinal) &&
            !html.Contains("data:audio/wav", StringComparison.OrdinalIgnoreCase),
            "Inspection HTML implied audio generation or embedded unrequested audio.");
    }

    /// <summary>Preserves one successful speech request when every metadata route is denied.</summary>
    private static async Task VerifyRestrictedMetadataAsync()
    {
        using var handler = new PipelineHandler(_ => JsonResponse(
            HttpStatusCode.Forbidden,
            """{"error":"Metadata access denied."}"""));

        var result = await RunAsync(DiagnosticMode.Check, handler);

        Require(
            handler.Requests.Count == 4 &&
            handler.Requests.Count(request =>
                request == "POST v1/audio/speech") == 1 &&
            result.ExecutionState == DiagnosticExecutionState.Completed &&
            result.Outcome == DiagnosticOutcome.PassedWithWarnings &&
            result.Server?.Observations.Count == 3 &&
            result.Server.Readiness == DiagnosticReadiness.Unknown &&
            FindCheck(result, "service.health").Code == "http_forbidden" &&
            FindCheck(result, "speech.request").Status ==
                DiagnosticCheckStatus.Passed &&
            FindCheck(result, "audio.signal").Status ==
                DiagnosticCheckStatus.Passed,
            "Metadata restrictions suppressed or invalidated successful synthesis.");
    }

    /// <summary>Labels audio success after a server-reported unready state.</summary>
    private static async Task VerifyUnreadyCheckAsync()
    {
        using var handler = new PipelineHandler(route =>
            DefaultMetadata(route, ready: false));

        var result = await RunAsync(DiagnosticMode.Check, handler);

        Require(
            result.ExecutionState == DiagnosticExecutionState.Completed &&
            result.Outcome == DiagnosticOutcome.PassedWithWarnings &&
            result.Server?.Readiness == DiagnosticReadiness.NotReady &&
            handler.Requests.Count(request =>
                request == "POST v1/audio/speech") == 1 &&
            FindCheck(result, "service.readiness").Status ==
                DiagnosticCheckStatus.Failed &&
            FindCheck(result, "service.readiness").Role ==
                DiagnosticCheckRole.Supporting &&
            FindCheck(result, "service.readiness.speech_discrepancy").Code ==
                "speech_after_reported_unready" &&
            FindCheck(result, "audio.signal").Status ==
                DiagnosticCheckStatus.Passed,
            "Successful audio erased an earlier claim or was blocked by metadata.");
    }

    /// <summary>Requires reported readiness in inspection mode without attempting speech.</summary>
    private static async Task VerifyUnreadyInspectAsync()
    {
        using var handler = new PipelineHandler(route =>
            DefaultMetadata(route, ready: false));

        var result = await RunAsync(DiagnosticMode.Inspect, handler);

        Require(
            handler.Requests.Count == 3 &&
            handler.Requests.All(request =>
                request.StartsWith("GET ", StringComparison.Ordinal)) &&
            result.ExecutionState == DiagnosticExecutionState.Completed &&
            result.Outcome == DiagnosticOutcome.Failed &&
            result.Server?.Readiness == DiagnosticReadiness.NotReady &&
            result.Request is null &&
            result.Audio is null &&
            FindCheck(result, "service.readiness").Role ==
                DiagnosticCheckRole.Required &&
            FindCheck(result, "service.readiness").Status ==
                DiagnosticCheckStatus.Failed,
            "An unready inspection passed or attempted synthesis.");

        RequireSpeechOmitted(result, "not_requested");
    }

    /// <summary>Preserves partial metadata and prevents POST after interruption.</summary>
    private static async Task VerifyInterruptionAsync()
    {
        DiagnosticMode[] modes = [DiagnosticMode.Check, DiagnosticMode.Inspect];
        bool[] callerModes = [true, false];

        foreach (var mode in modes)
        {
            foreach (var callerCancellation in callerModes)
            {
                var profile = CreateProfile() with
                {
                    TimeoutsMs = new ProfileTimeouts
                    {
                        Metadata = 5000,
                        Speech = 5000,
                        Run = callerCancellation ? 10000 : 1000
                    }
                };

                using var stream = new StalledStream();
                using var handler = new PipelineHandler(route =>
                    route == "tsbk/server/status"
                        ? StreamResponse(stream)
                        : DefaultMetadata(route));

                using var cancellation = new CancellationTokenSource(
                    TimeSpan.FromSeconds(15));

                var pending = RunAsync(
                    mode,
                    handler,
                    profile,
                    cancellation.Token);

                try
                {
                    await stream.ReadStarted.Task.WaitAsync(
                        TimeSpan.FromSeconds(5));

                    if (callerCancellation)
                    {
                        cancellation.Cancel();
                    }

                    var result = await pending.WaitAsync(
                        TimeSpan.FromSeconds(5));

                    var expectedState = callerCancellation
                        ? DiagnosticExecutionState.Cancelled
                        : DiagnosticExecutionState.TimedOut;

                    var expectedCode = callerCancellation
                        ? "cancelled"
                        : "deadline_exceeded";

                    Require(
                        result.ExecutionState == expectedState &&
                        result.Outcome == DiagnosticOutcome.Incomplete &&
                        handler.Requests.SequenceEqual(new[]
                        {
                            "GET health",
                            "GET tsbk/server/status"
                        }) &&
                        stream.Disposed &&
                        result.Server?.Observations.Count == 2 &&
                        result.Server.Readiness == DiagnosticReadiness.Ready &&
                        result.Server.ReadinessSourceRoute == "health" &&
                        FindCheck(result, "service.status").Code == expectedCode &&
                        FindCheck(result, "service.voices").Code == expectedCode &&
                        result.Audio is null &&
                        result.AudioPayload is null &&
                        result.Timings.SpeechTotalMs is null,
                        "Interrupted diagnostics lost metadata or continued to speech.");

                    RequireSpeechOmitted(
                        result,
                        mode == DiagnosticMode.Inspect
                            ? "not_requested"
                            : expectedCode);

                    RequireUniqueIds(result);
                }
                finally
                {
                    cancellation.Cancel();
                    await pending.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
    }

    /// <summary>Encodes newly collected remote labels in complete diagnostic reports.</summary>
    private static async Task VerifyMetadataEncodingAsync()
    {
        const string hostile =
            "<script>alert(\"metadata\")</script><img src=\"https://example.test/pixel\">";

        using var handler = new PipelineHandler(route =>
            DefaultMetadata(route, version: hostile));

        var result = await RunAsync(DiagnosticMode.Inspect, handler);
        var html = await RenderHtmlAsync(result);

        Require(
            result.Server?.Version == hostile &&
            html.Contains(
                System.Text.Encodings.Web.HtmlEncoder.Default.Encode(hostile),
                StringComparison.Ordinal) &&
            !html.Contains("<script", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("<img", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("src=\"http", StringComparison.OrdinalIgnoreCase),
            "Remote metadata introduced executable or externally loaded HTML.");
    }

    /// <summary>Runs one public diagnostic mode with controlled network evidence.</summary>
    private static Task<DiagnosticRunResult> RunAsync(
        DiagnosticMode mode,
        PipelineHandler handler,
        TargetProfile? profile = null,
        CancellationToken cancellationToken = default)
    {
        var target = profile ?? CreateProfile();

        var pending = mode switch
        {
            DiagnosticMode.Check => DiagnosticRunner.CheckAsync(
                target,
                ToolVersion,
                handler,
                cancellationToken,
                FixtureNetworkProbe.Connected),

            DiagnosticMode.Inspect => DiagnosticRunner.InspectAsync(
                target,
                ToolVersion,
                handler,
                cancellationToken,
                FixtureNetworkProbe.Connected),

            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        return pending.WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>Requires explicit omitted checks with no invented HTTP evidence.</summary>
    private static void RequireSpeechOmitted(
        DiagnosticRunResult result,
        string code)
    {
        string[] ids = ["speech.request", "audio.container", "audio.signal"];

        foreach (var id in ids)
        {
            var check = FindCheck(result, id);

            Require(
                check.Status == DiagnosticCheckStatus.Skipped &&
                check.Code == code &&
                check.Http is null &&
                check.DurationMs is null &&
                check.Role == (result.Mode == DiagnosticMode.Inspect
                    ? DiagnosticCheckRole.Supporting
                    : DiagnosticCheckRole.Required),
                "An omitted speech or audio check has an incorrect role or evidence.");
        }
    }

    /// <summary>Requires one canonical entry for each stable check identifier.</summary>
    private static void RequireUniqueIds(DiagnosticRunResult result)
    {
        Require(
            result.Checks.Select(check => check.Id)
                .Distinct(StringComparer.Ordinal).Count() == result.Checks.Count,
            "The diagnostic pipeline emitted duplicate check identifiers.");
    }

    /// <summary>Finds one diagnostic observation by its stable identifier.</summary>
    private static DiagnosticCheckResult FindCheck(
        DiagnosticRunResult result,
        string id)
    {
        return result.Checks.Single(check => check.Id == id);
    }

    /// <summary>Renders a complete offline report into memory.</summary>
    private static async Task<string> RenderHtmlAsync(DiagnosticRunResult result)
    {
        using var output = new MemoryStream();
        await HtmlReportWriter.WriteAsync(output, result);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>Creates an independently configured target for the fake HTTP handler.</summary>
    private static TargetProfile CreateProfile()
    {
        return new TargetProfile
        {
            SchemaVersion = 1,
            Name = "Diagnostic mode fixture",
            Target = new ProfileTarget
            {
                Id = "mode-fixture",
                BaseUrl = "http://fixture.example.test:5045/proxy/"
            },
            TimeoutsMs = new ProfileTimeouts
            {
                Metadata = 5000,
                Speech = 5000,
                Run = 10000
            }
        };
    }

    /// <summary>Returns recognized metadata with configurable readiness and version.</summary>
    private static HttpResponseMessage DefaultMetadata(
        string route,
        bool ready = true,
        string version = "1.0.10")
    {
        var json = route switch
        {
            "health" or "v1/health" => JsonSerializer.Serialize(new
            {
                service = "Tsubaki TTS Engine",
                version,
                synthesisReady = ready
            }),

            "tsbk/server/status" => JsonSerializer.Serialize(new
            {
                service = "Tsubaki TTS Engine",
                version,
                synthesisReady = ready,
                formats = new { wav = true },
                availableVoices = new[] { "piper_base", "alloy" }
            }),

            "tsbk/audio/voices" =>
                """{"voices":["piper_base","alloy"]}""",

            _ => throw new InvalidOperationException(
                "The diagnostic mode requested an unexpected metadata route.")
        };

        return JsonResponse(HttpStatusCode.OK, json);
    }

    /// <summary>Creates controlled JSON content for metadata responses.</summary>
    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        string json)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json))
        };

        response.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json");

        return response;
    }

    /// <summary>Creates a pending metadata body for interruption checks.</summary>
    private static HttpResponseMessage StreamResponse(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        };

        response.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json");

        return response;
    }

    /// <summary>Builds a small valid mono PCM16 WAV with nonzero samples.</summary>
    private static byte[] CreateWav()
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        writer.Write("RIFF"u8);
        writer.Write(44);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(8000);
        writer.Write(16000);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(8);
        writer.Write((short)0);
        writer.Write((short)8192);
        writer.Write((short)-8192);
        writer.Write((short)0);
        writer.Flush();

        return output.ToArray();
    }

    /// <summary>Fails the current regression check when its condition is false.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>Records metadata GETs and returns one supported speech response.</summary>
    private sealed class PipelineHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _metadata;
        private readonly List<string> _requests = [];

        public IReadOnlyList<string> Requests => _requests;

        /// <summary>Stores the metadata response factory used by this fixture.</summary>
        public PipelineHandler(Func<string, HttpResponseMessage>? metadata = null)
        {
            _metadata = metadata ?? (route => DefaultMetadata(route));
        }

        /// <summary>Records one exchange and returns the matching controlled response.</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = request.RequestUri?.AbsolutePath
                ?? throw new InvalidOperationException(
                    "The diagnostic request URI is missing.");

            Require(
                path.StartsWith("/proxy/", StringComparison.Ordinal),
                "The diagnostic mode lost its configured proxy prefix.");

            var route = path["/proxy/".Length..];
            _requests.Add(request.Method.Method + " " + route);

            if (request.Method == HttpMethod.Get)
            {
                Require(
                    request.Content is null,
                    "A metadata GET unexpectedly included request content.");

                return Task.FromResult(_metadata(route));
            }

            Require(
                request.Method == HttpMethod.Post &&
                route == "v1/audio/speech" &&
                request.Content is not null,
                "The diagnostic mode attempted an unexpected HTTP operation.");

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(CreateWav())
            };

            response.Content.Headers.ContentType =
                new MediaTypeHeaderValue("audio/wav");

            return Task.FromResult(response);
        }
    }

    /// <summary>Waits for cancellation after response headers have been observed.</summary>
    private sealed class StalledStream : Stream
    {
        public bool Disposed { get; private set; }

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>Signals body reading and waits for the supplied cancellation token.</summary>
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        /// <summary>Rejects synchronous reads.</summary>
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        /// <summary>Rejects flushing a read-only fixture.</summary>
        public override void Flush() => throw new NotSupportedException();

        /// <summary>Rejects seeking.</summary>
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        /// <summary>Rejects length changes.</summary>
        public override void SetLength(long value) =>
            throw new NotSupportedException();

        /// <summary>Rejects writing.</summary>
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        /// <summary>Records disposal of the pending body stream.</summary>
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
