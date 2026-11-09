using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Http;
using Tsubaki.ServiceCheck.Core.Metadata;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>Verifies metadata collection and sourced diagnostic conclusions.</summary>
internal static class MetadataInspectionChecks
{
    private const string VoicesJson = """{"voices":["piper_base","alloy"]}""";

    private const string StatusJson = """
        {
          "service": "Tsubaki TTS Engine",
          "version": "1.0.10",
          "synthesisReady": true,
          "model": {
            "baseVoiceDialect": "en-us",
            "sampleRateHz": 22050
          },
          "formats": {
            "wav": true
          },
          "availableVoices": ["piper_base", "alloy"]
        }
        """;

    private static readonly DateTimeOffset ObservedAt = new(
        2026, 11, 9, 14, 0, 0, TimeSpan.Zero);

    /// <summary>Returns collection, aggregation, interruption, and redaction checks.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks()
    {
        return
        [
            ("Healthy metadata inspection uses three GET requests", VerifyHealthyAsync),
            ("Health alias fallback limited to HTTP 404 and 405", VerifyHealthFallbackAsync),
            ("Other health failures preserve later metadata without alias retry", VerifyNoHealthFallbackAsync),
            ("Latest valid readiness selected and disagreements preserved", VerifyReadinessAsync),
            ("Unknown readiness cannot erase an earlier valid claim", VerifyUnknownReadinessAsync),
            ("Active metadata conclusions remain supporting checks", VerifyActiveRolesAsync),
            ("Status voice discovery fallback labeled and sourced", VerifyVoiceFallbackAsync),
            ("Complete and incomplete voice claims produce distinct warnings", VerifyVoiceWarningsAsync),
            ("Identity and version disagreements remain explicit", VerifyDisagreementsAsync),
            ("Restricted and foreign metadata have distinct inspection outcomes", VerifyUnavailableAsync),
            ("Local metadata timeout permits later endpoint requests", VerifyStageTimeoutAsync),
            ("Metadata interruption preserves earlier claims and stops later GETs", VerifyInterruptionAsync),
            ("Metadata secrets redacted after raw voice comparison", VerifyRedactionAsync)
        ];
    }

    /// <summary>Verifies the complete metadata path without sending synthesis requests.</summary>
    private static async Task VerifyHealthyAsync()
    {
        var result = await InspectAsync(DefaultResponse);

        Require(
            result.ExecutionState == DiagnosticExecutionState.Completed &&
            result.Routes.SequenceEqual(new[]
            {
                "health",
                "tsbk/server/status",
                "tsbk/audio/voices"
            }),
            "Healthy metadata collection did not use the expected three GETs.");

        var server = result.Inspection.Server;

        Require(
            server.Service == "Tsubaki TTS Engine" &&
            server.Version == "1.0.10" &&
            server.Readiness == DiagnosticReadiness.Ready &&
            server.ReadinessSourceRoute is not null &&
            server.ReadinessObservedAtUtc is not null &&
            server.AdvertisedVoicesComplete &&
            server.AdvertisedVoices.SequenceEqual(new[] { "piper_base", "alloy" }) &&
            server.VoicesSourceRoute == "tsbk/audio/voices" &&
            server.StatusSourceRoute == "tsbk/server/status" &&
            server.StatusSnapshot?.SampleRateHz == 22050 &&
            server.Observations.Count == 3,
            "Healthy metadata lost normalized values or source information.");

        Require(
            FindCheck(result.Inspection, "service.identity").Role ==
                DiagnosticCheckRole.Required &&
            FindCheck(result.Inspection, "service.readiness").Role ==
                DiagnosticCheckRole.Required &&
            Outcome(result.Inspection, result.ExecutionState) ==
                DiagnosticOutcome.Passed,
            "A complete healthy inspection did not pass.");
    }

