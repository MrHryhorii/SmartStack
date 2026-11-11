using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Reports;

namespace Tsubaki.ServiceCheck.Core.Comparison;

/// <summary>Compares recorded runs without network, file, or clock access.</summary>
public static class RunComparer
{
    private static readonly string[] CoreCheckIds =
    [
        "dns.resolve", "tcp.connect", "service.health", "service.status", "service.voices",
        "service.identity", "service.readiness", "speech.request", "audio.container", "audio.signal"
    ];

    /// <summary>Compares explicit before/after inputs using caller-supplied publication metadata.</summary>
    public static RunComparisonResult Compare(
        DiagnosticRunResult before,
        DiagnosticRunResult after,
        string toolVersion,
        string comparisonId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ValidateLabel(toolVersion, nameof(toolVersion), 1024);
        ValidateLabel(comparisonId, nameof(comparisonId), 128);
        cancellationToken.ThrowIfCancellationRequested();

        ReportValidator.Validate(before, cancellationToken);
        ReportValidator.Validate(after, cancellationToken);

        var scope = CreateScope(before, after);
        var checks = CompareChecks(before, after, scope, cancellationToken);
        var fields = RunComparisonFields.Build(
            before, after, scope, checks, cancellationToken);
        var findings = CreateFindings(before, after, scope, checks, fields);

        cancellationToken.ThrowIfCancellationRequested();

        return new RunComparisonResult
        {
            SchemaVersion = 1,
            ComparisonId = comparisonId,
            Tool = new DiagnosticToolInfo("Tsubaki Service Check", toolVersion),
            CreatedAtUtc = createdAtUtc.ToUniversalTime(),
            Before = before with { AudioPayload = null },
            After = after with { AudioPayload = null },
            Scope = scope,
            Findings = findings,
            Checks = checks,
            Fields = fields
        };
    }

    /// <summary>Determines configured-target and mode-specific outcome comparability.</summary>
    private static ComparisonScope CreateScope(
        DiagnosticRunResult before,
        DiagnosticRunResult after)
    {
        var sameTargetId = string.Equals(before.Target.Id, after.Target.Id, StringComparison.Ordinal);
        var sameMode = before.Mode == after.Mode;
        bool? sameRequest = before.Request is not null && after.Request is not null
            ? HashEquals(before.Request.Fingerprint, after.Request.Fingerprint)
            : null;

        return new ComparisonScope
        {
            SameTargetId = sameTargetId,
            SameTargetUrl = string.Equals(
                before.Target.BaseUrl, after.Target.BaseUrl, StringComparison.Ordinal),
            SameMode = sameMode,
            SameProfileFingerprint = HashEquals(
                before.Profile.Fingerprint, after.Profile.Fingerprint),
            SameRequestFingerprint = sameRequest,
            OutcomesComparable = sameTargetId && sameMode
                && (before.Mode == DiagnosticMode.Inspect || sameRequest == true)
        };
    }

    /// <summary>Pairs stable check IDs while retaining the supplied source order.</summary>
    private static IReadOnlyList<CheckComparisonResult> CompareChecks(
        DiagnosticRunResult before,
        DiagnosticRunResult after,
        ComparisonScope scope,
        CancellationToken cancellationToken)
    {
        var beforeById = before.Checks.ToDictionary(check => check.Id, StringComparer.Ordinal);
        var afterById = after.Checks.ToDictionary(check => check.Id, StringComparer.Ordinal);
        var ids = before.Checks.Select(check => check.Id)
            .Concat(after.Checks.Select(check => check.Id))
            .Distinct(StringComparer.Ordinal);
        var compared = new List<CheckComparisonResult>();

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeById.TryGetValue(id, out var old);
            afterById.TryGetValue(id, out var current);
            var change = GetCheckChange(id, old, current, scope);

            compared.Add(new CheckComparisonResult
            {
                Id = id,
                Before = old,
                After = current,
                Change = change,
                Summary = DescribeCheckChange(change, old, current)
            });
        }

