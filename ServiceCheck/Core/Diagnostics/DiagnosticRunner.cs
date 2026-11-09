using System.Diagnostics;
using System.Globalization;
using Tsubaki.ServiceCheck.Core.Audio;
using Tsubaki.ServiceCheck.Core.Http;
using Tsubaki.ServiceCheck.Core.Metadata;
using Tsubaki.ServiceCheck.Core.Networking;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Core.Diagnostics;

/// <summary>
/// Executes network and metadata inspection with optional speech verification.
/// </summary>
public static class DiagnosticRunner
{
    private static readonly (string Id, string Summary)[] MetadataChecks =
    [
        ("service.health", "Service health was not requested."),
        ("service.status", "Server status was not requested."),
        ("service.voices", "Advertised voices were not requested."),
        ("service.identity", "Service identity was not inspected."),
        ("service.readiness", "Server-reported synthesis readiness was not inspected.")
    ];

    private static readonly (string Id, string Summary)[] SpeechChecks =
    [
        ("speech.request", "The speech request did not produce a result."),
        ("audio.container", "Audio container inspection was not completed."),
        ("audio.signal", "Audio signal inspection was not completed.")
    ];

    /// <summary>Inspects the target and verifies one speech response.</summary>
    public static Task<DiagnosticRunResult> CheckAsync(
        TargetProfile profile,
        string toolVersion,
        HttpMessageHandler? httpHandler = null,
        CancellationToken cancellationToken = default,
        INetworkProbe? networkProbe = null)
    {
        return RunAsync(
            profile,
            toolVersion,
            DiagnosticMode.Check,
            httpHandler,
            cancellationToken,
            networkProbe);
    }

    /// <summary>Inspects network and metadata without requesting speech.</summary>
    public static Task<DiagnosticRunResult> InspectAsync(
        TargetProfile profile,
        string toolVersion,
        HttpMessageHandler? httpHandler = null,
        CancellationToken cancellationToken = default,
        INetworkProbe? networkProbe = null)
    {
        return RunAsync(
            profile,
            toolVersion,
            DiagnosticMode.Inspect,
            httpHandler,
            cancellationToken,
            networkProbe);
    }

    /// <summary>Runs the shared diagnostic stages under one overall deadline.</summary>
    private static async Task<DiagnosticRunResult> RunAsync(
        TargetProfile profile,
        string toolVersion,
        DiagnosticMode mode,
        HttpMessageHandler? httpHandler,
        CancellationToken cancellationToken,
        INetworkProbe? networkProbe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);

        var normalized = ProfileValidator.ValidateAndNormalize(profile);

        using var client = new TsubakiClient(normalized, httpHandler);

        var snapshots = DiagnosticSnapshotBuilder.Create(
            normalized,
            client.Redact);

        var startedAtUtc = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();

        using var runDeadline = new CancellationTokenSource(
            normalized.TimeoutsMs.Run);

