using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Http;

namespace Tsubaki.ServiceCheck.Core.Metadata;

/// <summary>Stores normalized server evidence and its diagnostic checks.</summary>
public sealed record MetadataInspectionResult(
    DiagnosticServerEvidence Server,
    IReadOnlyList<DiagnosticCheckResult> Checks);

/// <summary>Combines metadata observations without performing network operations.</summary>
public static class MetadataEvidenceBuilder
{
    /// <summary>Selects sourced claims and builds checks for the requested mode.</summary>
    public static MetadataInspectionResult Build(
        DiagnosticMode mode,
        IReadOnlyList<(MetadataObservation Observation, long DurationMs)> responses,
        string? requestedVoice,
        Func<string, string> redact,
        DiagnosticExecutionState executionState = DiagnosticExecutionState.Completed)
    {
        ArgumentNullException.ThrowIfNull(responses);
        ArgumentNullException.ThrowIfNull(redact);

        if (mode is not (DiagnosticMode.Check or DiagnosticMode.Inspect))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (executionState is not (
            DiagnosticExecutionState.Completed or
            DiagnosticExecutionState.Cancelled or
            DiagnosticExecutionState.TimedOut or
            DiagnosticExecutionState.Error))
        {
            throw new ArgumentOutOfRangeException(nameof(executionState));
        }

        if (mode == DiagnosticMode.Check)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedVoice);
        }

        var sources = CreateSources(responses);

        var health = FindEndpoint(sources, MetadataEndpoint.HealthFallback)
            ?? FindEndpoint(sources, MetadataEndpoint.Health);

        var status = FindEndpoint(sources, MetadataEndpoint.Status);
        var dedicatedVoices = FindEndpoint(sources, MetadataEndpoint.Voices);

        var identity = LastMatching(sources, HasExpectedIdentity);

        var identityClaim = LastMatching(
            sources,
            observation =>
                IsServiceEndpoint(observation.Endpoint) &&
                observation.Service is not null);

        var version = LastMatching(
            sources,
            observation =>
                HasExpectedIdentity(observation) &&
                observation.Version is not null);

        var readiness = LastMatching(
            sources,
            observation =>
                HasExpectedIdentity(observation) &&
                observation.SynthesisReady.HasValue);

        var statusSnapshot = status is not null &&
            HasExpectedIdentity(status.Observation)
                ? status
                : null;

        var voices = SelectVoices(dedicatedVoices, statusSnapshot);

        var readinessValue = readiness?.Observation.SynthesisReady switch
        {
            true => DiagnosticReadiness.Ready,
            false => DiagnosticReadiness.NotReady,
            _ => DiagnosticReadiness.Unknown
        };

        var server = new DiagnosticServerEvidence(
            identity?.Observation.Service ?? identityClaim?.Observation.Service,
            version?.Observation.Version,
            readinessValue,
            voices?.Observation.Voices?.Ids ?? [])
        {
            IdentitySourceRoute = identity?.Observation.Http.Route
                ?? identityClaim?.Observation.Http.Route,
            VersionSourceRoute = version?.Observation.Http.Route,
            ReadinessSourceRoute = readiness?.Observation.Http.Route,
            ReadinessObservedAtUtc = readiness?.Observation.ObservedAtUtc,
            VoicesSourceRoute = voices?.Observation.Http.Route,
            AdvertisedVoicesComplete = voices?.Observation.Voices?.Complete ?? false,
            StatusSnapshot = statusSnapshot?.Observation.StatusSnapshot,
            StatusSourceRoute = statusSnapshot?.Observation.Http.Route,
            Observations = sources.Select(source => source.Observation).ToArray()
        };

        var identityRole = mode == DiagnosticMode.Inspect
            ? DiagnosticCheckRole.Required
            : DiagnosticCheckRole.Supporting;

        var checks = new List<DiagnosticCheckResult>
        {
            CreateRouteCheck(
                "service.health",
                health,
                executionState,
                health is null
                    ? null
                    : sources.Where(source => source.Observation.Endpoint is
                        MetadataEndpoint.Health or MetadataEndpoint.HealthFallback)
                        .Sum(source => source.DurationMs)),

            CreateRouteCheck("service.status", status, executionState),

            CreateVoicesCheck(dedicatedVoices, voices, executionState),

            CreateIdentityCheck(
                identity,
                identityClaim,
                identityRole,
                executionState),

            CreateReadinessCheck(
                readiness,
                identityRole,
                executionState)
        };

        AddParsingWarnings(checks, sources);
        AddDisagreements(checks, sources, readiness, version);
        AddVoiceDisagreement(checks, dedicatedVoices, statusSnapshot);

        if (mode == DiagnosticMode.Check)
        {
            AddRequestWarnings(checks, voices, statusSnapshot, requestedVoice!);
        }

        return new MetadataInspectionResult(
            RedactServer(server, redact),
            checks.Select(check => RedactCheck(check, redact)).ToArray());
    }

    /// <summary>Validates the bounded set of endpoint observations.</summary>
    private static Source[] CreateSources(
        IReadOnlyList<(MetadataObservation Observation, long DurationMs)> responses)
    {
        if (responses.Count > 4)
        {
            throw new ArgumentException(
                "Metadata inspection accepts at most four endpoint observations.",
                nameof(responses));
        }

        var endpoints = new HashSet<MetadataEndpoint>();
        var sources = new List<Source>(responses.Count);

        foreach (var (observation, durationMs) in responses)
        {
            ArgumentNullException.ThrowIfNull(observation);

            if (observation.Endpoint is not (
                MetadataEndpoint.Health or
                MetadataEndpoint.HealthFallback or
                MetadataEndpoint.Status or
                MetadataEndpoint.Voices))
            {
                throw new ArgumentException(
                    "A metadata observation has an unsupported endpoint.",
                    nameof(responses));
            }

            if (!endpoints.Add(observation.Endpoint))
            {
                throw new ArgumentException(
                    "Metadata endpoint observations must be unique.",
                    nameof(responses));
            }

            if (durationMs < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(responses),
                    "Metadata durations cannot be negative.");
            }

            sources.Add(new Source(observation, durationMs));
        }

        return sources.ToArray();
    }

    /// <summary>Finds the observation for one configured endpoint.</summary>
    private static Source? FindEndpoint(
        IEnumerable<Source> sources,
        MetadataEndpoint endpoint)
    {
        return sources.FirstOrDefault(source =>
            source.Observation.Endpoint == endpoint);
    }

    /// <summary>Chooses the most recent valid claim by client observation time.</summary>
    private static Source? LastMatching(
        IEnumerable<Source> sources,
        Func<MetadataObservation, bool> predicate)
    {
        return sources
            .Where(source => predicate(source.Observation))
            .OrderBy(source => source.Observation.ObservedAtUtc)
            .LastOrDefault();
    }

    /// <summary>Recognizes endpoints allowed to contribute service identity claims.</summary>
    private static bool IsServiceEndpoint(MetadataEndpoint endpoint)
    {
        return endpoint is MetadataEndpoint.Health or
            MetadataEndpoint.HealthFallback or MetadataEndpoint.Status;
    }

    /// <summary>Requires recognized service metadata and a matching identifier.</summary>
    private static bool HasExpectedIdentity(MetadataObservation observation)
    {
        return IsServiceEndpoint(observation.Endpoint) &&
            observation.State == MetadataDocumentState.Recognized &&
            observation.ServiceIdentityMatched;
    }

    /// <summary>Prefers complete dedicated discovery, then complete status discovery.</summary>
    private static Source? SelectVoices(Source? dedicated, Source? status)
    {
        var direct = dedicated?.Observation is
        {
            State: MetadataDocumentState.Recognized,
            Voices: not null
        }
            ? dedicated
            : null;

        var fallback = status?.Observation.Voices is not null
            ? status
            : null;

        if (direct?.Observation.Voices?.Complete == true)
        {
            return direct;
        }

        if (fallback?.Observation.Voices?.Complete == true)
        {
            return fallback;
        }

        return direct ?? fallback;
    }

    /// <summary>Maps one route observation to supporting diagnostic evidence.</summary>
    private static DiagnosticCheckResult CreateRouteCheck(
        string id,
        Source? source,
        DiagnosticExecutionState executionState,
        long? durationMs = null)
    {
        if (source is null)
        {
            return MissingCheck(
                id,
                DiagnosticCheckRole.Supporting,
                executionState);
        }

        var observation = source.Observation;

        var status = observation.State switch
        {
            MetadataDocumentState.Recognized => DiagnosticCheckStatus.Passed,
            MetadataDocumentState.Invalid or MetadataDocumentState.Unrecognized =>
                DiagnosticCheckStatus.Failed,
            _ => observation.Code switch
            {
                "client_error" or "unexpected_cancellation" or
                    "metadata_body_unavailable" => DiagnosticCheckStatus.Error,

                "cancelled" or "deadline_exceeded" =>
                    observation.Http.StatusCode is null &&
                    observation.Http.BodyBytesRead is null
                        ? DiagnosticCheckStatus.Skipped
                        : DiagnosticCheckStatus.Error,

                _ => DiagnosticCheckStatus.Failed
            }
        };

        return new DiagnosticCheckResult
        {
            Id = id,
            Role = DiagnosticCheckRole.Supporting,
            Status = status,
            Code = observation.Code,
            Summary = observation.Summary,
            ObservedAtUtc = observation.ObservedAtUtc,
            DurationMs = durationMs ?? source.DurationMs,
            Http = observation.Http
        };
    }

    /// <summary>Reports voice discovery while labeling status-based fallback.</summary>
    private static DiagnosticCheckResult CreateVoicesCheck(
        Source? dedicated,
        Source? selected,
        DiagnosticExecutionState executionState)
    {
        if (selected is null || selected.Observation.Voices is not { } voices)
        {
            return CreateRouteCheck(
                "service.voices",
                dedicated,
                executionState);
        }

        var fromStatus = selected.Observation.Endpoint == MetadataEndpoint.Status;
        var warning = fromStatus || !voices.Complete;

        return new DiagnosticCheckResult
        {
            Id = "service.voices",
            Role = DiagnosticCheckRole.Supporting,
            Status = warning
                ? DiagnosticCheckStatus.Warning
                : DiagnosticCheckStatus.Passed,
            Code = fromStatus
                ? "voices_status_fallback"
                : voices.Complete ? "voices_received" : "voices_incomplete",
            Summary = fromStatus
                ? "Advertised voices were obtained from status because dedicated discovery was unavailable or incomplete."
                : voices.Complete
                    ? $"Received {voices.Ids.Count} advertised voice ID(s)."
                    : "The retained advertised voice list is incomplete.",
            ObservedAtUtc = dedicated?.Observation.ObservedAtUtc
                ?? selected.Observation.ObservedAtUtc,
            DurationMs = dedicated?.DurationMs,
            Http = dedicated?.Observation.Http ?? selected.Observation.Http
        };
    }

    /// <summary>Separates a matching identity, an explicit mismatch, and unknown identity.</summary>
    private static DiagnosticCheckResult CreateIdentityCheck(
        Source? identity,
        Source? claim,
        DiagnosticCheckRole role,
        DiagnosticExecutionState executionState)
    {
        if (identity is not null)
        {
            return DerivedCheck(
                "service.identity",
                role,
                DiagnosticCheckStatus.Passed,
                "service_identity_reported",
                "Metadata reported the expected Tsubaki service identifier.",
                identity);
        }

        if (claim is not null)
        {
            return DerivedCheck(
                "service.identity",
                role,
                DiagnosticCheckStatus.Failed,
                "service_identity_mismatch",
                "Metadata identified a different service.",
                claim);
        }

        return MissingCheck("service.identity", role, executionState) with
        {
            Code = executionState == DiagnosticExecutionState.Completed
                ? "service_identity_unavailable"
                : GetMissingCode(executionState),
            Summary = "Recognized Tsubaki identity metadata was unavailable."
        };
    }

    /// <summary>Uses the most recent valid boolean readiness claim.</summary>
    private static DiagnosticCheckResult CreateReadinessCheck(
        Source? readiness,
        DiagnosticCheckRole role,
        DiagnosticExecutionState executionState)
    {
        if (readiness is null)
        {
            return MissingCheck("service.readiness", role, executionState) with
            {
                Code = executionState == DiagnosticExecutionState.Completed
                    ? "readiness_unavailable"
                    : GetMissingCode(executionState),
                Summary = "A recognized boolean synthesis-readiness claim was unavailable."
            };
        }

        var ready = readiness.Observation.SynthesisReady == true;

        return DerivedCheck(
            "service.readiness",
            role,
            ready ? DiagnosticCheckStatus.Passed : DiagnosticCheckStatus.Failed,
            ready ? "readiness_reported_ready" : "readiness_reported_not_ready",
            ready
                ? "The server reported that synthesis was ready."
                : "The server reported that synthesis was not ready.",
            readiness);
    }

    /// <summary>Preserves parsing limitations as stable supporting checks.</summary>
    private static void AddParsingWarnings(
        List<DiagnosticCheckResult> checks,
        IEnumerable<Source> sources)
    {
        foreach (var source in sources)
        {
            var observation = source.Observation;

            if (observation.Warnings.Count == 0)
            {
                continue;
            }

            var endpoint = observation.Endpoint switch
            {
                MetadataEndpoint.Health => "health",
                MetadataEndpoint.HealthFallback => "health_fallback",
                MetadataEndpoint.Status => "status",
                MetadataEndpoint.Voices => "voices",
                _ => throw new ArgumentOutOfRangeException()
            };

            checks.Add(DerivedCheck(
                $"metadata.{endpoint}.warnings",
                DiagnosticCheckRole.Supporting,
                DiagnosticCheckStatus.Warning,
                "metadata_warnings",
                $"Metadata parsing reported {observation.Warnings.Count} warning(s). Review the source observations.",
                source));
        }
    }

    /// <summary>Labels conflicting identity, version, and readiness claims.</summary>
    private static void AddDisagreements(
        List<DiagnosticCheckResult> checks,
        Source[] sources,
        Source? readiness,
        Source? version)
    {
        var identityClaims = sources
            .Where(source => IsServiceEndpoint(source.Observation.Endpoint))
            .Select(source => source.Observation.Service?.Trim())
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .Count();

        if (identityClaims > 1)
        {
            var latest = LastMatching(
                sources,
                observation =>
                    IsServiceEndpoint(observation.Endpoint) &&
                    observation.Service is not null)!;

            checks.Add(DerivedCheck(
                "service.identity.disagreement",
                DiagnosticCheckRole.Supporting,
                DiagnosticCheckStatus.Warning,
                "service_identity_disagreement",
                "Service identifier claims differed between metadata responses.",
                latest));
        }

        var versions = sources
            .Where(source => HasExpectedIdentity(source.Observation))
            .Select(source => source.Observation.Version)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count();

        if (versions > 1 && version is not null)
        {
            checks.Add(DerivedCheck(
                "service.version.disagreement",
                DiagnosticCheckRole.Supporting,
                DiagnosticCheckStatus.Warning,
                "server_version_disagreement",
                "Server version claims differed between metadata responses.",
                version));
        }

        var readinessClaims = sources
            .Where(source => HasExpectedIdentity(source.Observation))
            .Select(source => source.Observation.SynthesisReady)
            .Where(value => value.HasValue)
            .Distinct()
            .Take(2)
            .Count();

        if (readinessClaims > 1 && readiness is not null)
        {
            checks.Add(DerivedCheck(
                "service.readiness.disagreement",
                DiagnosticCheckRole.Supporting,
                DiagnosticCheckStatus.Warning,
                "readiness_disagreement",
                "Readiness claims differed between metadata responses. The most recent valid claim is selected.",
                readiness));
        }
    }

    /// <summary>Compares complete advertised lists without merging their claims.</summary>
    private static void AddVoiceDisagreement(
        List<DiagnosticCheckResult> checks,
        Source? dedicated,
        Source? status)
    {
        if (dedicated is null || status is null ||
            dedicated.Observation.Voices is not { Complete: true } direct ||
            status.Observation.Voices is not { Complete: true } fallback)
        {
            return;
        }

        var ids = new HashSet<string>(direct.Ids, StringComparer.Ordinal);

        if (ids.SetEquals(fallback.Ids))
        {
            return;
        }

        checks.Add(DerivedCheck(
            "service.voices.disagreement",
            DiagnosticCheckRole.Supporting,
            DiagnosticCheckStatus.Warning,
            "voice_list_disagreement",
            "Complete advertised voice lists differed between the dedicated and status endpoints.",
            dedicated));
    }

    /// <summary>Warns about advertised request mismatches without blocking synthesis.</summary>
    private static void AddRequestWarnings(
        List<DiagnosticCheckResult> checks,
        Source? voices,
        Source? status,
        string requestedVoice)
    {
        if (voices is not null &&
            voices.Observation.Voices is { Complete: true } advertised &&
            !advertised.Ids.Contains(requestedVoice, StringComparer.Ordinal))
        {
            checks.Add(DerivedCheck(
                "speech.voice_advertisement",
                DiagnosticCheckRole.Supporting,
                DiagnosticCheckStatus.Warning,
                "voice_not_advertised",
                "The configured voice is absent from the selected advertised list.",
                voices));
        }

        if (status is null || status.Observation.StatusSnapshot?.WavAvailable != false)
        {
            return;
        }

        checks.Add(DerivedCheck(
            "service.wav_advertisement",
            DiagnosticCheckRole.Supporting,
            DiagnosticCheckStatus.Warning,
            "wav_not_advertised",
            "The server did not advertise WAV support in status metadata.",
            status));
    }

    /// <summary>Builds a sourced conclusion without assigning an HTTP-operation duration.</summary>
    private static DiagnosticCheckResult DerivedCheck(
        string id,
        DiagnosticCheckRole role,
        DiagnosticCheckStatus status,
        string code,
        string summary,
        Source source)
    {
        return new DiagnosticCheckResult
        {
            Id = id,
            Role = role,
            Status = status,
            Code = code,
            Summary = summary,
            ObservedAtUtc = source.Observation.ObservedAtUtc,
            Http = source.Observation.Http
        };
    }

    /// <summary>Records coverage that was unavailable or interrupted before observation.</summary>
    private static DiagnosticCheckResult MissingCheck(
        string id,
        DiagnosticCheckRole role,
        DiagnosticExecutionState executionState)
    {
        var interrupted = executionState is
            DiagnosticExecutionState.Cancelled or DiagnosticExecutionState.TimedOut;

        return new DiagnosticCheckResult
        {
            Id = id,
            Role = role,
            Status = interrupted
                ? DiagnosticCheckStatus.Skipped
                : DiagnosticCheckStatus.Error,
            Code = GetMissingCode(executionState),
            Summary = interrupted
                ? "Metadata inspection ended before this observation was available."
                : "This metadata observation was not available.",
            ObservedAtUtc = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Returns a coverage code tied to the execution state.</summary>
    private static string GetMissingCode(DiagnosticExecutionState executionState)
    {
        return executionState switch
        {
            DiagnosticExecutionState.Cancelled => "cancelled",
            DiagnosticExecutionState.TimedOut => "deadline_exceeded",
            DiagnosticExecutionState.Error => "execution_error",
            _ => "coverage_unavailable"
        };
    }

    /// <summary>Redacts selected values after raw claims have been compared.</summary>
    private static DiagnosticServerEvidence RedactServer(
        DiagnosticServerEvidence server,
        Func<string, string> redact)
    {
        return server with
        {
            Service = RedactOptional(server.Service, redact),
            Version = RedactOptional(server.Version, redact),
            AdvertisedVoices = server.AdvertisedVoices.Select(redact).ToArray(),
            IdentitySourceRoute = RedactOptional(server.IdentitySourceRoute, redact),
            VersionSourceRoute = RedactOptional(server.VersionSourceRoute, redact),
            ReadinessSourceRoute = RedactOptional(server.ReadinessSourceRoute, redact),
            VoicesSourceRoute = RedactOptional(server.VoicesSourceRoute, redact),
            StatusSourceRoute = RedactOptional(server.StatusSourceRoute, redact),
            StatusSnapshot = RedactStatus(server.StatusSnapshot, redact),
            Observations = server.Observations
                .Select(observation => observation with
                {
                    Service = RedactOptional(observation.Service, redact),
                    Version = RedactOptional(observation.Version, redact),
                    Status = RedactOptional(observation.Status, redact),
                    Summary = redact(observation.Summary),
                    Http = RedactHttp(observation.Http, redact),
                    Voices = observation.Voices is { } voices
                        ? voices with { Ids = voices.Ids.Select(redact).ToArray() }
                        : null,
                    StatusSnapshot = RedactStatus(observation.StatusSnapshot, redact),
                    Warnings = observation.Warnings
                        .Select(warning => warning with
                        {
                            Summary = redact(warning.Summary)
                        })
                        .ToArray()
                })
                .ToArray()
        };
    }

    /// <summary>Redacts text in selected status defaults.</summary>
    private static MetadataStatusSnapshot? RedactStatus(
        MetadataStatusSnapshot? snapshot,
        Func<string, string> redact)
    {
        return snapshot is null
            ? null
            : snapshot with
            {
                BaseVoiceDialect = RedactOptional(snapshot.BaseVoiceDialect, redact)
            };
    }

    /// <summary>Redacts check text and its selected HTTP fields.</summary>
    private static DiagnosticCheckResult RedactCheck(
        DiagnosticCheckResult check,
        Func<string, string> redact)
    {
        return check with
        {
            Summary = redact(check.Summary),
            Suggestions = check.Suggestions.Select(redact).ToArray(),
            Http = check.Http is null ? null : RedactHttp(check.Http, redact)
        };
    }

    /// <summary>Redacts captured HTTP text while retaining status and byte evidence.</summary>
    private static DiagnosticHttpEvidence RedactHttp(
        DiagnosticHttpEvidence evidence,
        Func<string, string> redact)
    {
        return evidence with
        {
            Route = redact(evidence.Route),
            MediaType = RedactOptional(evidence.MediaType, redact),
            RedirectLocation = RedactOptional(evidence.RedirectLocation, redact),
            ErrorExcerpt = RedactOptional(evidence.ErrorExcerpt, redact),
            ExceptionType = RedactOptional(evidence.ExceptionType, redact)
        };
    }

    /// <summary>Preserves unavailable text while redacting available values.</summary>
    private static string? RedactOptional(
        string? value,
        Func<string, string> redact)
    {
        return value is null ? null : redact(value);
    }

    /// <summary>Pairs a parsed observation with its measured operation duration.</summary>
    private sealed record Source(
        MetadataObservation Observation,
        long DurationMs);
}
