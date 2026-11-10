using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Audio;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Http;
using Tsubaki.ServiceCheck.Core.Metadata;

namespace Tsubaki.ServiceCheck.Core.Reports;

/// <summary>Validates imported report data without accessing external resources.</summary>
internal static class ReportValidator
{
    internal const int MaximumStringCharacters = 65_536;
    internal const int MaximumCollectionItems = 1_024;

    /// <summary>Checks report bounds, required evidence, and outcome consistency.</summary>
    internal static void Validate(
        DiagnosticRunResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        Require(result.SchemaVersion == 1, "Report schemaVersion must be 1.");
        RequireText(result.RunId, "runId", 128);
        Require(result.Tool is not null, "Report tool information is required.");
        RequireText(result.Tool.Name, "tool.name");
        RequireText(result.Tool.Version, "tool.version");
        RequireEnum(result.Mode, "mode");
        RequireEnum(result.ExecutionState, "executionState");
        RequireEnum(result.Outcome, "outcome");

        ValidateProfile(result);
        ValidateTimings(result);
        ValidateRequest(result);
        ValidateNetwork(result.Network);
        ValidateServer(result.Server);
        ValidateAudio(result.Audio);
        ValidateChecks(result, cancellationToken);

        var expected = DiagnosticOutcomeRules.Evaluate(
            result.Mode,
            result.ExecutionState,
            result.Checks);

        Require(result.Outcome == expected,
            "Report outcome does not match its execution state and checks.");

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Validates exported settings without resolving their addresses.</summary>
    private static void ValidateProfile(DiagnosticRunResult result)
    {
        var target = result.Target
            ?? throw new JsonException("Report target is required.");
        var profile = result.Profile
            ?? throw new JsonException("Report profile is required.");
        var routes = profile.Routes
            ?? throw new JsonException("Report profile routes are required.");
        var timeouts = profile.TimeoutsMs
            ?? throw new JsonException("Report profile timeouts are required.");
        var limits = profile.Limits
            ?? throw new JsonException("Report profile limits are required.");

        RequireText(target.Id, "target.id");
        RequireText(target.BaseUrl, "target.baseUrl");
        RequireText(profile.Name, "profile.name");
        RequireHash(profile.Fingerprint, "profile.fingerprint");
        Require(profile.AuthenticationType is "none" or "bearer",
            "Report authenticationType must be 'none' or 'bearer'.");

        foreach (var route in new[]
            { routes.Health, routes.HealthFallback, routes.Status, routes.Voices, routes.Speech })
        {
            RequireText(route, "profile.routes");
        }

        foreach (var timeout in new[]
            { timeouts.Dns, timeouts.Tcp, timeouts.Metadata, timeouts.Speech, timeouts.Run })
        {
            Require(timeout is >= 1 and <= 900_000,
                "Report timeouts must be between 1 and 900000 milliseconds.");
        }

        Require(limits.MaxMetadataBytes is >= 1 and <= 4 * 1024 * 1024,
            "Report maxMetadataBytes is outside the supported range.");
        Require(limits.MaxAudioBytes is >= 1 and <= 64 * 1024 * 1024,
            "Report maxAudioBytes is outside the supported range.");
        Require(limits.MaxInputCharacters is >= 1 and <= 10_000,
            "Report maxInputCharacters is outside the supported range.");
    }

    /// <summary>Validates nonnegative timings and the inspection-mode scope.</summary>
    private static void ValidateTimings(DiagnosticRunResult result)
    {
        var timings = result.Timings
            ?? throw new JsonException("Report timings are required.");

        RequireNonnegative(timings.TotalMs, "timings.totalMs");
        RequireNonnegative(timings.SpeechHeadersMs, "timings.speechHeadersMs");
        RequireNonnegative(timings.SpeechBodyMs, "timings.speechBodyMs");
        RequireNonnegative(timings.SpeechTotalMs, "timings.speechTotalMs");

        if (result.Mode != DiagnosticMode.Inspect)
            return;

        Require(result.Request is null && result.Audio is null
            && timings.SpeechHeadersMs is null && timings.SpeechBodyMs is null
            && timings.SpeechTotalMs is null,
            "Inspection reports must not contain speech or audio results.");
    }

    /// <summary>Validates the retained identity of a supported speech request.</summary>
    private static void ValidateRequest(DiagnosticRunResult result)
    {
        if (result.Mode == DiagnosticMode.Inspect)
            return;

        var request = result.Request
            ?? throw new JsonException("Active reports require a request snapshot.");

        RequireHash(request.Fingerprint, "request.fingerprint");
        RequireHash(request.InputSha256, "request.inputSha256");
        RequireText(request.Route, "request.route");
        RequireText(request.Model, "request.model");
        RequireText(request.Voice, "request.voice");
        Require(request.ResponseFormat == "wav" && !request.Stream,
            "Report requests must use non-streaming WAV synthesis.");
        Require(double.IsFinite(request.Speed) && request.Speed is >= 0.25 and <= 4.0,
            "Report request speed is outside the supported range.");
        Require(request.InputLength >= 1
            && request.InputLength <= result.Profile.Limits.MaxInputCharacters,
            "Report inputLength exceeds the configured input bounds.");
        Require(request.Route == result.Profile.Routes.Speech,
            "Report request route does not match its profile snapshot.");
    }

    /// <summary>Validates a bounded set of direct network observations.</summary>
    private static void ValidateNetwork(DiagnosticNetworkEvidence? network)
    {
        if (network is null)
            return;

        Require(network.Port is >= 1 and <= 65_535,
            "Report network port is outside the supported range.");

        foreach (var address in RequireList(network.ResolvedAddresses, 32, "resolvedAddresses"))
            RequireText(address, "resolvedAddresses");

        foreach (var attempt in RequireList(network.ConnectionAttempts, 32, "connectionAttempts"))
        {
            RequireText(attempt.Address, "connectionAttempts.address");
            RequireText(attempt.Code, "connectionAttempts.code", 128);
            RequireEnum(attempt.Status, "connectionAttempts.status");
            Require(attempt.Port == network.Port,
                "Report connection attempt port does not match its network port.");
            RequireNonnegative(attempt.DurationMs, "connectionAttempts.durationMs");
        }
    }

    /// <summary>Validates bounded metadata collections and finite server claims.</summary>
    private static void ValidateServer(DiagnosticServerEvidence? server)
    {
        if (server is null)
            return;

        RequireEnum(server.Readiness, "server.readiness");
        ValidateVoices(server.AdvertisedVoices);
        ValidateStatus(server.StatusSnapshot);

        var endpoints = new HashSet<MetadataEndpoint>();

        foreach (var observation in RequireList(server.Observations, 4, "server.observations"))
        {
            RequireEnum(observation.Endpoint, "observations.endpoint");
            RequireEnum(observation.State, "observations.state");
            Require(endpoints.Add(observation.Endpoint),
                "Report metadata endpoint observations must be unique.");
            RequireText(observation.Code, "observations.code", 128);
            RequireText(observation.Summary, "observations.summary");
            ValidateHttp(observation.Http);
            ValidateStatus(observation.StatusSnapshot);

            if (observation.Voices is not null)
                ValidateVoices(observation.Voices.Ids);

            foreach (var warning in RequireList(observation.Warnings, 32, "observations.warnings"))
            {
                RequireText(warning.Code, "warnings.code", 128);
                RequireText(warning.Summary, "warnings.summary");
            }
        }
    }

    /// <summary>Validates voice entries without changing redacted identifiers.</summary>
    private static void ValidateVoices(IReadOnlyList<string>? voices)
    {
        foreach (var voice in RequireList(voices, 512, "voices"))
            RequireText(voice, "voices");
    }

    /// <summary>Rejects non-finite values in selected server defaults.</summary>
    private static void ValidateStatus(MetadataStatusSnapshot? status)
    {
        if (status is null)
            return;

        foreach (var value in new[]
        {
            status.DefaultPitch, status.DefaultVolume, status.VolumeBoosterDb,
            status.DefaultTotalGainDb, status.RateLimit?.WindowSeconds
        })
        {
            RequireFinite(value, "server status");
        }
    }

    /// <summary>Validates audio measurements without opening a sample path.</summary>
    private static void ValidateAudio(DiagnosticAudioResult? audio)
    {
        if (audio is null)
            return;

        var inspection = audio.Inspection
            ?? throw new JsonException("Report audio inspection is required.");

        RequireEnum(inspection.ContainerState, "audio.containerState");
        RequireEnum(inspection.SignalState, "audio.signalState");
        RequireText(inspection.Code, "audio.code", 128);
        RequireText(inspection.Summary, "audio.summary");
        RequireHash(inspection.PayloadSha256, "audio.payloadSha256");
        RequireNonnegative(inspection.PayloadBytes, "audio.payloadBytes");
        RequireNonnegative(inspection.InspectionDurationMs, "audio.inspectionDurationMs");
        RequireNonnegative(inspection.DataBytes, "audio.dataBytes");
        RequireNonnegative(inspection.FrameCount, "audio.frameCount");
        RequireFinite(inspection.DurationSeconds, "audio.durationSeconds");
        RequireUnitInterval(inspection.PeakAbsolute, "audio.peakAbsolute");
        RequireUnitInterval(inspection.Rms, "audio.rms");
        RequireUnitInterval(inspection.NearFullScaleFraction, "audio.nearFullScaleFraction");
        RequireFinite(inspection.PeakDbfs, "audio.peakDbfs");
        RequireFinite(inspection.RmsDbfs, "audio.rmsDbfs");

        Require(inspection.DurationSeconds is null or >= 0,
            "Report audio duration must not be negative.");
        Require(audio.Retained
                ? inspection.ContainerState == WavContainerState.Valid && audio.SamplePath == "sample.wav"
                : audio.SamplePath is null,
            "Report audio retention and sample path are inconsistent.");

        foreach (var warning in RequireList(inspection.Warnings, 32, "audio.warnings"))
        {
            RequireText(warning.Code, "audio.warnings.code", 128);
            RequireText(warning.Summary, "audio.warnings.summary");
        }

        if (inspection.ContainerState != WavContainerState.Valid)
        {
            Require(inspection.SignalState == AudioSignalState.NotChecked,
                "Unsupported or invalid containers must not claim a verified signal.");
            return;
        }

        Require(inspection.FormatCode == 1 && inspection.BitsPerSample == 16
            && inspection.Channels is > 0 && inspection.SampleRateHz is > 0
            && inspection.DataBytes is > 0 && inspection.FrameCount is > 0
            && inspection.DurationSeconds is > 0
            && inspection.DataBytes <= inspection.PayloadBytes,
            "Report PCM16 audio measurements are incomplete or invalid.");

        var alignment = (long)inspection.Channels.GetValueOrDefault() * 2;
        var dataBytes = inspection.DataBytes.GetValueOrDefault();

        Require(inspection.BlockAlign == alignment
            && inspection.ByteRate == (long)inspection.SampleRateHz.GetValueOrDefault() * alignment
            && dataBytes % alignment == 0 && inspection.FrameCount == dataBytes / alignment,
            "Report PCM16 frame measurements are inconsistent.");

        var expectedDuration = (double)dataBytes / inspection.ByteRate.GetValueOrDefault();
        Require(Math.Abs(inspection.DurationSeconds.GetValueOrDefault() - expectedDuration)
            <= Math.Max(1e-9, expectedDuration * 1e-9),
            "Report audio duration does not match its PCM16 frame measurements.");

        Require(inspection.SignalState is AudioSignalState.Nonzero or AudioSignalState.Silent,
            "Supported audio must contain a signal inspection result.");
        Require(inspection.SignalState == AudioSignalState.Nonzero
                ? inspection.PeakAbsolute is > 0 && inspection.Rms is > 0
                : inspection.PeakAbsolute == 0 && inspection.Rms == 0,
            "Report signal state does not match its level measurements.");
    }

    /// <summary>Validates check identities, roles, and required evidence.</summary>
    private static void ValidateChecks(
        DiagnosticRunResult result,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var check in RequireList(result.Checks, 256, "checks"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireText(check.Id, "checks.id", 128);
            Require(ids.Add(check.Id), "Report check IDs must be unique.");
            RequireEnum(check.Role, "checks.role");
            RequireEnum(check.Status, "checks.status");
            RequireText(check.Code, "checks.code", 128);
            RequireText(check.Summary, "checks.summary");
            RequireNonnegative(check.DurationMs, "checks.durationMs");

            foreach (var suggestion in RequireList(check.Suggestions, 32, "checks.suggestions"))
                RequireText(suggestion, "checks.suggestions");

            if (check.Http is not null)
                ValidateHttp(check.Http);

            var expectedRole = GetKnownRole(result.Mode, check.Id);
            Require(expectedRole is null || check.Role == expectedRole,
                "Report check role does not match its operation mode.");

            if (result.Mode == DiagnosticMode.Inspect
                && check.Id is "speech.request" or "audio.container" or "audio.signal")
            {
                Require(check.Status == DiagnosticCheckStatus.Skipped
                    && check.Code == "not_requested" && check.Http is null,
                    "Inspection reports must mark speech checks as not requested.");
            }

            if (check.Status != DiagnosticCheckStatus.Passed)
                continue;

            Require(check.Id != "speech.request"
                || check.Http is { StatusCode: 200, BodyComplete: true, Method: "POST" },
                "A passed speech check requires a complete HTTP 200 response.");
            Require(check.Id != "audio.container"
                || result.Audio?.Inspection.ContainerState == WavContainerState.Valid,
                "A passed container check requires valid audio evidence.");
            Require(check.Id != "audio.signal"
                || result.Audio?.Inspection.SignalState == AudioSignalState.Nonzero,
                "A passed signal check requires nonzero audio evidence.");
            Require(check.Id != "service.identity" || result.Server?.Service is not null,
                "A passed identity check requires a reported service identifier.");
            Require(check.Id != "service.readiness"
                || result.Server?.Readiness == DiagnosticReadiness.Ready,
                "A passed readiness check requires a ready server claim.");
        }
    }

    /// <summary>Returns the fixed role of a known version 1 diagnostic check.</summary>
    private static DiagnosticCheckRole? GetKnownRole(DiagnosticMode mode, string id)
    {
        if (id is not ("dns.resolve" or "tcp.connect" or "service.health"
            or "service.status" or "service.voices" or "service.identity"
            or "service.readiness" or "speech.request" or "audio.container" or "audio.signal"))
        {
            return null;
        }

        var required = mode == DiagnosticMode.Check
            ? id is "speech.request" or "audio.container" or "audio.signal"
            : id is "service.identity" or "service.readiness";

        return required ? DiagnosticCheckRole.Required : DiagnosticCheckRole.Supporting;
    }

    /// <summary>Validates selected HTTP fields without following routes or redirects.</summary>
    private static void ValidateHttp(DiagnosticHttpEvidence? http)
    {
        if (http is null)
            throw new JsonException("Report HTTP evidence is required.");

        RequireText(http.Method, "http.method", 16);
        RequireText(http.Route, "http.route");
        Require(http.StatusCode is null or >= 100 and <= 999,
            "Report HTTP status is outside the supported range.");
        RequireNonnegative(http.DeclaredContentLength, "http.declaredContentLength");
        RequireNonnegative(http.BodyBytesRead, "http.bodyBytesRead");
        Require(http.ReportedSampleRateHz is null or > 0,
            "Report HTTP sample rate must be positive when present.");
    }

    /// <summary>Requires a non-null collection with bounded, non-null entries.</summary>
    private static IReadOnlyList<T> RequireList<T>(
        IReadOnlyList<T>? values,
        int maximum,
        string field) where T : class
    {
        Require(values is not null && values.Count <= maximum
            && values.All(value => value is not null),
            $"Report {field} must be a non-null list of at most {maximum} non-null entries.");

        return values;
    }

    /// <summary>Requires nonblank text within a documented character bound.</summary>
    private static void RequireText(
        string? value,
        string field,
        int maximum = MaximumStringCharacters)
    {
        Require(!string.IsNullOrWhiteSpace(value) && value.Length <= maximum,
            $"Report {field} must contain between 1 and {maximum} characters.");
    }

    /// <summary>Requires a hexadecimal SHA-256 digest without recomputing hidden input.</summary>
    private static void RequireHash(string? value, string field)
    {
        Require(value is { Length: 64 } && value.All(char.IsAsciiHexDigit),
            $"Report {field} must contain a SHA-256 hexadecimal digest.");
    }

    /// <summary>Rejects undefined enumeration values.</summary>
    private static void RequireEnum<T>(T value, string field) where T : struct, Enum
    {
        Require(Enum.IsDefined(value), $"Report {field} contains an unsupported value.");
    }

    /// <summary>Rejects negative client-observed counts and durations.</summary>
    private static void RequireNonnegative(long? value, string field)
    {
        Require(value is null or >= 0, $"Report {field} must not be negative.");
    }

    /// <summary>Rejects non-finite optional measurements.</summary>
    private static void RequireFinite(double? value, string field)
    {
        Require(value is null || double.IsFinite(value.Value),
            $"Report {field} must contain a finite number when present.");
    }

    /// <summary>Requires finite normalized signal measurements.</summary>
    private static void RequireUnitInterval(double? value, string field)
    {
        RequireFinite(value, field);
        Require(value is null or >= 0 and <= 1,
            $"Report {field} must be between 0 and 1 when present.");
    }

    /// <summary>Reports invalid imported data without echoing its contents.</summary>
    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
            throw new JsonException(message);
    }
}
