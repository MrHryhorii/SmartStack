using System.Text;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Http;
using Tsubaki.ServiceCheck.Core.Metadata;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>Verifies typed metadata parsing without external HTTP access.</summary>
internal static class MetadataParserChecks
{
    private const string HealthJson = """
        {
          "status": "ok",
          "service": "Tsubaki TTS Engine",
          "version": "1.0.10",
          "synthesisReady": true,
          "timestamp": "2026-11-09T01:00:00+02:00"
        }
        """;

    private const string StatusJson = """
        {
          "service": "Tsubaki TTS Engine",
          "version": "1.0.10",
          "status": "ready",
          "synthesisReady": true,
          "model": {
            "baseVoiceDialect": "en-us",
            "sampleRateHz": 22050
          },
          "voiceCloning": {
            "enabled": true,
            "available": false
          },
          "formats": {
            "wav": true,
            "mp3": true
          },
          "dsp": {
            "defaultSampleRate": 0,
            "defaultPitch": 1,
            "defaultVolume": 1,
            "volumeBoosterDb": 8,
            "defaultTotalGainDb": 8,
            "privateConfiguration": "metadata-private-marker"
          },
          "limits": {
            "maxTextLength": null,
            "rateLimit": {
              "permitLimit": 20,
              "windowSeconds": 10,
              "queueLimit": 5
            }
          },
          "availableVoices": ["piper_base", "alloy"],
          "effects": {
            "privateConfiguration": "metadata-private-marker"
          },
          "privateConfiguration": "metadata-private-marker"
        }
        """;

    /// <summary>Returns parser regression checks for recognized and hostile responses.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks()
    {
        return
        [
            ("Health identity, readiness, and timestamp sources verified",
                () => Run(VerifyHealth)),
            ("Missing readiness remains unknown",
                () => Run(VerifyUnknownReadiness)),
            ("Status allowlist preserves capabilities and defaults",
                () => Run(VerifyStatus)),
            ("Malformed optional metadata preserves valid observations",
                () => Run(VerifyOptionalFields)),
            ("Advertised voices do not establish service identity",
                () => Run(VerifyVoices)),
            ("Empty, absent, and partial voice coverage distinguished",
                () => Run(VerifyVoiceCoverage)),
            ("Retained metadata voice IDs bounded",
                () => Run(VerifyVoiceLimit)),
            ("Selected metadata text bounded and controls rejected",
                () => Run(VerifyTextBounds)),
            ("Unrecognized JSON cannot establish Tsubaki readiness",
                () => Run(VerifyUnexpectedContent)),
            ("Malformed and duplicate metadata JSON rejected",
                () => Run(VerifyMalformedJson)),
            ("Metadata UTF-8 BOM accepted and invalid UTF-8 rejected",
                () => Run(VerifyUtf8)),
            ("Metadata document size, depth, and property limits enforced",
                () => Run(VerifyStructureLimits)),
            ("Unavailable metadata preserves HTTP evidence",
                () => Run(VerifyUnavailable)),
            ("Metadata media-type discrepancies remain warnings",
                () => Run(VerifyMediaTypes)),
            ("Normalized metadata omits unselected server fields",
                () => Run(VerifySelectedExport)),
            ("Metadata parsing honors caller cancellation",
                () => Run(VerifyCancellation))
        ];
    }

    /// <summary>Preserves independent client and server timestamps.</summary>
    private static void VerifyHealth()
    {
        var finished = new DateTimeOffset(
            2026, 11, 9, 16, 0, 1, TimeSpan.FromHours(2));

        var response = CreateResponse(HealthJson) with
        {
            FinishedAtUtc = finished
        };

        var result = MetadataParser.Parse(response);

        Require(
            result.State == MetadataDocumentState.Recognized &&
            result.ServiceIdentityMatched &&
            result.Service == "Tsubaki TTS Engine" &&
            result.Version == "1.0.10" &&
            result.Status == "ok" &&
            result.SynthesisReady == true,
            "Recognized health fields were not preserved.");

        Require(
            result.Endpoint == MetadataEndpoint.Health &&
            result.Http.Method == "GET" &&
            result.Http.Route == "health" &&
            result.Http.StatusCode == 200 &&
            result.ObservedAtUtc == finished.ToUniversalTime() &&
            result.ObservedAtUtc.Offset == TimeSpan.Zero &&
            result.ServerTimestampUtc == new DateTimeOffset(
                2026, 11, 8, 23, 0, 0, TimeSpan.Zero) &&
            result.ServerTimestampUtc != result.ObservedAtUtc,
            "The server timestamp replaced client observation time or lost its offset.");

        string[] invalidTimestamps =
        [
            "not-a-date",
            "2026-11-09T01:00:00"
        ];

        foreach (var timestamp in invalidTimestamps)
        {
            var json = JsonSerializer.Serialize(new
            {
                service = "Tsubaki TTS Engine",
                synthesisReady = true,
                timestamp
            });

            var invalid = MetadataParser.Parse(CreateResponse(json));

            Require(
                invalid.State == MetadataDocumentState.Recognized &&
                invalid.SynthesisReady == true &&
                invalid.ServerTimestampUtc is null &&
                invalid.Warnings.Any(warning =>
                    warning.Code == "metadata_field_invalid"),
                "An invalid or offset-free timestamp became a server time claim.");
        }
    }

