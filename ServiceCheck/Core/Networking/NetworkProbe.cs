using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Core.Networking;

/// <summary>
/// Collects bounded DNS and direct TCP observations.
/// </summary>
public sealed class NetworkProbe : INetworkProbe
{
    private const int MaxRetainedAddresses = 32;

    private readonly Func<
        string,
        CancellationToken,
        Task<IPAddress[]>> _resolve;

    private readonly Func<
        IPAddress,
        int,
        CancellationToken,
        Task> _connect;

    /// <summary>
    /// Configures network operations that honor cancellation and own their resources.
    /// </summary>
    public NetworkProbe(
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPAddress, int, CancellationToken, Task>? connect = null)
    {
        _resolve = resolve ?? ResolveHostAsync;
        _connect = connect ?? TcpProbe.ConnectEndpointAsync;
    }

    /// <summary>
    /// Collects supporting observations for the configured target.
    /// </summary>
    public async Task<NetworkProbeResult> ProbeAsync(
        TargetProfile profile,
        CancellationToken cancellationToken = default,
        CancellationToken runCancellationToken = default)
    {
        var normalized = ProfileValidator.ValidateAndNormalize(profile);
        var endpoint = new Uri(normalized.Target.BaseUrl);

        using var runDeadline = CancellationTokenSource
            .CreateLinkedTokenSource(runCancellationToken);

        runDeadline.CancelAfter(normalized.TimeoutsMs.Run);

        var (Check, Addresses) = await ResolveAsync(
            endpoint,
            normalized.TimeoutsMs.Dns,
            cancellationToken,
            runDeadline.Token);

        var tcp = await TcpProbe.ProbeAsync(
            Addresses,
            endpoint.Port,
            normalized.TimeoutsMs.Tcp,
            _connect,
            cancellationToken,
            runDeadline.Token);

        return new NetworkProbeResult(
            Check,
            tcp.Check,
            new DiagnosticNetworkEvidence(
                endpoint.Port,
                Addresses.Select(address => address.ToString()).ToArray(),
                tcp.Attempts));
    }

    /// <summary>
    /// Resolves a hostname or records an expected IP-literal omission.
    /// </summary>
    private async Task<(
        DiagnosticCheckResult Check,
        IPAddress[] Addresses)> ResolveAsync(
        Uri endpoint,
        int timeoutMs,
        CancellationToken cancellationToken,
        CancellationToken runCancellationToken)
    {
        if (cancellationToken.IsCancellationRequested ||
            runCancellationToken.IsCancellationRequested)
        {
            return (
                InterruptedCheck("dns.resolve", cancellationToken),
                []);
        }

        var host = Uri.UnescapeDataString(endpoint.IdnHost);

        if (IPAddress.TryParse(host, out var literal))
        {
            return (
                CreateCheck(
                    "dns.resolve",
                    DiagnosticCheckStatus.Skipped,
                    "ip_literal",
                    "DNS lookup is not applicable to an IP literal."),
                [literal]);
        }

        var clock = Stopwatch.StartNew();

        using var deadline = new CancellationTokenSource(timeoutMs);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            runCancellationToken,
            deadline.Token);

        try
        {
            var resolved = await _resolve(host, linked.Token);
            var addresses = RetainAddresses(resolved, out var truncated);

            if (addresses.Length == 0)
            {
                return (
                    CreateCheck(
                        "dns.resolve",
                        DiagnosticCheckStatus.Failed,
                        "dns_resolution_failed",
                        "DNS resolution returned no usable IP addresses.",
                        clock.ElapsedMilliseconds,
                        ["Check the configured hostname and client DNS settings."]),
                    []);
            }

            var check = CreateCheck(
                "dns.resolve",
                truncated
                    ? DiagnosticCheckStatus.Warning
                    : DiagnosticCheckStatus.Passed,
                truncated ? "dns_address_limit" : "dns_resolved",
                truncated
                    ? "DNS resolution succeeded; the retained address list was limited."
                    : "DNS resolution returned usable IP addresses.",
                clock.ElapsedMilliseconds);

            return (check, addresses);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested ||
                runCancellationToken.IsCancellationRequested)
            {
                return (
                    InterruptedCheck(
                        "dns.resolve",
                        cancellationToken,
                        clock.ElapsedMilliseconds),
                    []);
            }

