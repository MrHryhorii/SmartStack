using System.Text.Json;
using System.Text.Json.Serialization;
using Tsubaki.ServiceCheck.Core.Diagnostics;

namespace Tsubaki.ServiceCheck.Core.Comparison;

/// <summary>Builds allowlisted field comparisons from validated run snapshots.</summary>
internal static class RunComparisonFields
{
    private static readonly JsonSerializerOptions ValueOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
        }
    };

    /// <summary>Compares settings, claims, measurements, and selected HTTP evidence.</summary>
    internal static IReadOnlyList<FieldComparisonResult> Build(
        DiagnosticRunResult before,
        DiagnosticRunResult after,
        ComparisonScope scope,
        IReadOnlyList<CheckComparisonResult> checks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(checks);
        cancellationToken.ThrowIfCancellationRequested();

        var fields = new List<FieldComparisonResult>();

        AddFields(fields, true, cancellationToken,
            ("target.id", "Configured target ID", before.Target.Id, after.Target.Id, null),
            ("target.baseUrl", "Configured target URL", before.Target.BaseUrl, after.Target.BaseUrl, null),
            ("mode", "Diagnostic mode", before.Mode, after.Mode, null),
            ("tool.name", "Source utility name", before.Tool.Name, after.Tool.Name, null),
            ("tool.version", "Source utility version", before.Tool.Version, after.Tool.Version, null),
            ("profile.name", "Profile name", before.Profile.Name, after.Profile.Name, null),
            ("profile.fingerprint", "Profile fingerprint", before.Profile.Fingerprint, after.Profile.Fingerprint, null),
            ("profile.authenticationType", "Authentication type", before.Profile.AuthenticationType, after.Profile.AuthenticationType, null),
            ("profile.routes.health", "Health route", before.Profile.Routes.Health, after.Profile.Routes.Health, null),
            ("profile.routes.healthFallback", "Health fallback route", before.Profile.Routes.HealthFallback, after.Profile.Routes.HealthFallback, null),
            ("profile.routes.status", "Status route", before.Profile.Routes.Status, after.Profile.Routes.Status, null),
            ("profile.routes.voices", "Voices route", before.Profile.Routes.Voices, after.Profile.Routes.Voices, null),
            ("profile.routes.speech", "Speech route", before.Profile.Routes.Speech, after.Profile.Routes.Speech, null),
            ("profile.timeoutsMs.dns", "DNS timeout", before.Profile.TimeoutsMs.Dns, after.Profile.TimeoutsMs.Dns, "ms"),
            ("profile.timeoutsMs.tcp", "TCP timeout", before.Profile.TimeoutsMs.Tcp, after.Profile.TimeoutsMs.Tcp, "ms"),
            ("profile.timeoutsMs.metadata", "Metadata timeout", before.Profile.TimeoutsMs.Metadata, after.Profile.TimeoutsMs.Metadata, "ms"),
            ("profile.timeoutsMs.speech", "Speech timeout", before.Profile.TimeoutsMs.Speech, after.Profile.TimeoutsMs.Speech, "ms"),
            ("profile.timeoutsMs.run", "Run deadline", before.Profile.TimeoutsMs.Run, after.Profile.TimeoutsMs.Run, "ms"),
            ("profile.limits.maxMetadataBytes", "Metadata response limit", before.Profile.Limits.MaxMetadataBytes, after.Profile.Limits.MaxMetadataBytes, "bytes"),
            ("profile.limits.maxAudioBytes", "Audio response limit", before.Profile.Limits.MaxAudioBytes, after.Profile.Limits.MaxAudioBytes, "bytes"),
            ("profile.limits.maxInputCharacters", "Input length limit", before.Profile.Limits.MaxInputCharacters, after.Profile.Limits.MaxInputCharacters, "UTF-16 units"),
            ("coverage.network", "Network evidence available", before.Network is not null, after.Network is not null, null),
            ("coverage.server", "Server evidence available", before.Server is not null, after.Server is not null, null),
            ("coverage.request", "Speech request recorded", before.Request is not null, after.Request is not null, null),
            ("coverage.audio", "Audio evidence available", before.Audio is not null, after.Audio is not null, null));

        AddFields(fields, scope.OutcomesComparable, cancellationToken,
            ("executionState", "Execution state", before.ExecutionState, after.ExecutionState, null),
            ("outcome", "Recorded diagnostic outcome", before.Outcome, after.Outcome, null));

        AddFields(fields, true, cancellationToken,
            ("request.fingerprint", "Speech request fingerprint", before.Request?.Fingerprint, after.Request?.Fingerprint, null),
            ("request.route", "Speech request route", before.Request?.Route, after.Request?.Route, null),
            ("request.model", "Requested model label", before.Request?.Model, after.Request?.Model, null),
            ("request.voice", "Requested voice ID", before.Request?.Voice, after.Request?.Voice, null),
            ("request.responseFormat", "Requested audio format", before.Request?.ResponseFormat, after.Request?.ResponseFormat, null),
            ("request.speed", "Requested speed", before.Request?.Speed, after.Request?.Speed, null),
            ("request.stream", "Requested streaming", before.Request?.Stream, after.Request?.Stream, null),
            ("request.inputLength", "Input length", before.Request?.InputLength, after.Request?.InputLength, "UTF-16 units"),
            ("request.inputSha256", "Input SHA-256", before.Request?.InputSha256, after.Request?.InputSha256, null));

        AddServerFields(fields, before, after, scope.SameTargetId, cancellationToken);

        AddFields(fields, scope.SameTargetId, cancellationToken,
            ("network.port", "Direct TCP target port", before.Network?.Port, after.Network?.Port, null),
            ("network.resolvedAddresses", "Resolved addresses", Sort(before.Network?.ResolvedAddresses), Sort(after.Network?.ResolvedAddresses), null));

        AddFields(fields, scope.OutcomesComparable, cancellationToken,
            ("timings.totalMs", "Client-observed run duration", before.Timings.TotalMs, after.Timings.TotalMs, "ms"),
            ("timings.speechHeadersMs", "Client-observed speech headers duration", before.Timings.SpeechHeadersMs, after.Timings.SpeechHeadersMs, "ms"),
            ("timings.speechBodyMs", "Client-observed speech body duration", before.Timings.SpeechBodyMs, after.Timings.SpeechBodyMs, "ms"),
            ("timings.speechTotalMs", "Client-observed speech request duration", before.Timings.SpeechTotalMs, after.Timings.SpeechTotalMs, "ms"));

        AddAudioFields(fields, before, after,
            scope.OutcomesComparable && scope.SameRequestFingerprint == true,
            cancellationToken);
        AddCheckFields(fields, checks, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return fields.ToArray();
    }

    /// <summary>Compares selected claims while retaining explicit-null text limits.</summary>
    private static void AddServerFields(
        List<FieldComparisonResult> fields,
        DiagnosticRunResult before,
        DiagnosticRunResult after,
        bool comparable,
        CancellationToken cancellationToken)
    {
        var old = before.Server;
        var current = after.Server;
        var oldStatus = old?.StatusSnapshot;
        var currentStatus = current?.StatusSnapshot;

        AddFields(fields, comparable, cancellationToken,
            ("server.service", "Reported service", old?.Service, current?.Service, null),
            ("server.version", "Reported server version", old?.Version, current?.Version, null),
            ("server.readiness", "Reported readiness", old?.Readiness, current?.Readiness, null),
            ("server.identitySourceRoute", "Identity source route", old?.IdentitySourceRoute, current?.IdentitySourceRoute, null),
            ("server.versionSourceRoute", "Version source route", old?.VersionSourceRoute, current?.VersionSourceRoute, null),
            ("server.readinessSourceRoute", "Readiness source route", old?.ReadinessSourceRoute, current?.ReadinessSourceRoute, null),
            ("server.voicesSourceRoute", "Voice discovery source route", old?.VoicesSourceRoute, current?.VoicesSourceRoute, null),
            ("server.advertisedVoicesComplete", "Voice discovery complete", old?.AdvertisedVoicesComplete, current?.AdvertisedVoicesComplete, null),
            ("server.statusSourceRoute", "Status snapshot source route", old?.StatusSourceRoute, current?.StatusSourceRoute, null),
            ("server.statusSnapshot.baseVoiceDialect", "Reported base voice dialect", oldStatus?.BaseVoiceDialect, currentStatus?.BaseVoiceDialect, null),
            ("server.statusSnapshot.sampleRateHz", "Reported model sample rate", oldStatus?.SampleRateHz, currentStatus?.SampleRateHz, "Hz"),
            ("server.statusSnapshot.voiceCloningEnabled", "Reported voice cloning enabled", oldStatus?.VoiceCloningEnabled, currentStatus?.VoiceCloningEnabled, null),
            ("server.statusSnapshot.voiceCloningAvailable", "Reported voice cloning available", oldStatus?.VoiceCloningAvailable, currentStatus?.VoiceCloningAvailable, null),
            ("server.statusSnapshot.wavAvailable", "Reported WAV support", oldStatus?.WavAvailable, currentStatus?.WavAvailable, null),
            ("server.statusSnapshot.defaultSampleRate", "Reported configured sample rate", oldStatus?.DefaultSampleRate, currentStatus?.DefaultSampleRate, "Hz"),
            ("server.statusSnapshot.defaultPitch", "Reported default pitch", oldStatus?.DefaultPitch, currentStatus?.DefaultPitch, null),
            ("server.statusSnapshot.defaultVolume", "Reported default volume", oldStatus?.DefaultVolume, currentStatus?.DefaultVolume, null),
            ("server.statusSnapshot.volumeBoosterDb", "Reported volume boost", oldStatus?.VolumeBoosterDb, currentStatus?.VolumeBoosterDb, "dB"),
            ("server.statusSnapshot.defaultTotalGainDb", "Reported total default gain", oldStatus?.DefaultTotalGainDb, currentStatus?.DefaultTotalGainDb, "dB"),
            ("server.statusSnapshot.textLimitKnown", "Text limit claim available", oldStatus?.TextLimitKnown, currentStatus?.TextLimitKnown, null),
            ("server.statusSnapshot.rateLimit.permitLimit", "Reported rate-limit permits", oldStatus?.RateLimit?.PermitLimit, currentStatus?.RateLimit?.PermitLimit, null),
            ("server.statusSnapshot.rateLimit.windowSeconds", "Reported rate-limit window", oldStatus?.RateLimit?.WindowSeconds, currentStatus?.RateLimit?.WindowSeconds, "s"),
            ("server.statusSnapshot.rateLimit.queueLimit", "Reported queue limit", oldStatus?.RateLimit?.QueueLimit, currentStatus?.RateLimit?.QueueLimit, null));

        var oldTextLimitKnown = oldStatus?.TextLimitKnown == true;
        var currentTextLimitKnown = currentStatus?.TextLimitKnown == true;

        Add(fields, "server.statusSnapshot.maxTextLength", "Reported text limit",
            oldTextLimitKnown ? oldStatus?.MaxTextLength : null,
            currentTextLimitKnown ? currentStatus?.MaxTextLength : null,
            comparable, "UTF-16 units", oldTextLimitKnown, currentTextLimitKnown);

        Add(fields, "server.advertisedVoices", "Advertised voice IDs",
            GetVoices(old), GetVoices(current),
            comparable && old?.AdvertisedVoicesComplete == true
                && current?.AdvertisedVoicesComplete == true);
    }

    /// <summary>Compares audio observations within the same active request context.</summary>
    private static void AddAudioFields(
        List<FieldComparisonResult> fields,
        DiagnosticRunResult before,
        DiagnosticRunResult after,
        bool comparable,
        CancellationToken cancellationToken)
    {
        var old = before.Audio?.Inspection;
        var current = after.Audio?.Inspection;

        AddFields(fields, comparable, cancellationToken,
            ("audio.inspection.containerState", "Audio container state", old?.ContainerState, current?.ContainerState, null),
            ("audio.inspection.signalState", "Audio signal state", old?.SignalState, current?.SignalState, null),
            ("audio.inspection.formatCode", "WAV format code", old?.FormatCode, current?.FormatCode, null),
            ("audio.inspection.channels", "Audio channels", old?.Channels, current?.Channels, null),
            ("audio.inspection.sampleRateHz", "Verified audio sample rate", old?.SampleRateHz, current?.SampleRateHz, "Hz"),
            ("audio.inspection.bitsPerSample", "Bits per audio sample", old?.BitsPerSample, current?.BitsPerSample, "bits"),
            ("audio.inspection.blockAlign", "Audio frame size", old?.BlockAlign, current?.BlockAlign, "bytes"),
            ("audio.inspection.byteRate", "Audio data byte rate", old?.ByteRate, current?.ByteRate, "bytes/s"),
            ("audio.inspection.payloadBytes", "Audio response size", old?.PayloadBytes, current?.PayloadBytes, "bytes"),
            ("audio.inspection.dataBytes", "PCM data size", old?.DataBytes, current?.DataBytes, "bytes"),
            ("audio.inspection.frameCount", "PCM frame count", old?.FrameCount, current?.FrameCount, "frames"),
            ("audio.inspection.durationSeconds", "Verified audio duration", old?.DurationSeconds, current?.DurationSeconds, "s"),
            ("audio.inspection.peakAbsolute", "Normalized peak level", old?.PeakAbsolute, current?.PeakAbsolute, null),
            ("audio.inspection.rms", "Normalized RMS level", old?.Rms, current?.Rms, null),
            ("audio.inspection.peakDbfs", "Peak level", old?.PeakDbfs, current?.PeakDbfs, "dBFS"),
            ("audio.inspection.rmsDbfs", "RMS level", old?.RmsDbfs, current?.RmsDbfs, "dBFS"),
            ("audio.inspection.nearFullScaleFraction", "Near-full-scale sample fraction", old?.NearFullScaleFraction, current?.NearFullScaleFraction, null),
            ("audio.inspection.payloadSha256", "Audio artifact SHA-256", old?.PayloadSha256, current?.PayloadSha256, null));

        AddFields(fields, true, cancellationToken,
            ("audio.retained", "Audio retained in source run", before.Audio?.Retained, after.Audio?.Retained, null));
    }

    /// <summary>Pairs check durations and selected HTTP fields without comparing error wording.</summary>
    private static void AddCheckFields(
        List<FieldComparisonResult> fields,
        IReadOnlyList<CheckComparisonResult> checks,
        CancellationToken cancellationToken)
    {
        foreach (var check in checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = $"checks[{JsonSerializer.Serialize(check.Id)}]";
            var comparable = check.Change != ComparisonChangeKind.NotComparable;

            Add(fields, prefix + ".durationMs", check.Id + " observed duration",
                check.Before?.DurationMs, check.After?.DurationMs, comparable, "ms");

            var old = check.Before?.Http;
            var current = check.After?.Http;

            if (old is null && current is null)
                continue;

            AddFields(fields, comparable, cancellationToken,
                (prefix + ".http.method", check.Id + " HTTP method", old?.Method, current?.Method, null),
                (prefix + ".http.route", check.Id + " HTTP route", old?.Route, current?.Route, null),
                (prefix + ".http.statusCode", check.Id + " HTTP status", old?.StatusCode, current?.StatusCode, null),
                (prefix + ".http.mediaType", check.Id + " response media type", old?.MediaType, current?.MediaType, null),
                (prefix + ".http.declaredContentLength", check.Id + " declared response size", old?.DeclaredContentLength, current?.DeclaredContentLength, "bytes"),
                (prefix + ".http.bodyBytesRead", check.Id + " response bytes read", old?.BodyBytesRead, current?.BodyBytesRead, "bytes"),
                (prefix + ".http.bodyComplete", check.Id + " response body complete", old?.BodyComplete, current?.BodyComplete, null),
                (prefix + ".http.reportedSampleRateHz", check.Id + " response-header sample rate", old?.ReportedSampleRateHz, current?.ReportedSampleRateHz, "Hz"),
                (prefix + ".http.redirectLocation", check.Id + " reported redirect", old?.RedirectLocation, current?.RedirectLocation, null),
                (prefix + ".http.exceptionType", check.Id + " exception category", old?.ExceptionType, current?.ExceptionType, null));
        }
    }

    /// <summary>Builds field rows from a fixed list of named value pairs.</summary>
    private static void AddFields(
        List<FieldComparisonResult> fields,
        bool comparable,
        CancellationToken cancellationToken,
        params (string Path, string Label, object? Before, object? After, string? Unit)[] pairs)
    {
        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(fields, pair.Path, pair.Label, pair.Before, pair.After, comparable, pair.Unit);
        }
    }

    /// <summary>Preserves unavailable values separately from explicit JSON-null claims.</summary>
    private static void Add(
        List<FieldComparisonResult> fields,
        string path,
        string label,
        object? before,
        object? after,
        bool comparable = true,
        string? unit = null,
        bool beforeNullIsKnown = false,
        bool afterNullIsKnown = false)
    {
        var beforeKnown = before is not null || beforeNullIsKnown;
        var afterKnown = after is not null || afterNullIsKnown;

        if (!beforeKnown && !afterKnown)
            return;

        var change = !beforeKnown ? ComparisonChangeKind.BeforeMissing
            : !afterKnown ? ComparisonChangeKind.AfterMissing
            : !comparable ? ComparisonChangeKind.NotComparable
            : Equal(path, before, after) ? ComparisonChangeKind.Unchanged
            : ComparisonChangeKind.Changed;

        fields.Add(new FieldComparisonResult
        {
            Path = path,
            Label = label,
            Change = change,
            Before = beforeKnown ? JsonSerializer.SerializeToElement(before, ValueOptions) : null,
            After = afterKnown ? JsonSerializer.SerializeToElement(after, ValueOptions) : null,
            Unit = unit
        });
    }

    /// <summary>Compares scalar values and normalized string lists.</summary>
    private static bool Equal(string path, object? before, object? after)
    {
        if (before is string[] old && after is string[] current)
            return old.SequenceEqual(current, StringComparer.Ordinal);

        if (before is string left && after is string right
            && (path.EndsWith("fingerprint", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("sha256", StringComparison.OrdinalIgnoreCase)))
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        return object.Equals(before, after);
    }

    /// <summary>Sorts a retained list without modifying the source snapshot.</summary>
    private static string[]? Sort(IReadOnlyList<string>? values)
    {
        return values?.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Returns known voice data while preserving partial-discovery limits.</summary>
    private static string[]? GetVoices(DiagnosticServerEvidence? server)
    {
        if (server is null || (!server.AdvertisedVoicesComplete
            && server.VoicesSourceRoute is null && server.AdvertisedVoices.Count == 0))
        {
            return null;
        }

        return Sort(server.AdvertisedVoices);
    }
}