    /// <summary>Uses the health alias only after a known missing-route response.</summary>
    private static async Task VerifyHealthFallbackAsync()
    {
        HttpStatusCode[] statuses =
        [
            HttpStatusCode.NotFound,
            HttpStatusCode.MethodNotAllowed
        ];

        foreach (var status in statuses)
        {
            var result = await InspectAsync(route => route == "health"
                ? Response(status, """{"error":"Route unavailable."}""")
                : DefaultResponse(route));

            Require(
                result.Routes.SequenceEqual(new[]
                {
                    "health",
                    "v1/health",
                    "tsbk/server/status",
                    "tsbk/audio/voices"
                }) &&
                result.Inspection.Server.Observations.Count == 4,
                "Known health fallback did not preserve the bounded request sequence.");

            var primary = result.Inspection.Server.Observations.Single(
                observation => observation.Endpoint == MetadataEndpoint.Health);

            var fallback = result.Inspection.Server.Observations.Single(
                observation => observation.Endpoint == MetadataEndpoint.HealthFallback);

            var health = FindCheck(result.Inspection, "service.health");

            Require(
                primary.Http.StatusCode == (int)status &&
                primary.State == MetadataDocumentState.Unavailable &&
                fallback.State == MetadataDocumentState.Recognized &&
                health.Status == DiagnosticCheckStatus.Passed &&
                health.Http?.Route == "v1/health" &&
                health.DurationMs is >= 0 &&
                Outcome(result.Inspection, result.ExecutionState) ==
                    DiagnosticOutcome.Passed,
                "The alias hid the primary response or lost successful health coverage.");
        }
    }

    /// <summary>Preserves later metadata after restricted, busy, or unrecognized health.</summary>
    private static async Task VerifyNoHealthFallbackAsync()
    {
        (HttpStatusCode Status, string Body)[] cases =
        [
            (HttpStatusCode.Unauthorized, """{"error":"Denied."}"""),
            (HttpStatusCode.Forbidden, """{"error":"Denied."}"""),
            (HttpStatusCode.TooManyRequests, """{"error":"Busy."}"""),
            (HttpStatusCode.TemporaryRedirect, """{"error":"Redirected."}"""),
            (HttpStatusCode.ServiceUnavailable, """{"error":"Unavailable."}"""),
            (HttpStatusCode.OK, "<html><body>Login required.</body></html>"),
            (HttpStatusCode.OK, """{"status":"ready"}""")
        ];

        foreach (var (status, body) in cases)
        {
            var result = await InspectAsync(route => route == "health"
                ? Response(status, body)
                : DefaultResponse(route));

            Require(
                result.Routes.Count == 3 &&
                !result.Routes.Contains("v1/health", StringComparer.Ordinal) &&
                result.ExecutionState == DiagnosticExecutionState.Completed &&
                result.Inspection.Server.Readiness == DiagnosticReadiness.Ready &&
                FindCheck(result.Inspection, "service.health").Status ==
                    DiagnosticCheckStatus.Failed &&
                Outcome(result.Inspection, result.ExecutionState) ==
                    DiagnosticOutcome.PassedWithWarnings,
                "A health restriction or content failure retried or blocked later metadata.");
        }
    }