    /// <summary>Does not infer a boolean readiness value from status text.</summary>
    private static void VerifyUnknownReadiness()
    {
        string[] documents =
        [
            """{"service":"Tsubaki TTS Engine","status":"ready"}""",
            """{"service":"Tsubaki TTS Engine","status":"ready","synthesisReady":null}""",
            """{"service":"Tsubaki TTS Engine","status":"ready","synthesisReady":"true"}""",
            """{"service":"Tsubaki TTS Engine","status":"ready","synthesisReady":1}"""
        ];

        foreach (var json in documents)
        {
            var result = MetadataParser.Parse(CreateResponse(json));

            Require(
                result.State == MetadataDocumentState.Recognized &&
                result.ServiceIdentityMatched &&
                result.Status == "ready" &&
                result.SynthesisReady is null,
                "Missing or incorrectly typed readiness was converted to a boolean.");
        }

        var notReady = MetadataParser.Parse(CreateResponse(
            """{"service":"Tsubaki TTS Engine","status":"ready","synthesisReady":false}"""));

        Require(
            notReady.SynthesisReady == false && notReady.Status == "ready",
            "Status text overrode an explicit false readiness claim.");
    }

    /// <summary>Preserves the selected status contract, including zero and null defaults.</summary>
    private static void VerifyStatus()
    {
        var result = MetadataParser.Parse(CreateResponse(
            StatusJson,
            MetadataEndpoint.Status));

        var status = result.StatusSnapshot
            ?? throw new InvalidOperationException(
                "The normalized status snapshot is missing.");

        Require(
            result.State == MetadataDocumentState.Recognized &&
            result.ServiceIdentityMatched &&
            result.SynthesisReady == true &&
            result.Voices is { Complete: true } voices &&
            voices.Ids.SequenceEqual(new[] { "piper_base", "alloy" }),
            "Status identity, readiness, or voice evidence is incomplete.");

        Require(
            status.BaseVoiceDialect == "en-us" &&
            status.SampleRateHz == 22050 &&
            status.VoiceCloningEnabled == true &&
            status.VoiceCloningAvailable == false &&
            status.WavAvailable == true &&
            status.DefaultSampleRate == 0 &&
            status.DefaultPitch == 1 &&
            status.DefaultVolume == 1 &&
            status.VolumeBoosterDb == 8 &&
            status.DefaultTotalGainDb == 8 &&
            status.TextLimitKnown &&
            status.MaxTextLength is null &&
            status.RateLimit is
            {
                PermitLimit: 20,
                WindowSeconds: 10,
                QueueLimit: 5
            } &&
            result.Warnings.Count == 0,
            "Selected capabilities, configured defaults, or limit claims changed.");

        var bounded = MetadataParser.Parse(CreateResponse(
            """{"service":"Tsubaki TTS Engine","limits":{"maxTextLength":200}}""",
            MetadataEndpoint.Status));

        Require(
            bounded.StatusSnapshot is
            {
                TextLimitKnown: true,
                MaxTextLength: 200
            },
            "The reported text length limit was not retained.");
    }

