namespace Tsubaki.ServiceCheck.Core.Diagnostics;

/// <summary>
/// Aggregates observations into a mode-specific run outcome.
/// </summary>
public static class DiagnosticOutcomeRules
{
    private static readonly string[] CheckRequiredIds =
    [
        "speech.request",
        "audio.container",
        "audio.signal"
    ];

    private static readonly string[] InspectRequiredIds =
    [
        "service.identity",
        "service.readiness"
    ];

    /// <summary>
    /// Evaluates execution state, required checks, and coverage warnings.
    /// </summary>
    public static DiagnosticOutcome Evaluate(
        DiagnosticMode mode,
        DiagnosticExecutionState executionState,
        IReadOnlyList<DiagnosticCheckResult> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        var requiredIds = GetRequiredIds(mode);

        if (executionState != DiagnosticExecutionState.Completed)
        {
            return DiagnosticOutcome.Incomplete;
        }

        if (checks.Any(check =>
            check.Role == DiagnosticCheckRole.Required &&
            check.Status == DiagnosticCheckStatus.Failed))
        {
            return DiagnosticOutcome.Failed;
        }

        var checksById = new Dictionary<string, DiagnosticCheckResult>(
            StringComparer.Ordinal);

        foreach (var check in checks)
        {
            if (!checksById.TryAdd(check.Id, check))
            {
                return DiagnosticOutcome.Incomplete;
            }
        }

        foreach (var id in requiredIds)
        {
            if (!checksById.TryGetValue(id, out var check) ||
                check.Role != DiagnosticCheckRole.Required ||
                check.Status != DiagnosticCheckStatus.Passed)
            {
                return DiagnosticOutcome.Incomplete;
            }
        }

        if (checks.Any(check =>
            check.Role == DiagnosticCheckRole.Required &&
            check.Status != DiagnosticCheckStatus.Passed))
        {
            return DiagnosticOutcome.Incomplete;
        }

        return checks.Any(check => ProducesWarning(mode, check))
            ? DiagnosticOutcome.PassedWithWarnings
            : DiagnosticOutcome.Passed;
    }

    /// <summary>
    /// Returns the checks required by the selected operation.
    /// </summary>
    private static string[] GetRequiredIds(DiagnosticMode mode)
    {
        return mode switch
        {
            DiagnosticMode.Check => CheckRequiredIds,
            DiagnosticMode.Inspect => InspectRequiredIds,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    /// <summary>
    /// Identifies warnings and unexpected gaps in supporting coverage.
    /// </summary>
    private static bool ProducesWarning(
        DiagnosticMode mode,
        DiagnosticCheckResult check)
    {
        return check.Status switch
        {
            DiagnosticCheckStatus.Passed => false,
            DiagnosticCheckStatus.Skipped => !IsExpectedSkip(mode, check),
            _ => true
        };
    }

    /// <summary>
    /// Recognizes deliberate omissions that do not reduce coverage.
    /// </summary>
    private static bool IsExpectedSkip(
        DiagnosticMode mode,
        DiagnosticCheckResult check)
    {
        if (check.Id == "dns.resolve" && check.Code == "ip_literal")
        {
            return true;
        }

        return mode == DiagnosticMode.Inspect &&
            check.Code == "not_requested" &&
            check.Id is "speech.request" or "audio.container" or "audio.signal";
    }
}