using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Networking;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>
/// Verifies bounded DNS and TCP observations with controlled operations.
/// </summary>
internal static class NetworkProbeChecks
{
    /// <summary>Returns network observation regression checks.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks()
    {
        return
        [
            ("IP literals skip DNS and preserve target ports", VerifyIpLiteralsAsync),
            ("DNS names normalized and address families interleaved", VerifyResolutionAsync),
            ("DNS failures skip dependent TCP inspection", VerifyDnsFailuresAsync),
            ("DNS stage deadline distinguished", VerifyDnsDeadlineAsync),
            ("Socket and TCP stage timeouts distinguished", VerifyTcpTimeoutsAsync),
            ("Caller cancellation stops network operations", VerifyCallerCancellationAsync),
            ("Run deadline interrupts DNS and TCP", VerifyRunDeadlineAsync),
            ("Successful fallback releases the superseded connection", VerifyFallbackAsync),
            ("Address retention and TCP concurrency bounded", VerifyBoundsAsync),
            ("Native TCP connection closed without application data", VerifyNativeConnectionAsync),
            ("Native refused connection classified", VerifyNativeRefusalAsync),
            ("Probe errors omit private text and classify unrelated cancellation", VerifyProbeErrorsAsync)
        ];
    }

    /// <summary>Verifies IPv4, IPv6, and default HTTPS port handling.</summary>
    private static async Task VerifyIpLiteralsAsync()
    {
        (string Url, IPAddress Address, int Port)[] cases =
        [
            ("http://127.0.0.1:5045/", IPAddress.Loopback, 5045),
            ("http://[::1]:5045/", IPAddress.IPv6Loopback, 5045),
            ("https://127.0.0.1/", IPAddress.Loopback, 443)
        ];

        foreach (var (url, expectedAddress, expectedPort) in cases)
        {
            var resolutions = 0;
            IPAddress? connectedAddress = null;
            var connectedPort = -1;

            var probe = new NetworkProbe(
                resolve: (_, _) =>
                {
                    resolutions++;
                    return Task.FromResult(Array.Empty<IPAddress>());
                },
                connect: (address, port, _) =>
                {
                    connectedAddress = address;
                    connectedPort = port;
                    return Task.CompletedTask;
                });

            var result = await RunAsync(probe, CreateProfile(url));

            Require(
                resolutions == 0 &&
                result.Dns.Status == DiagnosticCheckStatus.Skipped &&
                result.Dns.Code == "ip_literal",
                "An IP literal triggered DNS resolution.");

            Require(
                expectedAddress.Equals(connectedAddress) &&
                connectedPort == expectedPort &&
                result.Evidence.Port == expectedPort &&
                result.Tcp.Status == DiagnosticCheckStatus.Passed,
                "The literal address or target port changed.");

            Require(
                result.Evidence.ConnectionAttempts.Count == 1,
                "A literal target produced unexpected connection attempts.");
        }
    }

    /// <summary>Verifies IDN normalization, deduplication, and fast fallback.</summary>
    private static async Task VerifyResolutionAsync()
    {
        string? observedHost = null;

        IPAddress[] addresses =
        [
            IPAddress.IPv6Loopback,
            IPAddress.Parse("2001:db8::1"),
            IPAddress.Loopback,
            IPAddress.Loopback
        ];

        var probe = new NetworkProbe(
            resolve: (host, _) =>
            {
                observedHost = host;
                return Task.FromResult(addresses);
            },
            connect: (address, _, _) =>
                address.Equals(IPAddress.Loopback)
                    ? Task.CompletedTask
                    : Task.FromException(new SocketException(
                        (int)SocketError.ConnectionRefused)));

        var result = await RunAsync(
            probe,
            CreateProfile("http://m\u00FCnchen.example.test:5045/proxy/"));

        Require(
            observedHost == "xn--mnchen-3ya.example.test" &&
            result.Dns.Status == DiagnosticCheckStatus.Passed,
            "The hostname was not normalized for DNS resolution.");

        Require(
            result.Evidence.ResolvedAddresses.SequenceEqual(
                new[] { "::1", "127.0.0.1", "2001:db8::1" }),
            "Resolved addresses were not deduplicated and interleaved.");

        Require(
            result.Tcp.Status == DiagnosticCheckStatus.Passed &&
            result.Evidence.ConnectionAttempts.Count == 2 &&
            result.Evidence.ConnectionAttempts[0].Code == "connection_refused" &&
            result.Evidence.ConnectionAttempts[1].Status ==
                DiagnosticCheckStatus.Passed,
            "A failed address prevented successful fallback.");
    }