    /// <summary>Ignores malformed optional values while retaining valid identity and data.</summary>
    private static void VerifyOptionalFields()
    {
        const string json = """
            {
              "service": "Tsubaki TTS Engine",
              "version": 10,
              "synthesisReady": true,
              "model": {
                "baseVoiceDialect": false,
                "sampleRateHz": 0
              },
              "voiceCloning": {
                "enabled": "true",
                "available": false
              },
              "formats": {
                "wav": 0
              },
              "dsp": {
                "defaultSampleRate": 0,
                "defaultPitch": "1.0",
                "defaultVolume": 1e999,
                "volumeBoosterDb": -2,
                "defaultTotalGainDb": 0
              },
              "limits": {
                "maxTextLength": "unlimited",
                "rateLimit": {
                  "permitLimit": 20,
                  "windowSeconds": "10",
                  "queueLimit": 0
                }
              }
            }
            """;

        var result = MetadataParser.Parse(CreateResponse(
            json,
            MetadataEndpoint.Status));

        var status = result.StatusSnapshot
            ?? throw new InvalidOperationException(
                "Valid status fields were discarded.");

        Require(
            result.State == MetadataDocumentState.Recognized &&
            result.ServiceIdentityMatched &&
            result.SynthesisReady == true &&
            result.Version is null &&
            result.Warnings.Count > 0 &&
            status.BaseVoiceDialect is null &&
            status.SampleRateHz is null &&
            status.VoiceCloningEnabled is null &&
            status.VoiceCloningAvailable == false &&
            status.WavAvailable is null &&
            status.DefaultPitch is null &&
            status.DefaultVolume is null &&
            status.DefaultSampleRate == 0 &&
            status.VolumeBoosterDb == -2 &&
            status.DefaultTotalGainDb == 0 &&
            !status.TextLimitKnown &&
            status.MaxTextLength is null &&
            status.RateLimit is
            {
                PermitLimit: 20,
                WindowSeconds: null,
                QueueLimit: 0
            },
            "Malformed optional values replaced or erased valid metadata.");

        Require(
            !JsonSerializer.Serialize(result).Contains(
                "Infinity",
                StringComparison.Ordinal),
            "A nonfinite metadata number reached serialized output.");

        var wrongObjects = MetadataParser.Parse(CreateResponse(
            """{"service":"Tsubaki TTS Engine","synthesisReady":true,"model":[],"dsp":false}""",
            MetadataEndpoint.Status));

        Require(
            wrongObjects.State == MetadataDocumentState.Recognized &&
            wrongObjects.SynthesisReady == true &&
            wrongObjects.StatusSnapshot?.SampleRateHz is null &&
            wrongObjects.Warnings.Count == 2,
            "Malformed optional objects prevented valid readiness parsing.");
    }

    /// <summary>Normalizes duplicate voice IDs without claiming a service identity.</summary>
    private static void VerifyVoices()
    {
        var result = MetadataParser.Parse(CreateResponse(
            """{"voices":["piper_base","alloy","piper_base"]}""",
            MetadataEndpoint.Voices));

        Require(
            result.State == MetadataDocumentState.Recognized &&
            result.Voices is { Complete: true } voices &&
            voices.Ids.SequenceEqual(new[] { "piper_base", "alloy" }) &&
            !result.ServiceIdentityMatched &&
            result.Service is null &&
            result.SynthesisReady is null &&
            result.Warnings.Count == 0,
            "Voice discovery changed identifiers or inferred service readiness.");
    }

    /// <summary>Distinguishes known empty lists, absent lists, and partial retained data.</summary>
    private static void VerifyVoiceCoverage()
    {
        var empty = MetadataParser.Parse(CreateResponse(
            """{"voices":[]}""",
            MetadataEndpoint.Voices));

        Require(
            empty.State == MetadataDocumentState.Recognized &&
            empty.Voices is { Complete: true, Ids.Count: 0 },
            "A known empty voice list became unavailable coverage.");

        string[] missing =
        [
            "{}",
            """{"voices":null}""",
            """{"voices":{}}""",
            """{"data":["piper_base"]}"""
        ];

        foreach (var json in missing)
        {
            var result = MetadataParser.Parse(CreateResponse(
                json,
                MetadataEndpoint.Voices));

            Require(
                result.State == MetadataDocumentState.Unrecognized &&
                result.Voices is null,
                "Missing or unsupported voice data became a complete empty list.");
        }

        var partial = MetadataParser.Parse(CreateResponse(
            """{"voices":["piper_base",42,null,"","\u001b[31m","alloy"]}""",
            MetadataEndpoint.Voices));

        Require(
            partial.State == MetadataDocumentState.Recognized &&
            partial.Voices is { Complete: false } retained &&
            retained.Ids.SequenceEqual(new[] { "piper_base", "alloy" }) &&
            partial.Warnings.Any(warning =>
                warning.Code == "metadata_field_invalid"),
            "Malformed voice entries were retained or their coverage loss was hidden.");
    }

