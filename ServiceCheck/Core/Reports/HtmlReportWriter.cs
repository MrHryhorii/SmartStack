using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Tsubaki.ServiceCheck.Core.Audio;
using Tsubaki.ServiceCheck.Core.Diagnostics;

namespace Tsubaki.ServiceCheck.Core.Reports;

/// <summary>
/// Writes a self-contained diagnostic report with optional WAV playback.
/// </summary>
public static class HtmlReportWriter
{
    private const int Base64ChunkBytes = 24 * 1024;

    private const string DocumentStart = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta http-equiv="Content-Security-Policy"
              content="default-src 'none'; style-src 'unsafe-inline'; media-src data:; base-uri 'none'; form-action 'none'">
        <title>Tsubaki Service Check</title>
        <style>
        :root {
            color-scheme: light;
            --canvas: #F5F3F1;
            --surface: #FFFFFF;
            --text: #20212A;
            --secondary: #626571;
            --accent: #8A3158;
            --pass: #22643B;
            --warning: #8B5A00;
            --failure: #A3283B;
        }
        * { box-sizing: border-box; }
        body {
            margin: 0;
            background: var(--canvas);
            color: var(--text);
            font: 16px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif;
        }
        main { max-width: 1120px; margin: 0 auto; padding: 32px 16px; }
        header { margin-bottom: 24px; }
        section {
            margin: 16px 0;
            padding: 24px;
            background: var(--surface);
            border: 1px solid #DEDBDD;
            border-radius: 8px;
        }
        h1 { margin: 8px 0; font-size: 2rem; }
        h2 { margin: 0 0 16px; font-size: 1.25rem; }
        h3 { margin: 24px 0 8px; font-size: 1rem; }
        p { margin: 8px 0; }
        .eyebrow { color: var(--accent); font-weight: 700; }
        .muted { color: var(--secondary); }
        .pass { color: var(--pass); font-weight: 700; }
        .warning { color: var(--warning); font-weight: 700; }
        .failure { color: var(--failure); font-weight: 700; }
        .metrics {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(176px, 1fr));
            gap: 16px;
            margin: 16px 0 0;
        }
        .metrics > div {
            padding: 16px;
            background: var(--canvas);
            border-radius: 8px;
        }
        dt { color: var(--secondary); font-size: 0.875rem; }
        dd { margin: 8px 0 0; font-weight: 600; }
        .table-wrap { overflow-x: auto; }
        table { width: 100%; border-collapse: collapse; }
        table.checks { min-width: 720px; }
        caption { padding-bottom: 8px; text-align: left; color: var(--secondary); }
        th, td {
            padding: 8px 16px;
            border-bottom: 1px solid #DEDBDD;
            text-align: left;
            vertical-align: top;
        }
        th { font-weight: 600; }
        td, dd { font-variant-numeric: tabular-nums; }
        h1, h2, h3, code, pre, th, td, dd, p, li { overflow-wrap: anywhere; }
        pre {
            padding: 16px;
            background: var(--canvas);
            white-space: pre-wrap;
            font-size: 0.875rem;
        }
        .findings { padding-left: 24px; }
        .findings > li { margin-bottom: 16px; }
        details { margin-top: 16px; }
        summary { cursor: pointer; font-weight: 600; }
        :focus-visible { outline: 3px solid var(--accent); outline-offset: 3px; }
        audio { display: block; width: 100%; margin-top: 16px; }
        footer { padding: 16px 0; color: var(--secondary); font-size: 0.875rem; }
        @media (max-width: 640px) {
            main { padding: 16px 8px; }
            section { padding: 16px; }
            h1 { font-size: 1.5rem; }
        }
        @media print {
            body { background: white; color: black; }
            main { max-width: none; padding: 0; }
            section { border: 0; padding: 8px 0; margin: 8px 0; }
            h1, h2, h3 { break-after: avoid; }
            audio { display: none; }
            .table-wrap { overflow: visible; }
            table.checks { min-width: 0; }
            th, td { padding: 4px 8px; font-size: 10pt; }
        }
        </style>
        </head>
        <body>
        <main>
        """;

    /// <summary>
    /// Writes UTF-8 HTML while leaving the destination stream open.
    /// </summary>
    public static async Task WriteAsync(
        Stream destination,
        DiagnosticRunResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(result);

        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                "The report destination must be writable.",
                nameof(destination));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var canonicalJson = Encoding.UTF8.GetString(ReportJson.Serialize(result));
        var playbackPayload = GetPlaybackPayload(result);
        var document = new StringBuilder(DocumentStart);

        AppendHeader(document, result);
        AppendOverview(document, result);
        AppendFindings(document, result);
        AppendChecks(document, result);
        AppendServer(document, result);
        AppendAudio(document, result);

        await using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 16384,
            leaveOpen: true);

        await writer.WriteAsync(
            document.ToString().AsMemory(),
            cancellationToken);

        if (playbackPayload is not null)
        {
            await WritePlaybackAsync(writer, playbackPayload, cancellationToken);
        }
        else if (result.Audio is not null)
        {
            var message = result.Audio.Retained
                ? "<p>Retained audio bytes are unavailable for embedded playback.</p>"
                : "<p>Audio was not retained.</p>";

            await writer.WriteAsync(message.AsMemory(), cancellationToken);
        }

        var ending = $$"""
            </section>
            <section aria-labelledby="technical-title">
                <h2 id="technical-title">Technical details</h2>
                <details>
                    <summary>Canonical report data</summary>
                    <pre>{{Encode(canonicalJson)}}</pre>
                </details>
            </section>
            <footer>
                <h2>Scope</h2>
                <p>{{Encode(GetScope(result))}}</p>
                <p>
                    Signal warnings use diagnostic heuristics: RMS below -50 dBFS,
                    or at least 1% of samples with absolute level at or above 0.999.
                </p>
            </footer>
            </main>
            </body>
            </html>
            """;

        await writer.WriteAsync(ending.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>Appends target identity and client timestamps.</summary>
    private static void AppendHeader(
        StringBuilder document,
        DiagnosticRunResult result)
    {
        document.Append($$"""
            <header>
                <p class="eyebrow">{{Encode(result.Tool.Name)}}</p>
                <h1>{{Encode(result.Profile.Name)}}</h1>
                <p><code>{{Encode(result.Target.BaseUrl)}}</code></p>
                <p class="muted">
                    Target: {{Encode(result.Target.Id)}}.
                    Mode: {{Encode(result.Mode.ToString())}}.
                    Tool version: {{Encode(result.Tool.Version)}}.
                </p>
                <p class="muted">
                    Started: {{Encode(FormatUtc(result.StartedAtUtc))}}.
                    Finished: {{Encode(FormatUtc(result.FinishedAtUtc))}}.
                </p>
                <p class="muted">
                    Run: <code>{{Encode(result.RunId)}}</code>.
                    Reported server version: {{Encode(result.Server?.Version ?? "Unknown")}}.
                </p>
            </header>
            """);
    }

    /// <summary>Appends the overall result and key observations.</summary>
    private static void AppendOverview(
        StringBuilder document,
        DiagnosticRunResult result)
    {
        var outcomeClass = result.Outcome switch
        {
            DiagnosticOutcome.Passed => "pass",
            DiagnosticOutcome.Failed => "failure",
            _ => "warning"
        };

        var outcomeLabel = result.Outcome switch
        {
            DiagnosticOutcome.Passed => "Passed",
            DiagnosticOutcome.PassedWithWarnings => "Passed with warnings",
            DiagnosticOutcome.Failed => "Failed",
            _ => "Incomplete"
        };

        document.Append($$"""
            <section aria-labelledby="outcome-title">
                <h2 id="outcome-title">
                    Outcome: <span class="{{outcomeClass}}">{{outcomeLabel}}</span>
                </h2>
                <p>{{Encode(GetSummary(result))}}</p>
                <p class="muted">
                    Execution: {{Encode(GetExecutionLabel(result.ExecutionState))}}.
                    Total client-observed duration: {{Encode(Number(result.Timings.TotalMs, "0", " ms"))}}.
                </p>
                <dl class="metrics">
            """);

        AppendMetric(document, "Direct TCP observation", GetConnectionLabel(result));
        AppendMetric(document, "Server-reported readiness", GetReadinessLabel(result));
        AppendMetric(document, "Speech HTTP status", GetSpeechHttpLabel(result));

        var duration = result.Audio?.Inspection.ContainerState ==
            WavContainerState.Valid
                ? Number(result.Audio.Inspection.DurationSeconds, "0.######", " s")
                : "Not verified";

        AppendMetric(document, "Audio duration", duration);

        document.Append("</dl></section>");
    }

    /// <summary>Places required failures before supporting findings.</summary>
    private static void AppendFindings(
        StringBuilder document,
        DiagnosticRunResult result)
    {
        var findings = result.Checks
            .Where(check => check.Status is
                DiagnosticCheckStatus.Failed or
                DiagnosticCheckStatus.Error or
                DiagnosticCheckStatus.Warning)
            .OrderBy(GetFindingPriority)
            .ToArray();

        var skippedCount = result.Checks.Count(
            check => check.Status == DiagnosticCheckStatus.Skipped);

        document.Append(
            "<section aria-labelledby=\"findings-title\">" +
            "<h2 id=\"findings-title\">Findings and coverage</h2>");

        if (findings.Length > 0)
        {
            document.Append("<ul class=\"findings\">");

            foreach (var check in findings)
            {
                document.Append($$"""
                    <li>
                        <strong class="{{GetStatusClass(check.Status)}}">
                            {{GetStatusLabel(check.Status)}}
                        </strong>
                        <code>{{Encode(check.Id)}}</code>
                        <span class="muted">({{Encode(check.Role.ToString())}})</span>
                        <p>{{Encode(check.Summary)}}</p>
                    """);

                if (check.Http?.ErrorExcerpt is { } excerpt)
                {
                    document.Append("<pre>")
                        .Append(Encode(excerpt))
                        .Append("</pre>");
                }

                if (check.Suggestions.Count > 0)
                {
                    document.Append("<ul>");

                    foreach (var suggestion in check.Suggestions)
                    {
                        document.Append("<li>")
                            .Append(Encode(suggestion))
                            .Append("</li>");
                    }

                    document.Append("</ul>");
                }

                document.Append("</li>");
            }

            document.Append("</ul>");
        }

        if (skippedCount > 0)
        {
            document.Append("<p>Skipped checks: ")
                .Append(Number(skippedCount, "0"))
                .Append(". Review their scope and reasons in the check table.</p>");
        }

        if (findings.Length == 0 && skippedCount == 0)
        {
            document.Append("<p>No findings were recorded.</p>");
        }

        document.Append("</section>");
    }

    /// <summary>Appends selected server claims with their metadata sources and coverage.</summary>
    private static void AppendServer(StringBuilder document, DiagnosticRunResult result)
    {
        document.Append("""
            <section aria-labelledby="server-title">
            <h2 id="server-title">Reported server metadata</h2>
            <p>Server claims and configured defaults are separate from verified speech and audio results.</p>
            """);

        if (result.Server is not { } server)
        {
            document.Append("<p>No server metadata was recorded.</p></section>");
            return;
        }

        document.Append("""
            <div class="table-wrap" tabindex="0" role="region" aria-label="Reported server identity">
            <table><caption>Selected identity and readiness claims</caption><tbody>
            """);
        AppendRow(document, "Reported service", server.Service ?? "Not reported");
        AppendRow(document, "Reported version", server.Version ?? "Not reported");
        AppendRow(document, "Reported synthesis readiness", GetReadinessLabel(result));
        AppendRow(document, "Identity source route", server.IdentitySourceRoute ?? "Not reported");
        AppendRow(document, "Version source route", server.VersionSourceRoute ?? "Not reported");
        AppendRow(document, "Readiness source route", server.ReadinessSourceRoute ?? "Not reported");
        AppendRow(document, "Client readiness observation time", server.ReadinessObservedAtUtc is { } observed
            ? FormatUtc(observed) : "Not reported");
        document.Append("</tbody></table></div>");

        AppendServerSettings(document, server);
        document.Append("<h3>Advertised voices</h3><p>Source route: <code>")
            .Append(Encode(server.VoicesSourceRoute ?? "Not reported")).Append("</code>.</p>")
            .Append(server.AdvertisedVoicesComplete
                ? "<p>A complete advertised voice list was received.</p>"
                : "<p>Voice discovery was unavailable or incomplete. A missing ID does not establish that a voice is unavailable.</p>");

        if (server.AdvertisedVoices.Count == 0)
        {
            document.Append(server.AdvertisedVoicesComplete
                ? "<p>The reported list is empty.</p>"
                : "<p>No voice IDs were retained.</p>");
            document.Append("</section>");
            return;
        }

        document.Append("<details><summary>Retained voice IDs: ")
            .Append(Number(server.AdvertisedVoices.Count, "0")).Append("</summary><ul>");
        foreach (var voice in server.AdvertisedVoices)
            document.Append("<li><code>").Append(Encode(voice)).Append("</code></li>");
        document.Append("</ul></details></section>");
    }

    /// <summary>Appends allowlisted capabilities and defaults while preserving unknown claims.</summary>
    private static void AppendServerSettings(StringBuilder document, DiagnosticServerEvidence server)
    {
        document.Append("<h3>Reported capabilities and defaults</h3>");
        if (server.StatusSnapshot is not { } status)
        {
            document.Append("<p>Capabilities and defaults were not reported.</p>");
            return;
        }

        document.Append("<p>Source route: <code>").Append(Encode(server.StatusSourceRoute ?? "Not reported"))
            .Append("</code>.</p>")
            .Append("""
                <div class="table-wrap" tabindex="0" role="region" aria-label="Reported capabilities and defaults">
                <table><caption>Configured values reported by the server</caption><tbody>
                """);
        AppendRow(document, "Base voice dialect", status.BaseVoiceDialect ?? "Not reported");
        AppendRow(document, "Model sample rate", ReportedNumber(status.SampleRateHz, "0", " Hz"));
        AppendRow(document, "Voice cloning enabled", ReportedFlag(status.VoiceCloningEnabled));
        AppendRow(document, "Voice cloning available", ReportedFlag(status.VoiceCloningAvailable));
        AppendRow(document, "WAV support", ReportedFlag(status.WavAvailable));
        AppendRow(document, "Configured sample rate", status.DefaultSampleRate == 0
            ? "0 (no sample rate override)" : ReportedNumber(status.DefaultSampleRate, "0", " Hz"));
        AppendRow(document, "Default pitch", ReportedNumber(status.DefaultPitch));
        AppendRow(document, "Default volume", ReportedNumber(status.DefaultVolume));
        AppendRow(document, "Volume booster", ReportedNumber(status.VolumeBoosterDb, "0.###", " dB"));
        AppendRow(document, "Default total gain", ReportedNumber(status.DefaultTotalGainDb, "0.###", " dB"));
        AppendRow(document, "Text length limit", !status.TextLimitKnown ? "Not reported"
            : status.MaxTextLength is { } limit ? Number(limit, "0", " characters")
            : "No configured limit (explicit null)");
        AppendRow(document, "Rate limit permits", ReportedNumber(status.RateLimit?.PermitLimit, "0"));
        AppendRow(document, "Rate limit window", ReportedNumber(status.RateLimit?.WindowSeconds, "0.###", " s"));
        AppendRow(document, "Rate limit queue", ReportedNumber(status.RateLimit?.QueueLimit, "0"));
        document.Append("</tbody></table></div>");
    }

    /// <summary>Appends every recorded check in execution order.</summary>
    private static void AppendChecks(
        StringBuilder document,
        DiagnosticRunResult result)
    {
        document.Append("""
            <section aria-labelledby="checks-title">
            <h2 id="checks-title">Checks</h2>
            <div class="table-wrap" tabindex="0" role="region" aria-label="Diagnostic checks">
            <table class="checks">
            <caption>Ordered diagnostic observations</caption>
            <thead>
            <tr>
                <th scope="col">Check</th>
                <th scope="col">Role</th>
                <th scope="col">Status</th>
                <th scope="col">Elapsed</th>
                <th scope="col">Result</th>
            </tr>
            </thead>
            <tbody>
            """);

        foreach (var check in result.Checks)
        {
            document.Append($$"""
                <tr>
                    <th scope="row"><code>{{Encode(check.Id)}}</code></th>
                    <td>{{Encode(check.Role.ToString())}}</td>
                    <td class="{{GetStatusClass(check.Status)}}">
                        {{GetStatusLabel(check.Status)}}
                    </td>
                    <td>{{Encode(Number(check.DurationMs, "0", " ms"))}}</td>
                    <td>
                        {{Encode(check.Summary)}}
                        <br><code>{{Encode(check.Code)}}</code>
                    </td>
                </tr>
                """);
        }

        document.Append("</tbody></table></div></section>");
    }

    /// <summary>Opens the audio section and appends available measurements.</summary>
    private static void AppendAudio(
        StringBuilder document,
        DiagnosticRunResult result)
    {
        document.Append(
            "<section aria-labelledby=\"audio-title\">" +
            "<h2 id=\"audio-title\">Audio evidence</h2>");

        if (result.Mode == DiagnosticMode.Inspect)
        {
            document.Append("<p>Speech generation and audio inspection were not requested in inspection mode.</p>");
            return;
        }

        if (result.Audio is not { } audio)
        {
            document.Append("<p>Audio inspection was not completed.</p>");
            return;
        }

        var inspection = audio.Inspection;

        document.Append("""
            <div class="table-wrap" tabindex="0" role="region" aria-label="Audio measurements">
            <table>
            <caption>Received payload and observed audio measurements</caption>
            <tbody>
            """);

        AppendRow(document, "Container", inspection.ContainerState.ToString());
        AppendRow(document, "Signal", inspection.SignalState.ToString());
        AppendRow(document, "Format code", Number(inspection.FormatCode, "0"));
        AppendRow(document, "Bits per sample", Number(inspection.BitsPerSample, "0"));
        AppendRow(document, "Sample rate", Number(inspection.SampleRateHz, "0", " Hz"));
        AppendRow(document, "Channels", Number(inspection.Channels, "0"));
        AppendRow(document, "Frames", Number(inspection.FrameCount, "0"));
        AppendRow(document, "Duration", Number(inspection.DurationSeconds, "0.######", " s"));
        AppendRow(document, "Payload size", Number(inspection.PayloadBytes, "N0", " bytes"));
        AppendRow(document, "Audio data size", Number(inspection.DataBytes, "N0", " bytes"));
        AppendRow(document, "Peak absolute level", Number(inspection.PeakAbsolute, "0.######"));
        AppendRow(document, "RMS", Number(inspection.Rms, "0.######"));
        AppendRow(document, "Peak level", Decibels(inspection.PeakDbfs, inspection.SignalState));
        AppendRow(document, "RMS level", Decibels(inspection.RmsDbfs, inspection.SignalState));

        AppendRow(
            document,
            "Samples near full scale",
            Number(inspection.NearFullScaleFraction * 100, "0.###", "%"));

        AppendRow(
            document,
            "Inspection duration",
            Number(inspection.InspectionDurationMs, "0", " ms"));

        AppendRow(document, "Payload SHA-256", inspection.PayloadSha256);
        AppendRow(document, "Sample file", audio.SamplePath ?? "Not retained");

        document.Append("</tbody></table></div>");
    }

    /// <summary>Returns retained supported audio eligible for embedded playback.</summary>
    private static byte[]? GetPlaybackPayload(DiagnosticRunResult result)
    {
        if (result.Audio is not { Retained: true } audio ||
            audio.Inspection.ContainerState != WavContainerState.Valid ||
            result.AudioPayload is not { Length: > 0 } payload)
        {
            return null;
        }

        if (payload.Length > result.Profile.Limits.MaxAudioBytes)
        {
            throw new ArgumentException(
                "Retained audio exceeds the configured report limit.",
                nameof(result));
        }

        return payload;
    }

    /// <summary>Streams base64 audio using complete three-byte groups.</summary>
    private static async Task WritePlaybackAsync(
        StreamWriter writer,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        const string start = """
            <audio controls preload="none"
                   aria-label="Retained speech sample"
                   src="data:audio/wav;base64,
            """;

        await writer.WriteAsync(start.TrimEnd().AsMemory(), cancellationToken);

        for (var offset = 0; offset < payload.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(Base64ChunkBytes, payload.Length - offset);
            var encoded = Convert.ToBase64String(payload, offset, count);

            await writer.WriteAsync(encoded.AsMemory(), cancellationToken);
            offset += count;
        }

        const string end = """
            ">Your browser does not support WAV playback.</audio>
            """;

        await writer.WriteAsync(end.AsMemory(), cancellationToken);
    }

    /// <summary>Describes the strongest verified result without a health score.</summary>
    private static string GetSummary(DiagnosticRunResult result)
    {
        if (result.ExecutionState != DiagnosticExecutionState.Completed)
        {
            var message = result.ExecutionState switch
            {
                DiagnosticExecutionState.Cancelled =>
                    "The run was cancelled. Available observations are preserved.",
                DiagnosticExecutionState.TimedOut =>
                    "The run exceeded its allowed time. Available observations are preserved.",
                _ => "The utility could not complete the diagnostic run."
            };

            return result.Mode == DiagnosticMode.Inspect
                ? message + " Speech generation was not tested."
                : message;
        }

        var primary = result.Checks.FirstOrDefault(check =>
            check.Role == DiagnosticCheckRole.Required &&
            check.Status is DiagnosticCheckStatus.Failed or DiagnosticCheckStatus.Error);

        if (result.Mode == DiagnosticMode.Inspect)
        {
            var message = result.Outcome switch
            {
                DiagnosticOutcome.Passed or DiagnosticOutcome.PassedWithWarnings =>
                    "The server reported the expected identity and that synthesis was ready.",
                DiagnosticOutcome.Failed => primary?.Summary ?? "Inspection failed a required metadata check.",
                _ => "Inspection could not establish the required metadata."
            };
            return message + " Speech generation was not tested.";
        }

        if (result.Outcome is
            DiagnosticOutcome.Passed or DiagnosticOutcome.PassedWithWarnings)
        {
            return "Speech generation verified from this client.";
        }

        return primary?.Summary ?? "Speech generation could not be verified.";
    }

    /// <summary>States the limits of the requested operation.</summary>
    private static string GetScope(DiagnosticRunResult result)
    {
        if (result.Mode == DiagnosticMode.Inspect)
        {
            return "Speech generation was not tested. Server metadata represents reported observations.";
        }

        return "This check tests one speech request and supported PCM16 audio. " +
            "A nonzero waveform does not establish intelligibility, pronunciation quality, " +
            "language correctness, or speaker identity. Reported readiness and direct " +
            "connectivity observations are separate from audio verification.";
    }

    /// <summary>Returns a readable direct TCP observation.</summary>
    private static string GetConnectionLabel(DiagnosticRunResult result)
    {
        var check = result.Checks.FirstOrDefault(
            item => item.Id == "tcp.connect");

        return check?.Status switch
        {
            DiagnosticCheckStatus.Passed => "Passed",
            DiagnosticCheckStatus.Failed => "Failed",
            DiagnosticCheckStatus.Warning => "Warning",
            DiagnosticCheckStatus.Error => "Unavailable",
            _ => "Not tested"
        };
    }

    /// <summary>Returns readiness exclusively from server metadata.</summary>
    private static string GetReadinessLabel(DiagnosticRunResult result)
    {
        return result.Server?.Readiness switch
        {
            DiagnosticReadiness.Ready => "Ready",
            DiagnosticReadiness.NotReady => "Not ready",
            _ => "Unknown"
        };
    }

    /// <summary>Returns the observed speech response status.</summary>
    private static string GetSpeechHttpLabel(DiagnosticRunResult result)
    {
        if (result.Mode == DiagnosticMode.Inspect)
        {
            return "Not tested";
        }

        var http = result.Checks.FirstOrDefault(
            check => check.Id == "speech.request")?.Http;

        return http?.StatusCode is { } status
            ? Number(status, "0")
            : "Not observed";
    }

    /// <summary>Orders required failures before supporting errors and warnings.</summary>
    private static int GetFindingPriority(DiagnosticCheckResult check)
    {
        if (check.Status is DiagnosticCheckStatus.Failed or DiagnosticCheckStatus.Error)
        {
            return check.Role == DiagnosticCheckRole.Required ? 0 : 1;
        }

        return 2;
    }

    /// <summary>Returns a fixed CSS class for a check status.</summary>
    private static string GetStatusClass(DiagnosticCheckStatus status)
    {
        return status switch
        {
            DiagnosticCheckStatus.Passed => "pass",
            DiagnosticCheckStatus.Warning => "warning",
            DiagnosticCheckStatus.Failed or DiagnosticCheckStatus.Error => "failure",
            _ => "muted"
        };
    }

    /// <summary>Returns an accessible text status label.</summary>
    private static string GetStatusLabel(DiagnosticCheckStatus status)
    {
        return status switch
        {
            DiagnosticCheckStatus.Passed => "PASS",
            DiagnosticCheckStatus.Warning => "WARN",
            DiagnosticCheckStatus.Failed => "FAIL",
            DiagnosticCheckStatus.Skipped => "SKIPPED",
            _ => "ERROR"
        };
    }

    /// <summary>Returns a readable execution state.</summary>
    private static string GetExecutionLabel(DiagnosticExecutionState state)
    {
        return state switch
        {
            DiagnosticExecutionState.Completed => "Completed",
            DiagnosticExecutionState.Cancelled => "Cancelled",
            DiagnosticExecutionState.TimedOut => "Timed out",
            _ => "Error"
        };
    }

    /// <summary>Appends an encoded overview measurement.</summary>
    private static void AppendMetric(
        StringBuilder document,
        string label,
        string value)
    {
        document.Append("<div><dt>")
            .Append(Encode(label))
            .Append("</dt><dd>")
            .Append(Encode(value))
            .Append("</dd></div>");
    }

    /// <summary>Appends an encoded measurement table row.</summary>
    private static void AppendRow(
        StringBuilder document,
        string label,
        string value)
    {
        document.Append("<tr><th scope=\"row\">")
            .Append(Encode(label))
            .Append("</th><td>")
            .Append(Encode(value))
            .Append("</td></tr>");
    }

    /// <summary>Formats an available measurement using invariant culture.</summary>
    private static string Number(
        double? value,
        string format = "0.###",
        string unit = "")
    {
        return value is { } number
            ? number.ToString(format, CultureInfo.InvariantCulture) + unit
            : "Not measured";
    }

    /// <summary>Formats a reported numeric claim without inventing an unknown value.</summary>
    private static string ReportedNumber(double? value, string format = "0.###", string unit = "") =>
        value is null ? "Not reported" : Number(value, format, unit);

    /// <summary>Distinguishes reported false from an unavailable capability claim.</summary>
    private static string ReportedFlag(bool? value) => value switch
    {
        true => "Yes",
        false => "No",
        null => "Not reported"
    };

    /// <summary>Distinguishes undefined silent levels from missing measurements.</summary>
    private static string Decibels(double? value, AudioSignalState state)
    {
        if (value is not null)
        {
            return Number(value, "0.00", " dBFS");
        }

        return state == AudioSignalState.Silent
            ? "Undefined for silence"
            : "Not measured";
    }

    /// <summary>Formats a client timestamp explicitly as UTC.</summary>
    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString(
            "yyyy-MM-dd HH:mm:ss.fff 'UTC'",
            CultureInfo.InvariantCulture);
    }

    /// <summary>Encodes dynamic text for HTML text and attribute contexts.</summary>
    private static string Encode(string? value)
    {
        return HtmlEncoder.Default.Encode(value ?? string.Empty);
    }
}