    /// <summary>Verifies empty and failed DNS results without TCP side effects.</summary>
    private static async Task VerifyDnsFailuresAsync()
    {
        foreach (var failResolution in new[] { false, true })
        {
            var connections = 0;

            var probe = new NetworkProbe(
                resolve: (_, _) => failResolution
                    ? Task.FromException<IPAddress[]>(
                        new SocketException((int)SocketError.HostNotFound))
                    : Task.FromResult(Array.Empty<IPAddress>()),
                connect: (_, _, _) =>
                {
                    connections++;
                    return Task.CompletedTask;
                });

            var result = await RunAsync(probe);

            Require(
                result.Dns.Status == DiagnosticCheckStatus.Failed &&
                result.Dns.Code == "dns_resolution_failed",
                "DNS failure was not classified.");

            Require(
                result.Tcp.Status == DiagnosticCheckStatus.Skipped &&
                result.Tcp.Code == "dependency_failed" &&
                connections == 0 &&
                result.Evidence.ConnectionAttempts.Count == 0,
                "TCP inspection ran without resolved addresses.");
        }
    }

    /// <summary>Verifies a DNS stage timeout without a run interruption.</summary>
    private static async Task VerifyDnsDeadlineAsync()
    {
        var profile = CreateProfile() with
        {
            TimeoutsMs = new ProfileTimeouts
            {
                Dns = 100,
                Tcp = 2000,
                Run = 5000
            }
        };

        var probe = new NetworkProbe(
            resolve: async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return [];
            },
            connect: static (_, _, _) => Task.CompletedTask);

        var result = await RunAsync(probe, profile);

        Require(
            result.Dns.Status == DiagnosticCheckStatus.Failed &&
            result.Dns.Code == "dns_timeout" &&
            result.Dns.DurationMs is not null,
            "The DNS stage deadline was not recorded.");