        return compared.ToArray();
    }

    /// <summary>Separates missing observations, context changes, and diagnostic transitions.</summary>
    private static ComparisonChangeKind GetCheckChange(
        string id,
        DiagnosticCheckResult? before,
        DiagnosticCheckResult? after,
        ComparisonScope scope)
    {
        if (before is null)
            return ComparisonChangeKind.BeforeMissing;

        if (after is null)
            return ComparisonChangeKind.AfterMissing;

        if (!scope.SameTargetId || before.Role != after.Role
            || WasNotRequested(before) || WasNotRequested(after))
        {
            return ComparisonChangeKind.NotComparable;
        }

        if (IsRequestDependent(id)
            && (!scope.OutcomesComparable || scope.SameRequestFingerprint != true))
        {
            return ComparisonChangeKind.NotComparable;
        }

        if (!IsRequestDependent(id) && !IsSupportingStage(id) && !scope.OutcomesComparable)
            return ComparisonChangeKind.NotComparable;

        var unchanged = before.Status == after.Status
            && string.Equals(before.Code, after.Code, StringComparison.Ordinal)
            && before.Http?.StatusCode == after.Http?.StatusCode
            && before.Http?.BodyComplete == after.Http?.BodyComplete;

        return unchanged ? ComparisonChangeKind.Unchanged : ComparisonChangeKind.Changed;
    }

    /// <summary>Identifies observations whose meaning depends on the active speech payload.</summary>
    private static bool IsRequestDependent(string id)
    {
        return id.StartsWith("speech.", StringComparison.Ordinal)
            || id.StartsWith("audio.", StringComparison.Ordinal)
            || id == "service.readiness.speech_discrepancy";
    }

    /// <summary>Recognizes supporting network and metadata stages shared by both modes.</summary>
    private static bool IsSupportingStage(string id)
    {
        return id.StartsWith("dns.", StringComparison.Ordinal)
            || id.StartsWith("tcp.", StringComparison.Ordinal)
            || id.StartsWith("service.", StringComparison.Ordinal)
            || id.StartsWith("metadata.", StringComparison.Ordinal);
    }

    /// <summary>Identifies deliberate omissions that cannot prove a tested result.</summary>
    private static bool WasNotRequested(DiagnosticCheckResult check)
    {
        return check.Status == DiagnosticCheckStatus.Skipped && check.Code == "not_requested";
    }

    /// <summary>Describes the recorded transition without claiming a physical recovery.</summary>
    private static string DescribeCheckChange(
        ComparisonChangeKind change,
        DiagnosticCheckResult? before,
        DiagnosticCheckResult? after)
    {
        if (change == ComparisonChangeKind.BeforeMissing)
            return "New observation; no before check record was available.";

        if (change == ComparisonChangeKind.AfterMissing)
            return "Missing observation; no after check record was available.";

        if (change == ComparisonChangeKind.NotComparable)
            return "The check was not requested or its target, role, mode, or request context differs.";

        if (change == ComparisonChangeKind.Unchanged)
            return "Diagnostic status, result code, and HTTP completion evidence are unchanged.";

        if (before is null || after is null)
            throw new InvalidOperationException("A recorded transition requires both check observations.");

        if (before.Status != after.Status)
            return $"{Label(before.Status)} to {Label(after.Status)}.";

        return "Diagnostic status is unchanged; its result code or HTTP completion evidence differs.";
    }

    /// <summary>Explains configuration changes, incomplete coverage, and measurement limits.</summary>
    private static IReadOnlyList<ComparisonFinding> CreateFindings(
        DiagnosticRunResult before,
        DiagnosticRunResult after,
        ComparisonScope scope,
        IReadOnlyList<CheckComparisonResult> checks,
        IReadOnlyList<FieldComparisonResult> fields)
    {
        var findings = new List<ComparisonFinding>();

        AddFinding(findings, !scope.SameTargetId, "different_configured_targets",
            "The inputs identify different configured targets. Their observations are shown side by side.",
            ComparisonFindingSeverity.Warning);
        AddFinding(findings, !scope.SameTargetUrl, "target_url_changed",
            "The configured URL differs. A routing change does not establish physical server identity.",
            ComparisonFindingSeverity.Warning);
        AddFinding(findings, !scope.SameMode, "mode_changed",
            "The diagnostic modes differ. Their required outcomes and speech coverage have different scopes.",
            ComparisonFindingSeverity.Warning);
        AddFinding(findings, scope.SameRequestFingerprint == false, "speech_request_changed",
            "Speech request fingerprints differ. Functional speech results have limited comparability.",
            ComparisonFindingSeverity.Warning);
        AddFinding(findings, !scope.SameProfileFingerprint, "profile_settings_changed",
            "Effective profile fingerprints differ. Review the exported settings and request differences.");
        AddFinding(findings, before.Tool != after.Tool, "source_tool_changed",
            "The source utility name or version differs. Diagnostic coverage may have changed.");
        AddFinding(findings, before.RunId == after.RunId, "same_source_run_id",
            "Both inputs report the same run ID; a separate after observation is not established.");
        AddFinding(findings, before.StartedAtUtc > after.StartedAtUtc, "input_timestamps_reversed",
            "The before input has a later start timestamp. The supplied before/after order is preserved.");
        AddFinding(findings,
            before.ExecutionState != DiagnosticExecutionState.Completed
                || after.ExecutionState != DiagnosticExecutionState.Completed,
            "interrupted_execution",
            "At least one run did not complete. Recorded observations and missing coverage are retained.",
            ComparisonFindingSeverity.Warning);

        var beforeMissing = checks.Count(check => check.Change == ComparisonChangeKind.BeforeMissing);
        var afterMissing = checks.Count(check => check.Change == ComparisonChangeKind.AfterMissing);

        AddFinding(findings, beforeMissing > 0 || afterMissing > 0, "check_coverage_changed",
            FormattableString.Invariant(
                $"Check coverage differs: {beforeMissing} observation(s) have no before record and {afterMissing} have no after record."),
            ComparisonFindingSeverity.Warning);
        AddFinding(findings, HasMissingCoreChecks(before) || HasMissingCoreChecks(after),
            "check_coverage_incomplete",
            "At least one input omits known diagnostic check records. Their evidence remains unrecorded.",
            ComparisonFindingSeverity.Warning);
        AddFinding(findings,
            checks.Any(check => check.Before is not null && check.After is not null
                && check.Before.Role != check.After.Role),
            "check_roles_changed",
            "Some check roles differ. Required and supporting observations retain their original meanings.",
            ComparisonFindingSeverity.Warning);
        AddFinding(findings,
            before.Server?.Service is null || after.Server?.Service is null
                || before.Server?.Readiness == DiagnosticReadiness.Unknown
                || after.Server?.Readiness == DiagnosticReadiness.Unknown,
            "metadata_coverage_limited",
            "Some selected identity or readiness claims are unavailable. Review the source coverage.",
            ComparisonFindingSeverity.Warning);
        AddFinding(findings,
            before.Server?.AdvertisedVoicesComplete != true
                || after.Server?.AdvertisedVoicesComplete != true,
            "voice_coverage_limited",
            "At least one advertised voice list is unavailable or incomplete; missing IDs do not establish removal.",
            ComparisonFindingSeverity.Warning);

        AddFinding(findings,
            scope.OutcomesComparable && before.Outcome != after.Outcome,
            "recorded_outcome_changed",
            $"Recorded outcomes: {Label(before.Outcome)} to {Label(after.Outcome)}.");
        AddFinding(findings,
            before.Server is { Version: not null } oldServer
                && after.Server is { Version: not null } currentServer
                && !string.Equals(oldServer.Version, currentServer.Version, StringComparison.Ordinal),
            "reported_version_changed",
            "Reported server version values differ and provide comparison context.");
        AddFinding(findings,
            fields.Any(field => field.Path.StartsWith("server.statusSnapshot.", StringComparison.Ordinal)
                && field.Change is ComparisonChangeKind.Changed
                    or ComparisonChangeKind.BeforeMissing or ComparisonChangeKind.AfterMissing),
            "reported_defaults_changed",
            "Selected reported defaults or their available coverage differ.");
        AddFinding(findings,
            before.Audio is not null && after.Audio is not null
                && !HashEquals(before.Audio.Inspection.PayloadSha256, after.Audio.Inspection.PayloadSha256),
            "audio_artifact_changed",
            "Different audio hashes identify different received artifacts and do not establish a quality regression.");
        AddFinding(findings,
            fields.Any(field => field.Path.StartsWith("timings.", StringComparison.Ordinal)
                && field.Change == ComparisonChangeKind.Changed),
            "single_run_timing",
            "Client-observed durations describe individual runs, not statistical benchmarks or isolated inference timings.");

        return findings.ToArray();
    }

    /// <summary>Detects stable checks missing even when neither input contains their ID.</summary>
    private static bool HasMissingCoreChecks(DiagnosticRunResult result)
    {
        var ids = new HashSet<string>(result.Checks.Select(check => check.Id), StringComparer.Ordinal);
        return CoreCheckIds.Any(id => !ids.Contains(id));
    }

    /// <summary>Adds one bounded, evidence-based interpretation finding.</summary>
    private static void AddFinding(
        List<ComparisonFinding> findings,
        bool applies,
        string code,
        string summary,
        ComparisonFindingSeverity severity = ComparisonFindingSeverity.Information)
    {
        if (!applies)
            return;

        findings.Add(new ComparisonFinding(severity, code, summary));
    }

    /// <summary>Compares hexadecimal digests independently of their letter casing.</summary>
    private static bool HashEquals(string before, string after)
    {
        return string.Equals(before, after, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Formats enum labels consistently with canonical JSON.</summary>
    private static string Label<T>(T value) where T : struct, Enum
    {
        return JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
    }

    /// <summary>Validates publication labels without interpreting them as paths.</summary>
    private static void ValidateLabel(string? value, string parameterName, int maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException(
                $"The value must contain at most {maximum} characters without control characters.",
                parameterName);
    }
}
