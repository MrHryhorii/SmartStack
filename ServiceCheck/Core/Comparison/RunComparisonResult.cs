using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Diagnostics;

namespace Tsubaki.ServiceCheck.Core.Comparison;

/// <summary>Describes change, missing coverage, or limited comparability.</summary>
public enum ComparisonChangeKind
{
    Unchanged,
    Changed,
    BeforeMissing,
    AfterMissing,
    NotComparable
}

/// <summary>Classifies contextual comparison findings.</summary>
public enum ComparisonFindingSeverity
{
    Information,
    Warning
}

/// <summary>Explains a configuration change or interpretation limit.</summary>
public sealed record ComparisonFinding(
    ComparisonFindingSeverity Severity,
    string Code,
    string Summary);

/// <summary>Records configured-target and test-context comparability.</summary>
public sealed record ComparisonScope
{
    public required bool SameTargetId { get; init; }

    public required bool SameTargetUrl { get; init; }

    public required bool SameMode { get; init; }

    public required bool SameProfileFingerprint { get; init; }

    /// <summary>Is null when a speech fingerprint is unavailable on either side.</summary>
    public required bool? SameRequestFingerprint { get; init; }

    /// <summary>Allows outcome transitions within matching target and test contexts.</summary>
    public required bool OutcomesComparable { get; init; }
}

/// <summary>Pairs observations with one stable diagnostic check identifier.</summary>
public sealed record CheckComparisonResult
{
    public required string Id { get; init; }

    public DiagnosticCheckResult? Before { get; init; }

    public DiagnosticCheckResult? After { get; init; }

    public required ComparisonChangeKind Change { get; init; }

    public required string Summary { get; init; }
}

/// <summary>Pairs allowlisted settings, server claims, or measurements.</summary>
public sealed record FieldComparisonResult
{
    public required string Path { get; init; }

    public required string Label { get; init; }

    public required ComparisonChangeKind Change { get; init; }

    /// <summary>Null means unavailable; a JSON null value can represent an explicit claim.</summary>
    public JsonElement? Before { get; init; }

    /// <summary>Null means unavailable; a JSON null value can represent an explicit claim.</summary>
    public JsonElement? After { get; init; }

    public string? Unit { get; init; }
}

/// <summary>Stores an offline comparison in explicit before-and-after order.</summary>
public sealed record RunComparisonResult
{
    public required int SchemaVersion { get; init; }

    public required string ComparisonId { get; init; }

    public required DiagnosticToolInfo Tool { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DiagnosticRunResult Before { get; init; }

    public required DiagnosticRunResult After { get; init; }

    public required ComparisonScope Scope { get; init; }

    public required IReadOnlyList<ComparisonFinding> Findings { get; init; }

    public required IReadOnlyList<CheckComparisonResult> Checks { get; init; }

    public required IReadOnlyList<FieldComparisonResult> Fields { get; init; }
}