    /// <summary>Selects readiness by client observation time and retains earlier claims.</summary>
    private static Task VerifyReadinessAsync()
    {
        bool[] latestValues = [true, false];

        foreach (var latestReady in latestValues)
        {
            var health = Observe(
                MetadataEndpoint.Health,
                ServiceJson(!latestReady),
                milliseconds: 10);

            var status = Observe(
                MetadataEndpoint.Status,
                ServiceJson(latestReady),
                milliseconds: 20);

            var result = Build(
                DiagnosticMode.Inspect,
                [status, health]);

            Require(
                result.Server.Readiness == (latestReady
                    ? DiagnosticReadiness.Ready
                    : DiagnosticReadiness.NotReady) &&
                result.Server.ReadinessSourceRoute == "tsbk/server/status" &&
                result.Server.ReadinessObservedAtUtc == status.Observation.ObservedAtUtc &&
                result.Server.Observations.Count == 2 &&
                result.Server.Observations.Any(observation =>
                    observation.SynthesisReady == true) &&
                result.Server.Observations.Any(observation =>
                    observation.SynthesisReady == false) &&
                FindCheck(result, "service.readiness.disagreement").Status ==
                    DiagnosticCheckStatus.Warning,
                "Readiness selection used collection order or erased conflicting claims.");

            Require(
                Outcome(result, DiagnosticExecutionState.Completed) ==
                    (latestReady
                        ? DiagnosticOutcome.PassedWithWarnings
                        : DiagnosticOutcome.Failed),
                "The selected readiness claim did not control the inspection outcome.");
        }

        return Task.CompletedTask;
    }

    /// <summary>Preserves a valid earlier claim when later readiness is unavailable.</summary>
    private static Task VerifyUnknownReadinessAsync()
    {
        string[] laterBodies =
        [
            """{"service":"Tsubaki TTS Engine"}""",
            """{"service":"Tsubaki TTS Engine","synthesisReady":null}""",
            """{"service":"Tsubaki TTS Engine","synthesisReady":"true"}"""
        ];

        foreach (var body in laterBodies)
        {
            var result = Build(
                DiagnosticMode.Inspect,
                [
                    Observe(MetadataEndpoint.Health, ServiceJson(true), 10),
                    Observe(MetadataEndpoint.Status, body, 20)
                ]);

            Require(
                result.Server.Readiness == DiagnosticReadiness.Ready &&
                result.Server.ReadinessSourceRoute == "health" &&
                !result.Checks.Any(check =>
                    check.Id == "service.readiness.disagreement"),
                "Unknown later readiness erased or contradicted a valid earlier claim.");
        }

        return Task.CompletedTask;
    }

    /// <summary>Prevents server metadata from becoming a required active speech check.</summary>
    private static Task VerifyActiveRolesAsync()
    {
        var result = Build(
            DiagnosticMode.Check,
            [
                Observe(MetadataEndpoint.Health, ServiceJson(false), 10),
                Observe(MetadataEndpoint.Status, ServiceJson(false), 20),
                Observe(MetadataEndpoint.Voices, VoicesJson, 30)
            ],
            requestedVoice: "piper_base");

        Require(
            result.Server.Readiness == DiagnosticReadiness.NotReady &&
            FindCheck(result, "service.readiness").Status ==
                DiagnosticCheckStatus.Failed &&
            result.Checks.All(check =>
                check.Role == DiagnosticCheckRole.Supporting),
            "Metadata conclusions became required in active mode.");

        var checks = result.Checks.Concat(PassedSpeechChecks()).ToArray();

        Require(
            DiagnosticOutcomeRules.Evaluate(
                DiagnosticMode.Check,
                DiagnosticExecutionState.Completed,
                checks) == DiagnosticOutcome.PassedWithWarnings,
            "A server readiness claim overrode successful required speech evidence.");

        return Task.CompletedTask;
    }