    /// <summary>Accepts the exact retained voice cap and labels truncated discovery.</summary>
    private static void VerifyVoiceLimit()
    {
        var ids = Enumerable.Range(0, MetadataParser.MaxVoiceIds + 1)
            .Select(index => $"voice-{index}")
            .ToArray();

        var exact = MetadataParser.Parse(CreateResponse(
            JsonSerializer.Serialize(new
            {
                voices = ids[..MetadataParser.MaxVoiceIds]
            }),
            MetadataEndpoint.Voices));

        Require(
            exact.Voices is { Complete: true } exactVoices &&
            exactVoices.Ids.Count == MetadataParser.MaxVoiceIds,
            "The exact retained voice limit was rejected.");

        var oversized = MetadataParser.Parse(CreateResponse(
            JsonSerializer.Serialize(new { voices = ids }),
            MetadataEndpoint.Voices));

        Require(
            oversized.Voices is { Complete: false } retained &&
            retained.Ids.Count == MetadataParser.MaxVoiceIds &&
            oversized.Warnings.Count(warning =>
                warning.Code == "metadata_voice_limit") == 1,
            "Excessive voice discovery was not bounded and labeled.");
    }

    /// <summary>Rejects oversized or control-bearing labels without discarding other fields.</summary>
    private static void VerifyTextBounds()
    {
        var exactText = new string('v', MetadataParser.MaxFieldCharacters);

        var exact = MetadataParser.Parse(CreateResponse(
            JsonSerializer.Serialize(new
            {
                service = "Tsubaki TTS Engine",
                version = exactText,
                synthesisReady = true
            })));

        Require(
            exact.Version == exactText && exact.Warnings.Count == 0,
            "The exact selected-text size limit was rejected.");

        string[] invalidText =
        [
            exactText + "x",
            "\u001b[31mprivate",
            "   "
        ];

        foreach (var version in invalidText)
        {
            var result = MetadataParser.Parse(CreateResponse(
                JsonSerializer.Serialize(new
                {
                    service = "Tsubaki TTS Engine",
                    version,
                    synthesisReady = true
                })));

            Require(
                result.State == MetadataDocumentState.Recognized &&
                result.SynthesisReady == true &&
                result.Version is null &&
                result.Warnings.Any(warning =>
                    warning.Code == "metadata_field_invalid"),
                "Rejected label text was retained or erased valid readiness.");
        }

        var voice = MetadataParser.Parse(CreateResponse(
            JsonSerializer.Serialize(new
            {
                voices = new[] { exactText + "x", "piper_base" }
            }),
            MetadataEndpoint.Voices));

        Require(
            voice.Voices is { Complete: false } voices &&
            voices.Ids.SequenceEqual(new[] { "piper_base" }),
            "An oversized voice ID was retained or silently marked complete.");
    }

    /// <summary>Rejects unrelated JSON even when it claims a ready status.</summary>
    private static void VerifyUnexpectedContent()
    {
        string[] documents =
        [
            "null",
            "[]",
            "42",
            "\"login\"",
            "{}",
            """{"status":"ready","synthesisReady":true}""",
            """{"service":"Other service","version":"1.0.10","synthesisReady":true}"""
        ];

        foreach (var json in documents)
        {
            var result = MetadataParser.Parse(CreateResponse(json));

            Require(
                result.State == MetadataDocumentState.Unrecognized &&
                result.Code == "unexpected_content" &&
                !result.ServiceIdentityMatched &&
                result.SynthesisReady is null &&
                result.StatusSnapshot is null,
                "Unrecognized content established Tsubaki identity or readiness.");
        }
    }

    /// <summary>Rejects invalid JSON and duplicate decoded property names.</summary>
    private static void VerifyMalformedJson()
    {
        string[] malformed =
        [
            "",
            "{",
            "<html><body>Login required.</body></html>",
            """{"service":"Tsubaki TTS Engine",}""",
            """{/* comment */"service":"Tsubaki TTS Engine"}"""
        ];

        foreach (var json in malformed)
        {
            var result = MetadataParser.Parse(CreateResponse(json));

            Require(
                result.State == MetadataDocumentState.Invalid &&
                result.Code == "metadata_invalid_json" &&
                !result.ServiceIdentityMatched,
                "Invalid JSON was accepted as metadata.");
        }

        string[] duplicates =
        [
            """{"service":"Tsubaki TTS Engine","synthesisReady":false,"synthesisReady":true}""",
            """{"service":"Tsubaki TTS Engine","synthesisReady":true,"\u0073ynthesisReady":false}""",
            """{"service":"Tsubaki TTS Engine","unknown":{"value":1,"value":2}}"""
        ];

        foreach (var json in duplicates)
        {
            var result = MetadataParser.Parse(CreateResponse(json));

            Require(
                result.State == MetadataDocumentState.Invalid &&
                result.Code == "metadata_duplicate_property" &&
                !result.ServiceIdentityMatched,
                "Ambiguous duplicate metadata property names were accepted.");
        }
    }