            return (
                CreateCheck(
                    "dns.resolve",
                    deadline.IsCancellationRequested
                        ? DiagnosticCheckStatus.Failed
                        : DiagnosticCheckStatus.Error,
                    deadline.IsCancellationRequested
                        ? "dns_timeout"
                        : "unexpected_cancellation",
                    deadline.IsCancellationRequested
                        ? "DNS resolution exceeded its configured deadline."
                        : "DNS resolution was cancelled for an unknown reason.",
                    clock.ElapsedMilliseconds),
                []);
        }
        catch (SocketException)
        {
            return (
                CreateCheck(
                    "dns.resolve",
                    DiagnosticCheckStatus.Failed,
                    "dns_resolution_failed",
                    "The client could not resolve the configured hostname.",
                    clock.ElapsedMilliseconds,
                    ["Check the configured hostname and client DNS settings."]),
                []);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (
                CreateCheck(
                    "dns.resolve",
                    DiagnosticCheckStatus.Error,
                    "dns_probe_error",
                    "The client could not complete DNS inspection.",
                    clock.ElapsedMilliseconds),
                []);
        }
    }

    /// <summary>
    /// Retains distinct addresses while alternating available address families.
    /// </summary>
    private static IPAddress[] RetainAddresses(
        IPAddress[] resolved,
        out bool truncated)
    {
        var usable = resolved
            .Where(address => address.AddressFamily is
                AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Distinct()
            .ToArray();

        truncated = usable.Length > MaxRetainedAddresses;

        if (usable.Length == 0)
        {
            return [];
        }

        var preferredFamily = usable[0].AddressFamily;

        var preferred = usable
            .Where(address => address.AddressFamily == preferredFamily)
            .ToArray();

        var alternative = usable
            .Where(address => address.AddressFamily != preferredFamily)
            .ToArray();

        var retained = new List<IPAddress>(
            Math.Min(usable.Length, MaxRetainedAddresses));

        var count = Math.Max(preferred.Length, alternative.Length);

        for (var index = 0;
             index < count && retained.Count < MaxRetainedAddresses;
             index++)
        {
            if (index < preferred.Length)
            {
                retained.Add(preferred[index]);
            }

            if (index < alternative.Length &&
                retained.Count < MaxRetainedAddresses)
            {
                retained.Add(alternative[index]);
            }
        }

        return retained.ToArray();
    }

    /// <summary>
    /// Resolves all available IP address families through the platform resolver.
    /// </summary>
    private static Task<IPAddress[]> ResolveHostAsync(
        string host,
        CancellationToken cancellationToken)
    {
        return Dns.GetHostAddressesAsync(host, cancellationToken);
    }

    /// <summary>
    /// Creates a supporting network observation with bounded, selected evidence.
    /// </summary>
    internal static DiagnosticCheckResult CreateCheck(
        string id,
        DiagnosticCheckStatus status,
        string code,
        string summary,
        long? durationMs = null,
        IReadOnlyList<string>? suggestions = null)
    {
        return new DiagnosticCheckResult
        {
            Id = id,
            Role = DiagnosticCheckRole.Supporting,
            Status = status,
            Code = code,
            Summary = summary,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = durationMs,
            Suggestions = suggestions ?? []
        };
    }

    /// <summary>
    /// Distinguishes caller cancellation from an interrupted run deadline.
    /// </summary>
    internal static DiagnosticCheckResult InterruptedCheck(
        string id,
        CancellationToken cancellationToken,
        long? durationMs = null)
    {
        return CreateCheck(
            id,
            durationMs is null
                ? DiagnosticCheckStatus.Skipped
                : DiagnosticCheckStatus.Error,
            cancellationToken.IsCancellationRequested
                ? "cancelled"
                : "deadline_exceeded",
            cancellationToken.IsCancellationRequested
                ? "Network inspection was cancelled."
                : "Network inspection exceeded the run deadline.",
            durationMs);
    }
}