        Require(
            result.Tcp.Code == "dependency_failed",
            "A DNS stage timeout was confused with caller cancellation.");
    }

    /// <summary>Distinguishes a socket timeout from exhaustion of the TCP budget.</summary>
    private static async Task VerifyTcpTimeoutsAsync()
    {
        var profile = CreateProfile("http://127.0.0.1:5045/");

        var socketTimeout = new NetworkProbe(
            connect: static (_, _, _) => Task.FromException(
                new SocketException((int)SocketError.TimedOut)));

        var socketResult = await RunAsync(socketTimeout, profile);

        Require(
            socketResult.Tcp.Status == DiagnosticCheckStatus.Failed &&
            socketResult.Tcp.Code == "connection_timeout" &&
            !socketResult.Tcp.Summary.Contains(
                "configured deadline",
                StringComparison.Ordinal),
            "A socket timeout was incorrectly reported as a stage deadline.");

        var stageTimeout = new NetworkProbe(
            connect: static (_, _, token) =>
                Task.Delay(Timeout.InfiniteTimeSpan, token));

        var stageResult = await RunAsync(
            stageTimeout,
            profile with
            {
                TimeoutsMs = profile.TimeoutsMs with { Tcp = 100 }
            });

        Require(
            stageResult.Tcp.Status == DiagnosticCheckStatus.Failed &&
            stageResult.Tcp.Code == "connection_timeout" &&
            stageResult.Tcp.Summary.Contains(
                "configured deadline",
                StringComparison.Ordinal),
            "The TCP stage deadline was not distinguished.");
    }

    /// <summary>Verifies cancellation before work and during DNS resolution.</summary>
    private static async Task VerifyCallerCancellationAsync()
    {
        var operations = 0;

        var initialProbe = new NetworkProbe(
            resolve: (_, _) =>
            {
                operations++;
                return Task.FromResult(new[] { IPAddress.Loopback });
            },
            connect: (_, _, _) =>
            {
                operations++;
                return Task.CompletedTask;
            });

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();

            var result = await RunAsync(
                initialProbe,
                cancellationToken: cancelled.Token);

            Require(
                operations == 0 &&
                result.Dns.Status == DiagnosticCheckStatus.Skipped &&
                result.Dns.Code == "cancelled" &&
                result.Tcp.Code == "cancelled",
                "A cancelled caller started network operations.");
        }

        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(8));

        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var released = false;

        var activeProbe = new NetworkProbe(
            resolve: async (_, token) =>
            {
                entered.TrySetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return [];
                }
                finally
                {
                    released = true;
                }
            },
            connect: static (_, _, _) => Task.CompletedTask);

        var pending = RunAsync(
            activeProbe,
            CreateProfile() with
            {
                TimeoutsMs = new ProfileTimeouts
                {
                    Dns = 10000,
                    Tcp = 10000,
                    Run = 15000
                }
            },
            cancellation.Token);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            var result = await pending;

            Require(
                released &&
                result.Dns.Status == DiagnosticCheckStatus.Error &&
                result.Dns.Code == "cancelled" &&
                result.Tcp.Code == "cancelled",
                "Active DNS cancellation lost its cause or cleanup.");
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    /// <summary>Verifies that the run budget can interrupt either network stage.</summary>
    private static async Task VerifyRunDeadlineAsync()
    {
        var timeouts = new ProfileTimeouts
        {
            Dns = 2000,
            Tcp = 2000,
            Run = 100
        };

        var dnsProbe = new NetworkProbe(
            resolve: async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return [];
            },
            connect: static (_, _, _) => Task.CompletedTask);

        var dnsResult = await RunAsync(
            dnsProbe,
            CreateProfile() with { TimeoutsMs = timeouts });

        Require(
            dnsResult.Dns.Status == DiagnosticCheckStatus.Error &&
            dnsResult.Dns.Code == "deadline_exceeded" &&
            dnsResult.Tcp.Code == "deadline_exceeded",
            "The run deadline was confused with a DNS stage timeout.");

        var tcpProbe = new NetworkProbe(
            connect: static (_, _, token) =>
                Task.Delay(Timeout.InfiniteTimeSpan, token));

        var tcpResult = await RunAsync(
            tcpProbe,
            CreateProfile("http://127.0.0.1:5045/") with
            {
                TimeoutsMs = timeouts
            });

        Require(
            tcpResult.Tcp.Status == DiagnosticCheckStatus.Error &&
            tcpResult.Tcp.Code == "deadline_exceeded" &&
            tcpResult.Evidence.ConnectionAttempts.Count == 1,
            "The run deadline was confused with a TCP stage timeout.");
    }

    /// <summary>Verifies that successful fallback cancels and joins the first attempt.</summary>
    private static async Task VerifyFallbackAsync()
    {
        var started = 0;
        var finished = 0;

        var probe = new NetworkProbe(
            resolve: static (_, _) => Task.FromResult(
                new[] { IPAddress.IPv6Loopback, IPAddress.Loopback }),
            connect: async (address, _, token) =>
            {
                Interlocked.Increment(ref started);

                try
                {
                    if (address.Equals(IPAddress.IPv6Loopback))
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                }
                finally
                {
                    Interlocked.Increment(ref finished);
                }
            });

        var result = await RunAsync(probe);

        Require(
            result.Tcp.Status == DiagnosticCheckStatus.Passed &&
            started == 2 &&
            finished == 2,
            "Successful fallback left an unfinished connection operation.");

        Require(
            result.Evidence.ConnectionAttempts.Count == 2 &&
            result.Evidence.ConnectionAttempts[0].Code ==
                "alternative_succeeded" &&
            result.Evidence.ConnectionAttempts[1].Status ==
                DiagnosticCheckStatus.Passed,
            "Superseded and successful attempts were not preserved separately.");
    }

    /// <summary>Bounds retained addresses and simultaneous pending connections.</summary>
    private static async Task VerifyBoundsAsync()
    {
        var addresses = Enumerable.Range(1, 40)
            .Select(index => IPAddress.Parse($"192.0.2.{index}"))
            .ToArray();

        var started = 0;
        var active = 0;
        var finished = 0;

        var probe = new NetworkProbe(
            resolve: (_, _) => Task.FromResult(addresses),
            connect: async (_, _, token) =>
            {
                Interlocked.Increment(ref started);
                Interlocked.Increment(ref active);

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                    Interlocked.Increment(ref finished);
                }
            });

        var result = await RunAsync(
            probe,
            CreateProfile() with
            {
                TimeoutsMs = new ProfileTimeouts
                {
                    Dns = 1000,
                    Tcp = 1000,
                    Run = 5000
                }
            });

        Require(
            result.Dns.Status == DiagnosticCheckStatus.Warning &&
            result.Dns.Code == "dns_address_limit" &&
            result.Evidence.ResolvedAddresses.Count == 32,
            "Address retention did not enforce its documented limit.");

        Require(
            started > 0 &&
            started <= 2 &&
            active == 0 &&
            finished == started &&
            result.Evidence.ConnectionAttempts.Count == started,
            "TCP concurrency exceeded its bound or left unfinished operations.");

        Require(
            result.Tcp.Code == "connection_timeout",
            "Exhaustion of the shared TCP budget was not recorded.");
    }

    /// <summary>Verifies a real loopback connection and release of its socket.</summary>
    private static async Task VerifyNativeConnectionAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);

        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(8));

        listener.Start();

        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;

            var accepted = listener.AcceptTcpClientAsync(
                cancellation.Token).AsTask();

            var result = await RunAsync(
                new NetworkProbe(),
                CreateProfile($"http://127.0.0.1:{endpoint.Port}/"),
                cancellation.Token);

            Require(
                result.Tcp.Status == DiagnosticCheckStatus.Passed,
                "The native loopback connection did not pass.");

            using var peer = await accepted;

            var read = await peer.GetStream().ReadAsync(
                new byte[1],
                cancellation.Token);

            Require(
                read == 0,
                "The probe sent application data or left its socket open.");
        }
        finally
        {
            cancellation.Cancel();
            listener.Stop();
        }
    }

    /// <summary>Verifies refusal against a reserved port without a listener.</summary>
    private static async Task VerifyNativeRefusalAsync()
    {
        using var reservation = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp)
        {
            ExclusiveAddressUse = true
        };

        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var endpoint = (IPEndPoint)reservation.LocalEndPoint!;

        var profile = CreateProfile(
            $"http://127.0.0.1:{endpoint.Port}/") with
        {
            TimeoutsMs = new ProfileTimeouts
            {
                Dns = 1000,
                Tcp = 5000,
                Run = 10000
            }
        };

        var result = await RunAsync(new NetworkProbe(), profile);

        Require(
            result.Tcp.Status == DiagnosticCheckStatus.Failed &&
            result.Tcp.Code == "connection_refused" &&
            result.Evidence.ConnectionAttempts.Count == 1,
            "The native refused connection was not classified.");
    }

    /// <summary>Omits exception text and distinguishes unrelated cancellations.</summary>
    private static async Task VerifyProbeErrorsAsync()
    {
        const string privateDetail = "FixturePrivateNetworkDetail_47";

        var dnsError = new NetworkProbe(
            resolve: (_, _) => Task.FromException<IPAddress[]>(
                new IOException(privateDetail)),
            connect: static (_, _, _) => Task.CompletedTask);

        var dnsResult = await RunAsync(dnsError);

        Require(
            dnsResult.Dns.Status == DiagnosticCheckStatus.Error &&
            dnsResult.Dns.Code == "dns_probe_error" &&
            !JsonSerializer.Serialize(dnsResult).Contains(
                privateDetail,
                StringComparison.Ordinal),
            "A DNS probe error leaked exception text or became a target failure.");

        var tcpError = new NetworkProbe(
            connect: (_, _, _) => Task.FromException(
                new InvalidOperationException(privateDetail)));

        var tcpResult = await RunAsync(
            tcpError,
            CreateProfile("http://127.0.0.1:5045/"));

        Require(
            tcpResult.Tcp.Status == DiagnosticCheckStatus.Error &&
            tcpResult.Tcp.Code == "tcp_probe_error" &&
            !JsonSerializer.Serialize(tcpResult).Contains(
                privateDetail,
                StringComparison.Ordinal),
            "A TCP probe error leaked exception text or became a target failure.");

        var dnsCancellation = new NetworkProbe(
            resolve: static (_, _) => Task.FromCanceled<IPAddress[]>(
                new CancellationToken(true)),
            connect: static (_, _, _) => Task.CompletedTask);

        var cancelledDns = await RunAsync(dnsCancellation);

        Require(
            cancelledDns.Dns.Status == DiagnosticCheckStatus.Error &&
            cancelledDns.Dns.Code == "unexpected_cancellation",
            "Unrelated DNS cancellation was reported as a configured deadline.");

        var tcpCancellation = new NetworkProbe(
            connect: static (_, _, _) => Task.FromCanceled(
                new CancellationToken(true)));

        var cancelledTcp = await RunAsync(
            tcpCancellation,
            CreateProfile("http://127.0.0.1:5045/"));

        Require(
            cancelledTcp.Tcp.Status == DiagnosticCheckStatus.Error &&
            cancelledTcp.Tcp.Code == "unexpected_cancellation",
            "Unrelated TCP cancellation was reported as a configured deadline.");
    }

    /// <summary>Executes a probe and requires supporting roles with stable IDs.</summary>
    private static async Task<NetworkProbeResult> RunAsync(
        NetworkProbe probe,
        TargetProfile? profile = null,
        CancellationToken cancellationToken = default)
    {
        var result = await probe.ProbeAsync(
            profile ?? CreateProfile(),
            cancellationToken).WaitAsync(TimeSpan.FromSeconds(12));

        Require(
            result.Dns.Id == "dns.resolve" &&
            result.Tcp.Id == "tcp.connect" &&
            result.Dns.Role == DiagnosticCheckRole.Supporting &&
            result.Tcp.Role == DiagnosticCheckRole.Supporting,
            "Network observations changed their stable IDs or supporting roles.");

        return result;
    }

    /// <summary>Creates a validated target with short fixture operation budgets.</summary>
    private static TargetProfile CreateProfile(
        string baseUrl = "http://probe.example.test:5045/")
    {
        return new TargetProfile
        {
            SchemaVersion = 1,
            Name = "Network fixture",
            Target = new ProfileTarget
            {
                Id = "network-fixture",
                BaseUrl = baseUrl
            },
            TimeoutsMs = new ProfileTimeouts
            {
                Dns = 1000,
                Tcp = 2000,
                Run = 5000
            }
        };
    }

    /// <summary>Fails the current regression check when its condition is false.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}