    /// <summary>Accepts an optional UTF-8 BOM and rejects invalid UTF-8 bytes.</summary>
    private static void VerifyUtf8()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(HealthJson)];

        var accepted = MetadataParser.Parse(CreateResponse(withBom));

        Require(
            accepted.State == MetadataDocumentState.Recognized &&
            accepted.SynthesisReady == true,
            "A UTF-8 BOM prevented metadata parsing.");

        byte[] invalid =
        [
            0x7B, 0x22, 0x78, 0x22, 0x3A, 0x22,
            0xC3, 0x28,
            0x22, 0x7D
        ];

        var rejected = MetadataParser.Parse(CreateResponse(invalid));

        Require(
            rejected.State == MetadataDocumentState.Invalid &&
            rejected.Code == "metadata_invalid_json",
            "Invalid UTF-8 was accepted inside metadata JSON.");
    }

    /// <summary>Bounds document bytes, nesting depth, and property-name tracking.</summary>
    private static void VerifyStructureLimits()
    {
        var oversized = MetadataParser.Parse(CreateResponse(
            new byte[4 * 1024 * 1024 + 1]));

        Require(
            oversized.State == MetadataDocumentState.Invalid &&
            oversized.Code == "response_limit_exceeded",
            "An oversized synthetic document reached JSON parsing.");

        var nested = "{\"service\":\"Tsubaki TTS Engine\",\"unknown\":" +
            string.Concat(Enumerable.Repeat("{\"child\":", 40)) +
            "0" +
            new string('}', 40) +
            "}";

        var deep = MetadataParser.Parse(CreateResponse(nested));

        Require(
            deep.State == MetadataDocumentState.Invalid &&
            deep.Code == "metadata_invalid_json",
            "Excessive JSON nesting was accepted.");

        var properties = string.Join(
            ",",
            Enumerable.Range(0, 4097)
                .Select(index => $"\"extra{index}\":0"));

        var wideJson =
            "{\"service\":\"Tsubaki TTS Engine\",\"synthesisReady\":true," +
            properties +
            "}";

        var wide = MetadataParser.Parse(CreateResponse(wideJson));

        Require(
            wide.State == MetadataDocumentState.Invalid &&
            wide.Code == "metadata_structure_limit",
            "Excessive property tracking was not bounded.");
    }

    /// <summary>Preserves unavailable HTTP observations without deriving server claims.</summary>
    private static void VerifyUnavailable()
    {
        var forbidden = CreateResponse(
            HealthJson,
            MetadataEndpoint.HealthFallback) with
        {
            State = MetadataRequestState.Failed,
            Code = "http_forbidden",
            Summary = "Metadata request returned HTTP 403.",
            StatusCode = 403,
            ErrorExcerpt = "Metadata access is restricted.",
            Payload = null
        };

        var result = MetadataParser.Parse(forbidden);

        Require(
            result.State == MetadataDocumentState.Unavailable &&
            result.Code == "http_forbidden" &&
            result.Endpoint == MetadataEndpoint.HealthFallback &&
            result.Http.Route == "v1/health" &&
            result.Http.StatusCode == 403 &&
            result.Http.ErrorExcerpt == "Metadata access is restricted." &&
            !result.ServiceIdentityMatched &&
            result.SynthesisReady is null,
            "Unavailable metadata lost its original HTTP evidence.");

        var timedOut = CreateResponse(HealthJson) with
        {
            State = MetadataRequestState.TimedOut,
            Code = "metadata_timeout",
            BodyComplete = false,
            BodyBytesRead = 8,
            Payload = null
        };

        var partial = MetadataParser.Parse(timedOut);

        Require(
            partial.State == MetadataDocumentState.Unavailable &&
            partial.Code == "metadata_timeout" &&
            partial.Http.StatusCode == 200 &&
            partial.Http.BodyBytesRead == 8 &&
            !partial.Http.BodyComplete,
            "Partial transport evidence was erased during metadata parsing.");

        var missingBody = MetadataParser.Parse(
            CreateResponse(HealthJson) with
            {
                BodyComplete = false,
                Payload = null
            });

        Require(
            missingBody.State == MetadataDocumentState.Unavailable &&
            missingBody.Code == "metadata_body_unavailable",
            "An unavailable completed body was treated as valid metadata.");

        var wrongStatus = MetadataParser.Parse(
            CreateResponse(HealthJson) with { StatusCode = 201 });

        Require(
            wrongStatus.State == MetadataDocumentState.Unavailable &&
            !wrongStatus.ServiceIdentityMatched,
            "A contradictory non-200 transport result established service identity.");
    }

    /// <summary>Uses recognized JSON bytes while retaining media-type discrepancies.</summary>
    private static void VerifyMediaTypes()
    {
        string?[] mediaTypes = [null, "text/html", "application/health+json"];

        foreach (var mediaType in mediaTypes)
        {
            var result = MetadataParser.Parse(CreateResponse(
                HealthJson,
                mediaType: mediaType));

            var expectedWarning = mediaType is null || mediaType == "text/html";

            Require(
                result.State == MetadataDocumentState.Recognized &&
                result.SynthesisReady == true &&
                result.Warnings.Any(warning =>
                    warning.Code == "metadata_media_type") == expectedWarning,
                "Metadata media type replaced JSON evidence or hid its discrepancy.");
        }
    }

    /// <summary>Serializes normalized selected fields without raw or unselected content.</summary>
    private static void VerifySelectedExport()
    {
        var result = MetadataParser.Parse(CreateResponse(
            StatusJson,
            MetadataEndpoint.Status));

        var json = JsonSerializer.Serialize(result);

        Require(
            result.State == MetadataDocumentState.Recognized &&
            json.Contains("en-us", StringComparison.Ordinal) &&
            !json.Contains("metadata-private-marker", StringComparison.Ordinal) &&
            !json.Contains("privateConfiguration", StringComparison.Ordinal) &&
            !json.Contains("\"Payload\"", StringComparison.Ordinal) &&
            !json.Contains("\"RequestUri\"", StringComparison.Ordinal),
            "Normalized metadata retained raw or unselected server content.");
    }

    /// <summary>Rejects parsing when caller cancellation is already requested.</summary>
    private static void VerifyCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            MetadataParser.Parse(
                CreateResponse(HealthJson),
                cancellation.Token);
        }
        catch (OperationCanceledException) when (
            cancellation.IsCancellationRequested)
        {
            return;
        }

        throw new InvalidOperationException(
            "Metadata parsing ignored caller cancellation.");
    }

    /// <summary>Creates a complete UTF-8 metadata response fixture.</summary>
    private static MetadataRequestResult CreateResponse(
        string json,
        MetadataEndpoint endpoint = MetadataEndpoint.Health,
        string? mediaType = "application/json")
    {
        return CreateResponse(
            Encoding.UTF8.GetBytes(json),
            endpoint,
            mediaType);
    }

    /// <summary>Creates transport evidence around controlled response bytes.</summary>
    private static MetadataRequestResult CreateResponse(
        byte[] payload,
        MetadataEndpoint endpoint = MetadataEndpoint.Health,
        string? mediaType = "application/json")
    {
        var route = endpoint switch
        {
            MetadataEndpoint.Health => "health",
            MetadataEndpoint.HealthFallback => "v1/health",
            MetadataEndpoint.Status => "tsbk/server/status",
            MetadataEndpoint.Voices => "tsbk/audio/voices",
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
        };

        var started = new DateTimeOffset(
            2026, 11, 9, 14, 0, 0, TimeSpan.Zero);

        return new MetadataRequestResult
        {
            Endpoint = endpoint,
            State = MetadataRequestState.Completed,
            Code = "metadata_response_received",
            Summary = "Metadata response received.",
            Route = route,
            RequestUri = new Uri("https://tts.example.test/proxy/" + route),
            StartedAtUtc = started,
            FinishedAtUtc = started.AddMilliseconds(25),
            TotalDurationMs = 25,
            StatusCode = 200,
            MediaType = mediaType,
            DeclaredContentLength = payload.Length,
            BodyBytesRead = payload.Length,
            BodyComplete = true,
            HeadersDurationMs = 5,
            BodyDurationMs = 20,
            Payload = payload
        };
    }

    /// <summary>Adapts a synchronous parser check to the executable harness.</summary>
    private static Task Run(Action check)
    {
        check();
        return Task.CompletedTask;
    }

    /// <summary>Fails the current regression check when its condition is false.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
