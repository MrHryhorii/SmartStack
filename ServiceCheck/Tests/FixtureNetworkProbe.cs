using System.Net;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Networking;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>
/// Supplies controlled network observations for diagnostic fixtures.
/// </summary>
internal sealed class FixtureNetworkProbe : INetworkProbe
{
    public static INetworkProbe Connected { get; } = new FixtureNetworkProbe();

    private readonly Func<
        TargetProfile,
        CancellationToken,
        CancellationToken,
        Task<NetworkProbeResult>> _probe;

    /// <summary>Stores a fixture operation or uses successful observations.</summary>
    public FixtureNetworkProbe(
        Func<
            TargetProfile,
            CancellationToken,
            CancellationToken,
            Task<NetworkProbeResult>>? probe = null)
    {
        _probe = probe ?? CreateConnectedAsync;
    }

    /// <summary>Executes the controlled network operation.</summary>
    public Task<NetworkProbeResult> ProbeAsync(
        TargetProfile profile,
        CancellationToken cancellationToken = default,
        CancellationToken runCancellationToken = default)
    {
        return _probe(profile, cancellationToken, runCancellationToken);
    }

    /// <summary>Creates successful observations without opening connections.</summary>
    private static Task<NetworkProbeResult> CreateConnectedAsync(
        TargetProfile profile,
        CancellationToken cancellationToken,
        CancellationToken runCancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        runCancellationToken.ThrowIfCancellationRequested();

        var endpoint = new Uri(profile.Target.BaseUrl);

        var isLiteral = IPAddress.TryParse(
            Uri.UnescapeDataString(endpoint.IdnHost),
            out var literal);

        var address = isLiteral
            ? literal!.ToString()
            : IPAddress.Loopback.ToString();

        var dns = CreateCheck(
            "dns.resolve",
            isLiteral
                ? DiagnosticCheckStatus.Skipped
                : DiagnosticCheckStatus.Passed,
            isLiteral ? "ip_literal" : "dns_resolved",
            isLiteral
                ? "DNS lookup is not applicable to an IP literal."
                : "Fixture DNS addresses are available.");

        var tcp = CreateCheck(
            "tcp.connect",
            DiagnosticCheckStatus.Passed,
            "tcp_connected",
            "Fixture TCP connectivity is available.");

        var evidence = new DiagnosticNetworkEvidence(
            endpoint.Port,
            [address],
            [
                new DiagnosticConnectionEvidence(
                    address,
                    endpoint.Port,
                    DiagnosticCheckStatus.Passed,
                    "tcp_connected",
                    0)
            ]);

        return Task.FromResult(new NetworkProbeResult(dns, tcp, evidence));
    }

    /// <summary>Creates one supporting fixture observation.</summary>
    private static DiagnosticCheckResult CreateCheck(
        string id,
        DiagnosticCheckStatus status,
        string code,
        string summary)
    {
        return new DiagnosticCheckResult
        {
            Id = id,
            Role = DiagnosticCheckRole.Supporting,
            Status = status,
            Code = code,
            Summary = summary,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = status == DiagnosticCheckStatus.Skipped ? null : 0
        };
    }
}