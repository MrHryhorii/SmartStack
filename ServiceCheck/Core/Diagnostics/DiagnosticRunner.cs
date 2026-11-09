using System.Diagnostics;
using System.Globalization;
using Tsubaki.ServiceCheck.Core.Audio;
using Tsubaki.ServiceCheck.Core.Http;
using Tsubaki.ServiceCheck.Core.Networking;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Core.Diagnostics;

/// <summary>
/// Executes supporting network probes and one bounded speech check.
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

    private static readonly (string Id, string Summary)[] RequiredChecks =
    [
        ("speech.request", "The speech request did not produce a result."),
        ("audio.container", "Audio container inspection was not completed."),
        ("audio.signal", "Audio signal inspection was not completed.")
    ];

    /// <summary>
    /// Inspects the network, performs one speech request, and verifies its audio.
    /// </summary>
    public static async Task<DiagnosticRunResult> CheckAsync(
        TargetProfile profile,
        string toolVersion,
        HttpMessageHandler? httpHandler = null,
        CancellationToken cancellationToken = default,
        INetworkProbe? networkProbe = null)
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
            checks.AddRange(CreateMetadataChecks());

            inspectionCancellation.Token.ThrowIfCancellationRequested();

            speech = await client.RequestSpeechAsync(
                cancellationToken,
                runDeadline.Token);

            checks.Add(CreateSpeechCheck(speech, snapshots.Request.Route));
            executionState = GetExecutionState(speech.State);

            if (speech.State == SpeechRequestState.Completed)
            {
                inspectionCancellation.Token.ThrowIfCancellationRequested();

                var payload = speech.Payload
                    ?? throw new InvalidOperationException(
                        "A completed speech response has no payload.");

                inspection = WavInspector.Inspect(
                    payload,
                    inspectionCancellation.Token);

                AddAudioChecks(checks, inspection, speech);

                if (normalized.Output.RetainAudio &&
                    inspection.ContainerState == WavContainerState.Valid)
                {
                    audioPayload = payload;
                }
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

        AddMissingChecks(checks, executionState);
        clock.Stop();

        return new DiagnosticRunResult
        {
            SchemaVersion = 1,
            RunId = CreateRunId(startedAtUtc),
            Tool = new DiagnosticToolInfo(
                "Tsubaki Service Check",
                client.Redact(toolVersion)),
            Mode = DiagnosticMode.Check,
            StartedAtUtc = startedAtUtc,
            FinishedAtUtc = DateTimeOffset.UtcNow,
            ExecutionState = executionState,
            Outcome = DiagnosticOutcomeRules.Evaluate(
                DiagnosticMode.Check,
                executionState,
                checks),
            Target = snapshots.Target,
            Profile = snapshots.Profile,
            Request = snapshots.Request,
            Network = network?.Evidence,
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

    /// <summary>
    /// Records metadata coverage that is unavailable in this execution path.
    /// </summary>
    private static IEnumerable<DiagnosticCheckResult> CreateMetadataChecks()
    {
        return MetadataChecks.Select(item => new DiagnosticCheckResult
        {
            Id = item.Id,
            Role = DiagnosticCheckRole.Supporting,
            Status = DiagnosticCheckStatus.Skipped,
            Code = "coverage_unavailable",
            Summary = item.Summary,
            ObservedAtUtc = DateTimeOffset.UtcNow
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

    /// <summary>
    /// Preserves explicit results for required checks that did not finish.
    /// </summary>
    private static void AddMissingChecks(
        List<DiagnosticCheckResult> checks,
        DiagnosticExecutionState executionState)
    {
        var code = executionState switch
        {
            DiagnosticExecutionState.Cancelled => "cancelled",
            DiagnosticExecutionState.TimedOut => "deadline_exceeded",
            DiagnosticExecutionState.Error => "execution_error",
            _ => "dependency_failed"
        };

        foreach (var (id, summary) in RequiredChecks)
        {
            if (checks.Any(check => check.Id == id))
            {
                continue;
            }

            checks.Add(new DiagnosticCheckResult
            {
                Id = id,
                Role = DiagnosticCheckRole.Required,
                Status = DiagnosticCheckStatus.Skipped,
                Code = code,
                Summary = summary,
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