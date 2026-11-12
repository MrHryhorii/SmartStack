using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Comparison;
using Tsubaki.ServiceCheck.Core.Diagnostics;

namespace Tsubaki.ServiceCheck.Core.Reports;

/// <summary>Writes an offline comparison without loading external resources.</summary>
public static class HtmlComparisonWriter
{
    private const string DocumentStart = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta http-equiv="Content-Security-Policy"
              content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
        <title>Tsubaki Service Check comparison</title>
        <style>
        :root {
            color-scheme: light;
            --canvas: #F5F3F1; --surface: #FFFFFF; --text: #20212A;
            --secondary: #626571; --accent: #8A3158; --border: #DEDBDD;
            --pass: #22643B; --warning: #8B5A00; --failure: #A3283B;
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--canvas); color: var(--text);
               font: 16px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
        main { max-width: 1200px; margin: 0 auto; padding: 32px 16px; }
        header { margin-bottom: 24px; }
        section { margin: 16px 0; padding: 24px; background: var(--surface);
                  border: 1px solid var(--border); border-radius: 8px; }
        h1 { margin: 8px 0; font-size: 2rem; }
        h2 { margin: 0 0 16px; font-size: 1.25rem; }
        p { margin: 8px 0; }
        .accent { color: var(--accent); font-weight: 700; }
        .muted { color: var(--secondary); }
        .pass { color: var(--pass); font-weight: 700; }
        .warning { color: var(--warning); font-weight: 700; }
        .failure { color: var(--failure); font-weight: 700; }
        .table-wrap { overflow-x: auto; }
        table { width: 100%; border-collapse: collapse; table-layout: fixed; }
        table.comparison { min-width: 760px; }
        caption { padding-bottom: 8px; text-align: left; color: var(--secondary); }
        th, td { padding: 8px 16px; border-bottom: 1px solid var(--border);
                 text-align: left; vertical-align: top; }
        th { font-weight: 600; }
        td { font-variant-numeric: tabular-nums; }
        code, pre, th, td, p, li { overflow-wrap: anywhere; }
        pre { padding: 16px; background: var(--canvas); white-space: pre-wrap; }
        ul { padding-left: 24px; }
        li { margin-bottom: 8px; }
        details { margin-top: 8px; }
        summary { cursor: pointer; font-weight: 600; }
        :focus-visible { outline: 3px solid var(--accent); outline-offset: 3px; }
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
            h1, h2 { break-after: avoid; }
            .table-wrap { overflow: visible; }
            table.comparison { min-width: 0; }
            th, td { padding: 4px 8px; font-size: 9pt; }
        }
        </style>
        </head>
        <body>
        <main>
        """;

    private const string DocumentEnd = """
        <footer>
            <h2>Interpretation</h2>
            <p>This comparison describes recorded client observations and configuration.</p>
            <p>A changed URL does not establish physical server identity.
               Different audio hashes do not establish a quality regression.
               Single-run timings are descriptive, not statistical benchmarks.</p>
        </footer>
        </main>
        </body>
        </html>
        """;

    /// <summary>Writes UTF-8 HTML and leaves the destination stream open.</summary>
    public static async Task WriteAsync(
        Stream destination,
        RunComparisonResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(result);

        if (!destination.CanWrite)
            throw new ArgumentException("The report destination must be writable.", nameof(destination));

        if (result.SchemaVersion != 1)
            throw new ArgumentException("Unsupported comparison result schema version.", nameof(result));

        cancellationToken.ThrowIfCancellationRequested();

        await using var writer = new StreamWriter(
            destination, new UTF8Encoding(false), bufferSize: 16384, leaveOpen: true);

        await WriteMarkupAsync(writer, DocumentStart, cancellationToken);
        await WriteMarkupAsync(writer, CreateOverview(result), cancellationToken);
        await WriteFindingsAsync(writer, result, cancellationToken);
        await WriteChecksAsync(writer, result, cancellationToken);
        await WriteFieldsAsync(writer, result, cancellationToken);
        await WriteMarkupAsync(writer, DocumentEnd, cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>Shows source identity, explicit input order, and functional context.</summary>
    private static string CreateOverview(RunComparisonResult result)
    {
        var scope = result.Scope;
        var outcome = scope.OutcomesComparable
            ? $"Recorded outcomes: {Label(result.Before.Outcome)} to {Label(result.After.Outcome)}."
            : "Recorded outcomes have different target or test contexts. Review the source results side by side.";
        var document = new StringBuilder($$"""
            <header>
                <p class="accent">{{Encode(result.Tool.Name)}}</p>
                <h1>Before-and-after comparison</h1>
                <p>{{Encode(outcome)}}</p>
                <p class="muted">Comparison: <code>{{Encode(result.ComparisonId)}}</code>.
                   Created: {{Encode(Utc(result.CreatedAtUtc))}}.
                   Tool version: {{Encode(result.Tool.Version)}}.</p>
            </header>
            <section aria-labelledby="context-title">
                <h2 id="context-title">Comparison context</h2>
                <p>Same configured target: <strong>{{YesNo(scope.SameTargetId)}}</strong>.
                   Same URL: <strong>{{YesNo(scope.SameTargetUrl)}}</strong>.
                   Same mode: <strong>{{YesNo(scope.SameMode)}}</strong>.</p>
                <p>Same effective profile: <strong>{{YesNo(scope.SameProfileFingerprint)}}</strong>.
                   Same speech request: <strong>{{YesNo(scope.SameRequestFingerprint)}}</strong>.
                   Outcomes comparable: <strong>{{YesNo(scope.OutcomesComparable)}}</strong>.</p>
            </section>
            <section aria-labelledby="sources-title">
                <h2 id="sources-title">Source runs</h2>
                <div class="table-wrap"><table class="comparison">
                <caption>Caller-supplied before/after order; timestamps do not reorder inputs.</caption>
                <thead><tr><th scope="col">Observation</th><th scope="col">Before</th>
                    <th scope="col">After</th></tr></thead><tbody>
            """);

        var before = result.Before;
        var after = result.After;
        AppendRow(document, "Run ID", before.RunId, after.RunId);
        AppendRow(document, "Started", Utc(before.StartedAtUtc), Utc(after.StartedAtUtc));
        AppendRow(document, "Finished", Utc(before.FinishedAtUtc), Utc(after.FinishedAtUtc));
        AppendRow(document, "Target ID", before.Target.Id, after.Target.Id);
        AppendRow(document, "Target URL", before.Target.BaseUrl, after.Target.BaseUrl);
        AppendRow(document, "Mode", Label(before.Mode), Label(after.Mode));
        AppendRow(document, "Profile", before.Profile.Name, after.Profile.Name);
        AppendRow(document, "Recorded outcome", Label(before.Outcome), Label(after.Outcome));
        AppendRow(document, "Execution", Label(before.ExecutionState), Label(after.ExecutionState));
        AppendRow(document, "Source utility", $"{before.Tool.Name} {before.Tool.Version}",
            $"{after.Tool.Name} {after.Tool.Version}");
        AppendRow(document, "Reported service", before.Server?.Service, after.Server?.Service);
        AppendRow(document, "Reported version", before.Server?.Version, after.Server?.Version);
        AppendRow(document, "Reported readiness",
            before.Server is { } oldServer ? Label(oldServer.Readiness) : null,
            after.Server is { } newServer ? Label(newServer.Readiness) : null);
        document.Append("</tbody></table></div></section>");
        return document.ToString();
    }

    /// <summary>Places interpretation warnings ahead of informational findings.</summary>
    private static async Task WriteFindingsAsync(
        StreamWriter writer, RunComparisonResult result, CancellationToken cancellationToken)
    {
        await WriteMarkupAsync(writer,
            "<section aria-labelledby=\"findings-title\"><h2 id=\"findings-title\">Findings</h2>",
            cancellationToken);

        if (result.Findings.Count == 0)
        {
            await WriteMarkupAsync(writer, "<p>No contextual findings were recorded.</p></section>",
                cancellationToken);
            return;
        }

        await WriteMarkupAsync(writer, "<ul>", cancellationToken);

        foreach (var finding in result.Findings.OrderByDescending(item => item.Severity))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var style = finding.Severity == ComparisonFindingSeverity.Warning ? "warning" : "muted";
            await WriteMarkupAsync(writer, $$"""
                <li><strong class="{{style}}">{{Encode(Label(finding.Severity))}}</strong>:
                    {{Encode(finding.Summary)}} <code>{{Encode(finding.Code)}}</code></li>
                """, cancellationToken);
        }

        await WriteMarkupAsync(writer, "</ul></section>", cancellationToken);
    }

    /// <summary>Shows source statuses and coverage without inventing missing observations.</summary>
    private static async Task WriteChecksAsync(
        StreamWriter writer, RunComparisonResult result, CancellationToken cancellationToken)
    {
        await WriteMarkupAsync(writer, """
            <section aria-labelledby="checks-title"><h2 id="checks-title">Check observations</h2>
            <div class="table-wrap"><table class="comparison">
            <caption>Recorded status, role, and evidence for each diagnostic check ID.</caption>
            <thead><tr><th scope="col">Check</th><th scope="col">Before</th>
                <th scope="col">After</th><th scope="col">Change</th></tr></thead><tbody>
            """, cancellationToken);

        foreach (var check in result.Checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteMarkupAsync(writer, $$"""
                <tr><th scope="row"><code>{{Encode(check.Id)}}</code></th>
                    <td>{{CheckMarkup(check.Before)}}</td><td>{{CheckMarkup(check.After)}}</td>
                    <td><strong>{{ChangeLabel(check.Change)}}</strong>
                        <p>{{Encode(check.Summary)}}</p></td></tr>
                """, cancellationToken);
        }

        if (result.Checks.Count == 0)
            await WriteMarkupAsync(writer, "<tr><td colspan=\"4\">No check records were supplied.</td></tr>",
                cancellationToken);

        await WriteMarkupAsync(writer, "</tbody></table></div></section>", cancellationToken);
    }

    /// <summary>Shows selected differences first while retaining unchanged context.</summary>
    private static async Task WriteFieldsAsync(
        StreamWriter writer, RunComparisonResult result, CancellationToken cancellationToken)
    {
        await WriteMarkupAsync(writer, """
            <section aria-labelledby="fields-title"><h2 id="fields-title">Settings and measurements</h2>
            <p>Not recorded means a value is unavailable. An explicit null remains a recorded claim.</p>
            <div class="table-wrap"><table class="comparison">
            <caption>Selected settings, reported claims, and client observations; differences first.</caption>
            <thead><tr><th scope="col">Field</th><th scope="col">Before</th>
                <th scope="col">After</th><th scope="col">Change</th></tr></thead><tbody>
            """, cancellationToken);

        foreach (var field in result.Fields.OrderBy(item => item.Change == ComparisonChangeKind.Unchanged))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unit = field.Unit is null ? "" : $"<p class=\"muted\">Unit: {Encode(field.Unit)}</p>";
            await WriteMarkupAsync(writer, $$"""
                <tr><th scope="row">{{Encode(field.Label)}}
                        <p><code>{{Encode(field.Path)}}</code></p>{{unit}}</th>
                    <td>{{ValueMarkup(field.Before)}}</td><td>{{ValueMarkup(field.After)}}</td>
                    <td>{{ChangeLabel(field.Change)}}</td></tr>
                """, cancellationToken);
        }

        if (result.Fields.Count == 0)
            await WriteMarkupAsync(writer, "<tr><td colspan=\"4\">No selected field values were supplied.</td></tr>",
                cancellationToken);

        await WriteMarkupAsync(writer, "</tbody></table></div></section>", cancellationToken);
    }

    /// <summary>Formats one source check using encoded text and controlled status classes.</summary>
    private static string CheckMarkup(DiagnosticCheckResult? check)
    {
        if (check is null)
            return "<em>Not recorded</em>";

        var style = check.Status switch
        {
            DiagnosticCheckStatus.Passed => "pass",
            DiagnosticCheckStatus.Failed or DiagnosticCheckStatus.Error => "failure",
            DiagnosticCheckStatus.Warning => "warning",
            _ => "muted"
        };
        var document = new StringBuilder($$"""
            <strong class="{{style}}">{{Encode(Label(check.Status))}}</strong>
            <p>Role: {{Encode(Label(check.Role))}}</p>
            <p>{{Encode(check.Summary)}}</p><code>{{Encode(check.Code)}}</code>
            """);

        if (check.DurationMs is { } duration)
            document.Append("<p>Client-observed duration: ")
                .Append(duration.ToString(CultureInfo.InvariantCulture)).Append(" ms.</p>");

        if (check.Http?.StatusCode is { } status)
            document.Append("<p>HTTP ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(".</p>");

        if (check.Http is { ErrorExcerpt: { } excerpt } http)
        {
            document.Append("<details><summary>HTTP error excerpt</summary><pre>")
                .Append(Encode(excerpt)).Append("</pre>");

            if (http.ErrorExcerptTruncated)
                document.Append("<p>The excerpt was truncated.</p>");

            document.Append("</details>");
        }

        if (check.Suggestions.Count > 0)
        {
            document.Append("<ul>");
            foreach (var suggestion in check.Suggestions)
                document.Append("<li>").Append(Encode(suggestion)).Append("</li>");
            document.Append("</ul>");
        }

        return document.ToString();
    }

    /// <summary>Distinguishes missing fields from typed values and explicit-null claims.</summary>
    private static string ValueMarkup(JsonElement? value)
    {
        if (value is null)
            return "<em>Not recorded</em>";

        if (value.Value.ValueKind == JsonValueKind.Null)
            return "<code>null</code> <span class=\"muted\">(explicit claim)</span>";

        var text = value.Value.ValueKind == JsonValueKind.String
            ? value.Value.GetString() ?? ""
            : value.Value.GetRawText();
        var display = text.Length == 0 ? "\"\"" : text;
        return $"<code>{Encode(display)}</code>";
    }

    /// <summary>Appends a plain-text source row without turning references into links.</summary>
    private static void AppendRow(StringBuilder document, string label, string? before, string? after)
    {
        document.Append("<tr><th scope=\"row\">").Append(Encode(label))
            .Append("</th><td>").Append(Encode(before ?? "Not recorded"))
            .Append("</td><td>").Append(Encode(after ?? "Not recorded")).Append("</td></tr>");
    }

    /// <summary>Labels transitions without assigning a new diagnostic outcome.</summary>
    private static string ChangeLabel(ComparisonChangeKind change) => change switch
    {
        ComparisonChangeKind.Unchanged => "Unchanged",
        ComparisonChangeKind.Changed => "Changed",
        ComparisonChangeKind.BeforeMissing => "New observation",
        ComparisonChangeKind.AfterMissing => "Missing observation",
        ComparisonChangeKind.NotComparable => "Not comparable",
        _ => "Unknown change"
    };

    /// <summary>Formats an observed timestamp with explicit UTC context.</summary>
    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>Labels known equality separately from unavailable request context.</summary>
    private static string YesNo(bool? value) => value is null ? "Not available" : value.Value ? "Yes" : "No";

    /// <summary>Formats stable enum words for human-readable labels.</summary>
    private static string Label<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()).Replace('_', ' ');

    /// <summary>Encodes all recorded text before inserting it into markup.</summary>
    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);

    /// <summary>Writes one bounded section or row under the caller's cancellation token.</summary>
    private static Task WriteMarkupAsync(StreamWriter writer, string markup, CancellationToken cancellationToken) =>
        writer.WriteAsync(markup.AsMemory(), cancellationToken);
}