    /// <summary>Uses complete status discovery when dedicated discovery is unavailable.</summary>
    private static async Task VerifyVoiceFallbackAsync()
    {
        (HttpStatusCode Status, string Body)[] cases =
        [
            (HttpStatusCode.Forbidden, """{"error":"Voice discovery denied."}"""),
            (HttpStatusCode.OK, """{"data":["alloy"]}"""),
            (HttpStatusCode.OK, """{"voices":["alloy",42]}""")
        ];

        foreach (var (status, body) in cases)
        {
            var result = await InspectAsync(
                route => route == "tsbk/audio/voices"
                    ? Response(status, body)
                    : DefaultResponse(route),
                DiagnosticMode.Check,
                requestedVoice: "piper_base");

            var server = result.Inspection.Server;
            var voices = FindCheck(result.Inspection, "service.voices");

            Require(
                result.Routes.Count == 3 &&
                server.VoicesSourceRoute == "tsbk/server/status" &&
                server.AdvertisedVoicesComplete &&
                server.AdvertisedVoices.SequenceEqual(
                    new[] { "piper_base", "alloy" }) &&
                voices.Status == DiagnosticCheckStatus.Warning &&
                voices.Code == "voices_status_fallback" &&
                voices.Http?.StatusCode == (int)status &&
                server.Observations.Any(observation =>
                    observation.Endpoint == MetadataEndpoint.Voices &&
                    observation.Http.StatusCode == (int)status) &&
                !result.Inspection.Checks.Any(check =>
                    check.Code == "voice_not_advertised"),
                "Status discovery fallback lost its source or the dedicated response.");
        }
    }

    /// <summary>Distinguishes a complete exclusion from incomplete discovery coverage.</summary>
    private static async Task VerifyVoiceWarningsAsync()
    {
        var statusBody = JsonSerializer.Serialize(new
        {
            service = "Tsubaki TTS Engine",
            version = "1.0.10",
            synthesisReady = true,
            availableVoices = new[] { "piper_base", "alloy" },
            formats = new { wav = false }
        });

        var complete = await InspectAsync(
            route => route switch
            {
                "tsbk/server/status" => Response(HttpStatusCode.OK, statusBody),
                "tsbk/audio/voices" => Response(
                    HttpStatusCode.OK,
                    """{"voices":["alloy"]}"""),
                _ => DefaultResponse(route)
            },
            DiagnosticMode.Check,
            requestedVoice: "piper_base");

        Require(
            complete.Inspection.Server.AdvertisedVoicesComplete &&
            complete.Inspection.Server.VoicesSourceRoute == "tsbk/audio/voices" &&
            complete.Inspection.Server.AdvertisedVoices.SequenceEqual(
                new[] { "alloy" }) &&
            FindCheck(complete.Inspection, "speech.voice_advertisement").Code ==
                "voice_not_advertised" &&
            FindCheck(complete.Inspection, "service.voices.disagreement").Code ==
                "voice_list_disagreement" &&
            FindCheck(complete.Inspection, "service.wav_advertisement").Code ==
                "wav_not_advertised",
            "Complete conflicting advertisements were merged or their warnings were lost.");

        var partial = await InspectAsync(
            route => route switch
            {
                "tsbk/server/status" => Response(
                    HttpStatusCode.OK,
                    ServiceJson(true)),
                "tsbk/audio/voices" => Response(
                    HttpStatusCode.OK,
                    """{"voices":["alloy",null]}"""),
                _ => DefaultResponse(route)
            },
            DiagnosticMode.Check,
            requestedVoice: "piper_base");

        Require(
            !partial.Inspection.Server.AdvertisedVoicesComplete &&
            partial.Inspection.Server.AdvertisedVoices.SequenceEqual(
                new[] { "alloy" }) &&
            FindCheck(partial.Inspection, "service.voices").Code ==
                "voices_incomplete" &&
            !partial.Inspection.Checks.Any(check =>
                check.Code == "voice_not_advertised"),
            "Incomplete discovery was used to claim a configured voice was absent.");
    }

