using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Core.Networking;

/// <summary>
/// Preserves supporting checks and collected network evidence.
/// </summary>
public sealed record NetworkProbeResult(
    DiagnosticCheckResult Dns,
    DiagnosticCheckResult Tcp,
    DiagnosticNetworkEvidence Evidence);

/// <summary>
/// Observes DNS and direct TCP connectivity for one configured target.
/// </summary>
public interface INetworkProbe
{
    /// <summary>
    /// Collects network observations under configured and caller deadlines.
    /// </summary>
    Task<NetworkProbeResult> ProbeAsync(
        TargetProfile profile,
        CancellationToken cancellationToken = default,
        CancellationToken runCancellationToken = default);
}