        using var inspectionCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, runDeadline.Token);

        var checks = new List<DiagnosticCheckResult>();
        var executionState = DiagnosticExecutionState.Completed;

        NetworkProbeResult? network = null;
        DiagnosticServerEvidence? server = null;
        SpeechRequestResult? speech = null;
        WavInspectionResult? inspection = null;
        byte[]? audioPayload = null;

        try
        {
            network = await ObserveNetworkAsync(
                normalized,
                networkProbe ?? new NetworkProbe(),
                client,
                cancellationToken,
                runDeadline.Token);

            checks.Add(network.Dns);
            checks.Add(network.Tcp);

            inspectionCancellation.Token.ThrowIfCancellationRequested();

            var metadata = await ObserveMetadataAsync(
                client,
                mode,
                mode == DiagnosticMode.Check
                    ? normalized.Synthesis.Voice
                    : null,
                cancellationToken,
                runDeadline.Token);

            server = metadata.Inspection.Server;
            checks.AddRange(metadata.Inspection.Checks);
            executionState = metadata.ExecutionState;

            inspectionCancellation.Token.ThrowIfCancellationRequested();

            if (mode == DiagnosticMode.Check &&
                executionState == DiagnosticExecutionState.Completed)
            {
                speech = await client.RequestSpeechAsync(
                    cancellationToken,
                    runDeadline.Token);

                checks.Add(CreateSpeechCheck(speech, snapshots.Request.Route));
                executionState = GetExecutionState(speech.State);

                inspection = InspectReceivedAudio(
                    checks,
                    server,
                    speech,
                    inspectionCancellation.Token);

                audioPayload = normalized.Output.RetainAudio &&
                    inspection?.ContainerState == WavContainerState.Valid
                        ? speech.Payload
                        : null;
            }
        }
        catch (OperationCanceledException)
        {
            executionState = cancellationToken.IsCancellationRequested
                ? DiagnosticExecutionState.Cancelled
                : runDeadline.IsCancellationRequested
                    ? DiagnosticExecutionState.TimedOut
                    : DiagnosticExecutionState.Error;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            executionState = DiagnosticExecutionState.Error;

            checks.Add(new DiagnosticCheckResult
            {
                Id = "run.execution",
                Role = DiagnosticCheckRole.Supporting,
                Status = DiagnosticCheckStatus.Error,
                Code = "diagnostic_error",
                Summary = "The utility could not complete the diagnostic run.",
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Suggestions =
                [
                    "Review the utility version and reproduce the failure with the same profile."
                ]
            });
        }

        if (cancellationToken.IsCancellationRequested)
        {
            executionState = DiagnosticExecutionState.Cancelled;
        }
        else if (runDeadline.IsCancellationRequested)
        {
            executionState = DiagnosticExecutionState.TimedOut;
        }

        AddMissingChecks(checks, mode, executionState);
        clock.Stop();

        return new DiagnosticRunResult
        {
            SchemaVersion = 1,
            RunId = CreateRunId(startedAtUtc),
            Tool = new DiagnosticToolInfo(
                "Tsubaki Service Check",
                client.Redact(toolVersion)),
            Mode = mode,
            StartedAtUtc = startedAtUtc,
            FinishedAtUtc = DateTimeOffset.UtcNow,
            ExecutionState = executionState,
            Outcome = DiagnosticOutcomeRules.Evaluate(
                mode,
                executionState,
                checks),
            Target = snapshots.Target,
            Profile = snapshots.Profile,
            Request = mode == DiagnosticMode.Check ? snapshots.Request : null,
            Network = network?.Evidence,
            Server = server,
            Timings = new DiagnosticTimings(
                clock.ElapsedMilliseconds,
                speech?.HeadersDurationMs,
                speech?.BodyDurationMs,
                speech?.TotalDurationMs),
            Audio = inspection is null
                ? null
                : new DiagnosticAudioResult(inspection),
            Checks = checks.ToArray(),
            AudioPayload = audioPayload
        };
    }

    /// <summary>
    /// Preserves supporting failures without suppressing an available speech attempt.
    /// </summary>
    private static async Task<NetworkProbeResult> ObserveNetworkAsync(
        TargetProfile profile,
        INetworkProbe probe,
        TsubakiClient client,
        CancellationToken cancellationToken,
        CancellationToken runCancellationToken)
    {
        try
        {
            var result = await probe.ProbeAsync(
                profile,
                cancellationToken,
                runCancellationToken);

            return RedactNetwork(result, client);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var interrupted = cancellationToken.IsCancellationRequested ||
                runCancellationToken.IsCancellationRequested;

            var status = interrupted
                ? DiagnosticCheckStatus.Skipped
                : DiagnosticCheckStatus.Error;

            var code = interrupted
                ? cancellationToken.IsCancellationRequested
                    ? "cancelled"
                    : "deadline_exceeded"
                : exception is OperationCanceledException
                    ? "unexpected_cancellation"
                    : "network_probe_error";

            var summary = interrupted
                ? "Network inspection was interrupted before evidence was available."
                : "The utility could not complete preliminary network inspection.";

            return new NetworkProbeResult(
                NetworkProbe.CreateCheck("dns.resolve", status, code, summary),
                NetworkProbe.CreateCheck("tcp.connect", status, code, summary),
                new DiagnosticNetworkEvidence(
                    new Uri(profile.Target.BaseUrl).Port,
                    [],
                    []));
        }
    }

    /// <summary>
    /// Sanitizes collected network text while preserving typed observations.
    /// </summary>
    private static NetworkProbeResult RedactNetwork(
        NetworkProbeResult result,
        TsubakiClient client)
    {
        var evidence = result.Evidence with
        {
            ResolvedAddresses = result.Evidence.ResolvedAddresses
                .Select(client.Redact)
                .ToArray(),
            ConnectionAttempts = result.Evidence.ConnectionAttempts
                .Select(attempt => attempt with
                {
                    Address = client.Redact(attempt.Address)
                })
                .ToArray()
        };

        return new NetworkProbeResult(
            RedactNetworkCheck(result.Dns, "dns.resolve", client),
            RedactNetworkCheck(result.Tcp, "tcp.connect", client),
            evidence);
    }

    /// <summary>
    /// Restricts a network check to its supporting role and sanitized text.
    /// </summary>
    private static DiagnosticCheckResult RedactNetworkCheck(
        DiagnosticCheckResult check,
        string id,
        TsubakiClient client)
    {
        return check with
        {
            Id = id,
            Role = DiagnosticCheckRole.Supporting,
            Summary = client.Redact(check.Summary),
            Suggestions = check.Suggestions.Select(client.Redact).ToArray(),
            Http = null
        };
    }

    /// <summary>Preserves optional metadata failures while honoring whole-run interruption.</summary>
    private static async Task<(
        MetadataInspectionResult Inspection,
        DiagnosticExecutionState ExecutionState)> ObserveMetadataAsync(
        TsubakiClient client,
        DiagnosticMode mode,
        string? requestedVoice,
        CancellationToken cancellationToken,
        CancellationToken runCancellationToken)
    {
        try
        {
            return await MetadataInspector.InspectAsync(
                client,
                mode,
                requestedVoice,
                cancellationToken,
                runCancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var executionState = cancellationToken.IsCancellationRequested
                ? DiagnosticExecutionState.Cancelled
                : runCancellationToken.IsCancellationRequested
                    ? DiagnosticExecutionState.TimedOut
                    : DiagnosticExecutionState.Completed;

            var inspection = MetadataEvidenceBuilder.Build(
                mode,
                [],
                requestedVoice,
                client.Redact,
                executionState);

            if (executionState != DiagnosticExecutionState.Completed)
            {
                return (inspection, executionState);
            }

            var code = exception is OperationCanceledException
                ? "unexpected_cancellation"
                : "metadata_inspection_error";

            inspection = inspection with
            {
                Checks = inspection.Checks
                    .Select(check => check with
                    {
                        Status = DiagnosticCheckStatus.Error,
                        Code = code,
                        Summary = "The utility could not complete metadata inspection."
                    })
                    .ToArray()
            };

            return (inspection, executionState);
        }
    }

    /// <summary>Inspects audio only after a completed speech response.</summary>
    private static WavInspectionResult? InspectReceivedAudio(
        List<DiagnosticCheckResult> checks,
        DiagnosticServerEvidence? server,
        SpeechRequestResult speech,
        CancellationToken cancellationToken)
    {
        if (speech.State != SpeechRequestState.Completed)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var payload = speech.Payload
            ?? throw new InvalidOperationException(
                "A completed speech response has no payload.");

        var inspection = WavInspector.Inspect(payload, cancellationToken);

        AddAudioChecks(checks, inspection, speech);
        AddReadinessDiscrepancy(checks, server, inspection);

        return inspection;
    }

    /// <summary>Labels successful audio received after an earlier unready claim.</summary>
    private static void AddReadinessDiscrepancy(
        List<DiagnosticCheckResult> checks,
        DiagnosticServerEvidence? server,
        WavInspectionResult inspection)
    {
        if (server?.Readiness != DiagnosticReadiness.NotReady ||
            inspection.ContainerState != WavContainerState.Valid ||
            inspection.SignalState != AudioSignalState.Nonzero)
        {
            return;
        }

        checks.Add(new DiagnosticCheckResult
        {
            Id = "service.readiness.speech_discrepancy",
            Role = DiagnosticCheckRole.Supporting,
            Status = DiagnosticCheckStatus.Warning,
            Code = "speech_after_reported_unready",
            Summary = "The server reported synthesis was not ready before this request returned supported nonzero audio.",
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Http = checks.FirstOrDefault(check =>
                check.Id == "service.readiness")?.Http
        });
    }

    /// <summary>
    /// Copies selected HTTP evidence into the speech check.
    /// </summary>
    private static DiagnosticCheckResult CreateSpeechCheck(
        SpeechRequestResult speech,
        string route)
    {
        return new DiagnosticCheckResult
        {
            Id = "speech.request",
            Role = DiagnosticCheckRole.Required,
            Status = speech.State switch
            {
                SpeechRequestState.Completed => DiagnosticCheckStatus.Passed,
                SpeechRequestState.Failed => DiagnosticCheckStatus.Failed,
                _ => DiagnosticCheckStatus.Error
            },
            Code = speech.Code,
            Summary = speech.Summary,
            ObservedAtUtc = speech.FinishedAtUtc,
            DurationMs = speech.TotalDurationMs,
            Http = new DiagnosticHttpEvidence
            {
                Method = "POST",
                Route = route,
                StatusCode = speech.StatusCode,
                MediaType = speech.MediaType,
                DeclaredContentLength = speech.DeclaredContentLength,
                BodyBytesRead = speech.BodyBytesRead,
                BodyComplete = speech.BodyComplete,
                ReportedSampleRateHz = speech.ReportedSampleRateHz,
                RedirectLocation = speech.RedirectLocation,
                ErrorExcerpt = speech.ErrorExcerpt,
                ErrorExcerptTruncated = speech.ErrorExcerptTruncated,
                ExceptionType = speech.ExceptionType
            },
            Suggestions = GetSpeechSuggestions(speech.Code)
        };
    }

    /// <summary>
    /// Maps transport completion into the run execution state.
    /// </summary>
    private static DiagnosticExecutionState GetExecutionState(
        SpeechRequestState state)
    {
        return state switch
        {
            SpeechRequestState.Completed or SpeechRequestState.Failed =>
                DiagnosticExecutionState.Completed,
            SpeechRequestState.Cancelled => DiagnosticExecutionState.Cancelled,
            SpeechRequestState.TimedOut => DiagnosticExecutionState.TimedOut,
            _ => DiagnosticExecutionState.Error
        };
    }

    /// <summary>
    /// Adds required audio results and any supporting audio warnings.
    /// </summary>
    private static void AddAudioChecks(
        List<DiagnosticCheckResult> checks,
        WavInspectionResult inspection,
        SpeechRequestResult speech)
    {
        var observedAtUtc = DateTimeOffset.UtcNow;

        checks.Add(CreateContainerCheck(inspection, observedAtUtc));
        checks.Add(CreateSignalCheck(inspection, observedAtUtc));

        if (inspection.ContainerState != WavContainerState.Valid)
        {
            return;
        }

        if (!IsWavMediaType(speech.MediaType))
        {
            checks.Add(CreateWarning(
                "speech.media_type",
                "unexpected_content_type",
                "The supported WAV response has a missing or unexpected media type."));
        }

        if (speech.ReportedSampleRateHz is { } reported &&
            inspection.SampleRateHz is { } actual &&
            reported != actual)
        {
            checks.Add(CreateWarning(
                "audio.sample_rate_header",
                "audio_sample_rate_mismatch",
                "The response sample-rate header differs from the WAV header."));
        }

        foreach (var warning in inspection.Warnings)
        {
            checks.Add(CreateWarning(
                $"audio.warning.{warning.Code}",
                warning.Code,
                warning.Summary));
        }
    }

    /// <summary>
    /// Records whether the response contains a supported WAV container.
    /// </summary>
    private static DiagnosticCheckResult CreateContainerCheck(
        WavInspectionResult inspection,
        DateTimeOffset observedAtUtc)
    {
        var valid = inspection.ContainerState == WavContainerState.Valid;

        return new DiagnosticCheckResult
        {
            Id = "audio.container",
            Role = DiagnosticCheckRole.Required,
            Status = inspection.ContainerState switch
            {
                WavContainerState.Valid => DiagnosticCheckStatus.Passed,
                WavContainerState.Invalid => DiagnosticCheckStatus.Failed,
                _ => DiagnosticCheckStatus.Error
            },
            Code = valid ? "audio_container_verified" : inspection.Code,
            Summary = valid
                ? "Received a supported PCM16 WAV container."
                : inspection.Summary,
            ObservedAtUtc = observedAtUtc,
            Suggestions = inspection.ContainerState switch
            {
                WavContainerState.Invalid =>
                [
                    "Inspect server and reverse-proxy response handling for this request."
                ],
                WavContainerState.Unsupported =>
                [
                    "Use the supported little-endian PCM16 WAV response format."
                ],
                _ => []
            }
        };
    }

    /// <summary>
    /// Records nonzero audio evidence or an unmet container dependency.
    /// </summary>
    private static DiagnosticCheckResult CreateSignalCheck(
        WavInspectionResult inspection,
        DateTimeOffset observedAtUtc)
    {
        if (inspection.ContainerState != WavContainerState.Valid)
        {
            return new DiagnosticCheckResult
            {
                Id = "audio.signal",
                Role = DiagnosticCheckRole.Required,
                Status = DiagnosticCheckStatus.Skipped,
                Code = "dependency_failed",
                Summary = "Signal analysis requires a supported PCM16 WAV container.",
                ObservedAtUtc = observedAtUtc
            };
        }

        return new DiagnosticCheckResult
        {
            Id = "audio.signal",
            Role = DiagnosticCheckRole.Required,
            Status = inspection.SignalState switch
            {
                AudioSignalState.Nonzero => DiagnosticCheckStatus.Passed,
                AudioSignalState.Silent => DiagnosticCheckStatus.Failed,
                _ => DiagnosticCheckStatus.Error
            },
            Code = inspection.SignalState switch
            {
                AudioSignalState.Nonzero => "audio_signal_verified",
                AudioSignalState.Silent => "audio_silent",
                _ => "audio_signal_unavailable"
            },
            Summary = inspection.SignalState switch
            {
                AudioSignalState.Nonzero =>
                    "Received audio contains nonzero PCM16 samples.",
                AudioSignalState.Silent =>
                    "Received PCM16 audio contains only zero-valued samples.",
                _ => "Signal analysis did not produce a result."
            },
            ObservedAtUtc = observedAtUtc,
            Suggestions = inspection.SignalState == AudioSignalState.Silent
                ? ["Review server audio settings and processing for this request."]
                : []
        };
    }

    /// <summary>
    /// Creates a supporting warning with its stable observation code.
    /// </summary>
    private static DiagnosticCheckResult CreateWarning(
        string id,
        string code,
        string summary)
    {
        return new DiagnosticCheckResult
        {
            Id = id,
            Role = DiagnosticCheckRole.Supporting,
            Status = DiagnosticCheckStatus.Warning,
            Code = code,
            Summary = summary,
            ObservedAtUtc = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Recognizes common WAV response media types.
    /// </summary>
    private static bool IsWavMediaType(string? mediaType)
    {
        return mediaType?.ToLowerInvariant() is
            "audio/wav" or
            "audio/x-wav" or
            "audio/wave" or
            "audio/vnd.wave";
    }

    /// <summary>Preserves missing metadata and explicit mode-specific speech omissions.</summary>
    private static void AddMissingChecks(
        List<DiagnosticCheckResult> checks,
        DiagnosticMode mode,
        DiagnosticExecutionState executionState)
    {
        var code = executionState switch
        {
            DiagnosticExecutionState.Cancelled => "cancelled",
            DiagnosticExecutionState.TimedOut => "deadline_exceeded",
            DiagnosticExecutionState.Error => "execution_error",
            _ => "coverage_unavailable"
        };

        foreach (var (id, summary) in MetadataChecks)
        {
            if (checks.Any(check => check.Id == id))
            {
                continue;
            }

            var required = mode == DiagnosticMode.Inspect &&
                (id is "service.identity" or "service.readiness");

            checks.Add(new DiagnosticCheckResult
            {
                Id = id,
                Role = required
                    ? DiagnosticCheckRole.Required
                    : DiagnosticCheckRole.Supporting,
                Status = DiagnosticCheckStatus.Skipped,
                Code = code,
                Summary = summary,
                ObservedAtUtc = DateTimeOffset.UtcNow
            });
        }

        foreach (var (id, summary) in SpeechChecks)
        {
            if (checks.Any(check => check.Id == id))
            {
                continue;
            }

            var omitted = mode == DiagnosticMode.Inspect;

            checks.Add(new DiagnosticCheckResult
            {
                Id = id,
                Role = omitted
                    ? DiagnosticCheckRole.Supporting
                    : DiagnosticCheckRole.Required,
                Status = DiagnosticCheckStatus.Skipped,
                Code = omitted
                    ? "not_requested"
                    : executionState == DiagnosticExecutionState.Completed
                        ? "dependency_failed"
                        : code,
                Summary = omitted
                    ? "This check was not requested in inspection mode."
                    : summary,
                ObservedAtUtc = DateTimeOffset.UtcNow
            });
        }
    }

    /// <summary>
    /// Provides next steps tied to observed HTTP failure categories.
    /// </summary>
    private static string[] GetSpeechSuggestions(string code)
    {
        return code switch
        {
            "dns_resolution_failed" =>
            [
                "Check the configured hostname and DNS resolution from this client."
            ],
            "connection_refused" =>
            [
                "Check the target URL, listening address, port, and service state."
            ],
            "connection_timeout" or "deadline_exceeded" =>
            [
                "Check the access path and configured request and run timeouts."
            ],
            "tls_handshake_failed" or "tls_validation_failed" =>
            [
                "Check the endpoint hostname, certificate trust, and TLS configuration."
            ],
            "http_unauthorized" or "http_forbidden" =>
            [
                "Check the deployment authentication and access policy."
            ],
            "http_not_found" or "http_method_not_allowed" =>
            [
                "Check the service root, reverse-proxy prefix, speech route, and deployed version."
            ],
            "http_rate_limited" =>
            [
                "Review server rate limits before sending another request."
            ],
            "http_server_error" =>
            [
                "Check server and reverse-proxy logs for this request time window."
            ],
            "http_redirect" =>
            [
                "Configure the final service URL directly; redirects are not followed."
            ],
            "response_limit_exceeded" =>
            [
                "Review the configured audio byte limit and the server response."
            ],
            "response_length_mismatch" or "response_truncated" =>
            [
                "Check server and proxy response handling for truncation."
            ],
            _ => []
        };
    }

    /// <summary>
    /// Creates a utility-controlled identifier using UTC and a random suffix.
    /// </summary>
    private static string CreateRunId(DateTimeOffset startedAtUtc)
    {
        var timestamp = startedAtUtc.UtcDateTime.ToString(
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture);

        var suffix = Guid.NewGuid().ToString("N")[..12];

        return $"{timestamp}_{suffix}";
    }
}