    /// <summary>Preserves identity conflicts and selects the latest valid version claim.</summary>
    private static Task VerifyDisagreementsAsync()
    {
        var versions = Build(
            DiagnosticMode.Inspect,
            [
                Observe(MetadataEndpoint.Health, ServiceJson(true, "1.0.10"), 10),
                Observe(MetadataEndpoint.Status, ServiceJson(true, "2.0.0"), 20)
            ]);

        Require(
            versions.Server.Version == "2.0.0" &&
            versions.Server.VersionSourceRoute == "tsbk/server/status" &&
            FindCheck(versions, "service.version.disagreement").Status ==
                DiagnosticCheckStatus.Warning,
            "Version disagreements were erased or selected from the wrong source.");

        var identities = Build(
            DiagnosticMode.Inspect,
            [
                Observe(MetadataEndpoint.Health, ServiceJson(true), 10),
                Observe(
                    MetadataEndpoint.Status,
                    ServiceJson(true, "9.9.9", "Other service"),
                    20)
            ]);

        Require(
            identities.Server.Service == "Tsubaki TTS Engine" &&
            identities.Server.IdentitySourceRoute == "health" &&
            identities.Server.Version == "1.0.10" &&
            FindCheck(identities, "service.identity").Status ==
                DiagnosticCheckStatus.Passed &&
            FindCheck(identities, "service.identity.disagreement").Status ==
                DiagnosticCheckStatus.Warning &&
            FindCheck(identities, "service.status").Status ==
                DiagnosticCheckStatus.Failed,
            "A foreign metadata response replaced recognized Tsubaki claims.");

        return Task.CompletedTask;
    }

    /// <summary>Distinguishes unknown identity caused by access policy from a foreign service.</summary>
    private static async Task VerifyUnavailableAsync()
    {
        var restricted = await InspectAsync(_ => Response(
            HttpStatusCode.Forbidden,
            """{"error":"Metadata restricted."}"""));

        Require(
            restricted.Routes.Count == 3 &&
            restricted.Inspection.Server.Readiness == DiagnosticReadiness.Unknown &&
            FindCheck(restricted.Inspection, "service.identity").Status ==
                DiagnosticCheckStatus.Error &&
            FindCheck(restricted.Inspection, "service.readiness").Status ==
                DiagnosticCheckStatus.Error &&
            Outcome(restricted.Inspection, restricted.ExecutionState) ==
                DiagnosticOutcome.Incomplete,
            "Restricted metadata was presented as a known foreign or failed service.");

        var foreign = await InspectAsync(route => route == "tsbk/audio/voices"
            ? DefaultResponse(route)
            : Response(
                HttpStatusCode.OK,
                ServiceJson(true, "1.0.0", "Other service")));

        Require(
            FindCheck(foreign.Inspection, "service.identity").Status ==
                DiagnosticCheckStatus.Failed &&
            FindCheck(foreign.Inspection, "service.identity").Code ==
                "service_identity_mismatch" &&
            foreign.Inspection.Server.Readiness == DiagnosticReadiness.Unknown &&
            Outcome(foreign.Inspection, foreign.ExecutionState) ==
                DiagnosticOutcome.Failed,
            "An explicit foreign service identity became a passing or unknown identity.");
    }

    /// <summary>Continues independent endpoint requests after a local body timeout.</summary>
    private static async Task VerifyStageTimeoutAsync()
    {
        var profile = CreateProfile() with
        {
            TimeoutsMs = new ProfileTimeouts
            {
                Metadata = 250,
                Run = 10000
            }
        };

        using var stream = new StalledStream();

        var result = await InspectAsync(
            route => route == "health"
                ? StreamResponse(stream)
                : DefaultResponse(route),
            profile: profile);

        Require(
            result.ExecutionState == DiagnosticExecutionState.Completed &&
            result.Routes.Count == 3 &&
            stream.Disposed &&
            result.Inspection.Server.Readiness == DiagnosticReadiness.Ready &&
            FindCheck(result.Inspection, "service.health").Code ==
                "metadata_timeout" &&
            Outcome(result.Inspection, result.ExecutionState) ==
                DiagnosticOutcome.PassedWithWarnings,
            "A local metadata deadline stopped independent endpoint requests.");
    }

    /// <summary>Preserves earlier observations when caller or shared run cancellation interrupts.</summary>
    private static async Task VerifyInterruptionAsync()
    {
        bool[] callerModes = [true, false];

        foreach (var callerCancellation in callerModes)
        {
            var expectedState = callerCancellation
                ? DiagnosticExecutionState.Cancelled
                : DiagnosticExecutionState.TimedOut;

            using var stream = new StalledStream();
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromSeconds(15));

            using var handler = new RecordingHandler(route =>
                route == "tsbk/server/status"
                    ? StreamResponse(stream)
                    : DefaultResponse(route));

            using var client = new TsubakiClient(CreateProfile(), handler);

            var pending = MetadataInspector.InspectAsync(
                client,
                DiagnosticMode.Inspect,
                null,
                callerCancellation ? cancellation.Token : default,
                callerCancellation ? default : cancellation.Token);

            try
            {
                await stream.ReadStarted.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));

                cancellation.Cancel();

                var result = await pending.WaitAsync(
                    TimeSpan.FromSeconds(5));

                var expectedCode = callerCancellation
                    ? "cancelled"
                    : "deadline_exceeded";

                Require(
                    result.ExecutionState == expectedState &&
                    handler.Routes.SequenceEqual(
                        new[] { "health", "tsbk/server/status" }) &&
                    stream.Disposed &&
                    result.Inspection.Server.Observations.Count == 2 &&
                    result.Inspection.Server.Readiness == DiagnosticReadiness.Ready &&
                    result.Inspection.Server.ReadinessSourceRoute == "health" &&
                    FindCheck(result.Inspection, "service.status").Code == expectedCode &&
                    FindCheck(result.Inspection, "service.voices").Status ==
                        DiagnosticCheckStatus.Skipped &&
                    FindCheck(result.Inspection, "service.voices").Code == expectedCode &&
                    Outcome(result.Inspection, result.ExecutionState) ==
                        DiagnosticOutcome.Incomplete,
                    "Interrupted collection lost earlier claims or sent later metadata GETs.");
            }
            finally
            {
                cancellation.Cancel();
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
            }

            using var preCancelled = new CancellationTokenSource();
            preCancelled.Cancel();

            using var unusedHandler = new RecordingHandler(DefaultResponse);
            using var unusedClient = new TsubakiClient(
                CreateProfile(),
                unusedHandler);

            var unused = await MetadataInspector.InspectAsync(
                unusedClient,
                DiagnosticMode.Inspect,
                null,
                callerCancellation ? preCancelled.Token : default,
                callerCancellation ? default : preCancelled.Token);

            Require(
                unused.ExecutionState == expectedState &&
                unusedHandler.Routes.Count == 0 &&
                unused.Inspection.Server.Observations.Count == 0 &&
                Outcome(unused.Inspection, unused.ExecutionState) ==
                    DiagnosticOutcome.Incomplete,
                "Pre-cancelled collection reached the HTTP handler.");
        }
    }

    /// <summary>Verifies that redaction follows comparisons of original identifiers.</summary>
    private static Task VerifyRedactionAsync()
    {
        const string token = "piper_base";

        var health = Observe(
            MetadataEndpoint.Health,
            JsonSerializer.Serialize(new
            {
                service = "Tsubaki TTS Engine",
                version = token,
                status = token,
                synthesisReady = true
            }),
            10);

        var status = Observe(
            MetadataEndpoint.Status,
            JsonSerializer.Serialize(new
            {
                service = "Tsubaki TTS Engine",
                version = token,
                status = token,
                synthesisReady = true,
                model = new { baseVoiceDialect = token },
                availableVoices = new[] { token }
            }),
            20);

        var voices = Observe(
            MetadataEndpoint.Voices,
            JsonSerializer.Serialize(new { voices = new[] { token } }),
            30);

        var sources = new[] { health, status, voices }
            .Select(source => (
                Observation: source.Observation with
                {
                    Summary = "Fixture " + token,
                    Http = source.Observation.Http with
                    {
                        Route = source.Observation.Http.Route + "/" + token,
                        MediaType = "application/" + token + "+json",
                        ErrorExcerpt = "Fixture " + token,
                        ExceptionType = "Fixture_" + token,
                        RedirectLocation = "https://redirect.example.test/" + token
                    },
                    Warnings = [new MetadataWarning("fixture_warning", token)]
                },
                source.DurationMs))
            .ToArray();

        var result = MetadataEvidenceBuilder.Build(
            DiagnosticMode.Check,
            sources,
            token,
            text => text.Replace(token, "***", StringComparison.Ordinal));

        var json = JsonSerializer.Serialize(result);

        Require(
            !json.Contains(token, StringComparison.Ordinal) &&
            result.Server.Version == "***" &&
            result.Server.StatusSnapshot?.BaseVoiceDialect == "***" &&
            result.Server.AdvertisedVoices.SequenceEqual(new[] { "***" }) &&
            !result.Checks.Any(check =>
                check.Code == "voice_not_advertised") &&
            result.Server.Observations.All(observation =>
                observation.Http.Route.Contains("***", StringComparison.Ordinal)),
            "Redaction leaked captured text or changed the raw configured-voice comparison.");

        return Task.CompletedTask;
    }

    /// <summary>Runs metadata collection with controlled GET responses and an overall deadline.</summary>
    private static async Task<(
        MetadataInspectionResult Inspection,
        DiagnosticExecutionState ExecutionState,
        IReadOnlyList<string> Routes)> InspectAsync(
        Func<string, HttpResponseMessage> response,
        DiagnosticMode mode = DiagnosticMode.Inspect,
        string? requestedVoice = null,
        TargetProfile? profile = null)
    {
        using var handler = new RecordingHandler(response);
        using var client = new TsubakiClient(
            profile ?? CreateProfile(),
            handler);

        using var runDeadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));

        var result = await MetadataInspector.InspectAsync(
            client,
            mode,
            requestedVoice,
            CancellationToken.None,
            runDeadline.Token)
            .WaitAsync(TimeSpan.FromSeconds(15));

        return (
            result.Inspection,
            result.ExecutionState,
            handler.Routes.ToArray());
    }

    /// <summary>Builds conclusions from controlled observation timestamps.</summary>
    private static MetadataInspectionResult Build(
        DiagnosticMode mode,
        IReadOnlyList<(MetadataObservation Observation, long DurationMs)> observations,
        string? requestedVoice = null)
    {
        return MetadataEvidenceBuilder.Build(
            mode,
            observations,
            requestedVoice,
            text => text);
    }

    /// <summary>Creates one parsed observation with a deterministic client timestamp.</summary>
    private static (MetadataObservation Observation, long DurationMs) Observe(
        MetadataEndpoint endpoint,
        string json,
        int milliseconds)
    {
        var payload = Encoding.UTF8.GetBytes(json);

        var route = endpoint switch
        {
            MetadataEndpoint.Health => "health",
            MetadataEndpoint.HealthFallback => "v1/health",
            MetadataEndpoint.Status => "tsbk/server/status",
            MetadataEndpoint.Voices => "tsbk/audio/voices",
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
        };

        var response = new MetadataRequestResult
        {
            Endpoint = endpoint,
            State = MetadataRequestState.Completed,
            Code = "metadata_response_received",
            Summary = "Metadata response received.",
            Route = route,
            RequestUri = new Uri("https://tts.example.test/proxy/" + route),
            StartedAtUtc = ObservedAt.AddMilliseconds(milliseconds - 10),
            FinishedAtUtc = ObservedAt.AddMilliseconds(milliseconds),
            TotalDurationMs = 10,
            StatusCode = 200,
            MediaType = "application/json",
            DeclaredContentLength = payload.Length,
            BodyBytesRead = payload.Length,
            BodyComplete = true,
            Payload = payload
        };

        return (MetadataParser.Parse(response), 10);
    }

    /// <summary>Creates a recognized service claim for prepared responses.</summary>
    private static string ServiceJson(
        bool ready,
        string version = "1.0.10",
        string service = "Tsubaki TTS Engine")
    {
        return JsonSerializer.Serialize(new
        {
            service,
            version,
            synthesisReady = ready
        });
    }

    /// <summary>Returns the known health, status, and voice response shapes.</summary>
    private static HttpResponseMessage DefaultResponse(string route)
    {
        return route switch
        {
            "health" or "v1/health" =>
                Response(HttpStatusCode.OK, ServiceJson(true)),
            "tsbk/server/status" =>
                Response(HttpStatusCode.OK, StatusJson),
            "tsbk/audio/voices" =>
                Response(HttpStatusCode.OK, VoicesJson),
            _ => throw new InvalidOperationException(
                "The collector requested an unexpected route.")
        };
    }

    /// <summary>Creates one JSON response with controlled bytes.</summary>
    private static HttpResponseMessage Response(
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

    /// <summary>Creates a response whose body waits for cancellation.</summary>
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

    /// <summary>Creates required fixture evidence for testing active outcome roles.</summary>
    private static IEnumerable<DiagnosticCheckResult> PassedSpeechChecks()
    {
        string[] ids = ["speech.request", "audio.container", "audio.signal"];

        return ids.Select(id => new DiagnosticCheckResult
        {
            Id = id,
            Role = DiagnosticCheckRole.Required,
            Status = DiagnosticCheckStatus.Passed,
            Code = "fixture_pass",
            Summary = "Required fixture evidence passed.",
            ObservedAtUtc = ObservedAt
        });
    }

    /// <summary>Evaluates the metadata checks using inspection outcome rules.</summary>
    private static DiagnosticOutcome Outcome(
        MetadataInspectionResult result,
        DiagnosticExecutionState executionState)
    {
        return DiagnosticOutcomeRules.Evaluate(
            DiagnosticMode.Inspect,
            executionState,
            result.Checks);
    }

    /// <summary>Finds one stable metadata check identifier.</summary>
    private static DiagnosticCheckResult FindCheck(
        MetadataInspectionResult result,
        string id)
    {
        return result.Checks.Single(check => check.Id == id);
    }

    /// <summary>Creates a profile for controlled metadata collection.</summary>
    private static TargetProfile CreateProfile()
    {
        return new TargetProfile
        {
            SchemaVersion = 1,
            Name = "Metadata inspection fixture",
            Target = new ProfileTarget
            {
                Id = "metadata-inspection",
                BaseUrl = "https://tts.example.test/proxy/"
            },
            TimeoutsMs = new ProfileTimeouts
            {
                Metadata = 5000,
                Run = 10000
            }
        };
    }

    /// <summary>Fails the current regression check when its condition is false.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>Records GET routes and dispatches controlled response bodies.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _response;
        private readonly List<string> _routes = [];

        public IReadOnlyList<string> Routes => _routes;

        /// <summary>Stores the response factory used by this handler.</summary>
        public RecordingHandler(Func<string, HttpResponseMessage> response)
        {
            _response = response;
        }

        /// <summary>Rejects synthesis requests and records each metadata GET.</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Require(
                request.Method == HttpMethod.Get && request.Content is null,
                "Metadata collection attempted a request other than a bodyless GET.");

            var path = request.RequestUri?.AbsolutePath
                ?? throw new InvalidOperationException(
                    "The metadata request URI is missing.");

            Require(
                path.StartsWith("/proxy/", StringComparison.Ordinal),
                "The configured proxy prefix was lost.");

            var route = path["/proxy/".Length..];
            _routes.Add(route);

            return Task.FromResult(_response(route));
        }
    }

    /// <summary>Provides a response body that remains pending until cancellation.</summary>
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

        /// <summary>Signals the read and waits for its cancellation token.</summary>
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

        /// <summary>Rejects flushing a read-only stream.</summary>
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

        /// <summary>Records disposal of the pending response body.</summary>
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
