using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tsubaki.ServiceCheck.Core.Diagnostics;

namespace Tsubaki.ServiceCheck.Core.Networking;

/// <summary>
/// Observes direct TCP connectivity across a bounded address list.
/// </summary>
internal static class TcpProbe
{
    private const int FallbackDelayMs = 250;

    /// <summary>
    /// Attempts retained addresses within one shared TCP deadline.
    /// </summary>
    internal static async Task<(
        DiagnosticCheckResult Check,
        DiagnosticConnectionEvidence[] Attempts)> ProbeAsync(
        IPAddress[] addresses,
        int port,
        int timeoutMs,
        Func<IPAddress, int, CancellationToken, Task> connect,
        CancellationToken cancellationToken,
        CancellationToken runCancellationToken)
    {
        if (cancellationToken.IsCancellationRequested ||
            runCancellationToken.IsCancellationRequested)
        {
            return (
                NetworkProbe.InterruptedCheck("tcp.connect", cancellationToken),
                []);
        }

        if (addresses.Length == 0)
        {
            return (
                NetworkProbe.CreateCheck(
                    "tcp.connect",
                    DiagnosticCheckStatus.Skipped,
                    "dependency_failed",
                    "Direct TCP inspection requires an available target IP address."),
                []);
        }

        var clock = Stopwatch.StartNew();

        using var deadline = new CancellationTokenSource(timeoutMs);
        using var winner = new CancellationTokenSource();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            runCancellationToken,
            deadline.Token,
            winner.Token);

        var context = new ProbeContext
        {
            Port = port,
            Connect = connect,
            UserCancellation = cancellationToken,
            RunCancellation = runCancellationToken,
            Deadline = deadline.Token,
            Winner = winner,
            Token = linked.Token,
            Pending = new ConcurrentQueue<(int Index, IPAddress Address)>(
                addresses.Select((address, index) =>
                    (Index: index, Address: address)))
        };

        await Task.WhenAll(
            RunWorkerAsync(context, 0),
            RunWorkerAsync(context, FallbackDelayMs));

        clock.Stop();

        var attempts = context.Attempts
            .OrderBy(attempt => attempt.Index)
            .Select(attempt => attempt.Evidence)
            .ToArray();

        return (
            CreateOverallCheck(context, attempts, clock.ElapsedMilliseconds),
            attempts);
    }

    /// <summary>
    /// Opens and disposes one socket without sending application data.
    /// </summary>
    internal static async Task ConnectEndpointAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var socket = new Socket(
            address.AddressFamily,
            SocketType.Stream,
            ProtocolType.Tcp);

        await socket.ConnectAsync(
            new IPEndPoint(address, port),
            cancellationToken);
    }

    /// <summary>
    /// Processes addresses until success, cancellation, or queue exhaustion.
    /// </summary>
    private static async Task RunWorkerAsync(
        ProbeContext context,
        int delayMs)
    {
        if (context.Token.IsCancellationRequested || context.Pending.IsEmpty)
        {
            return;
        }

        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, context.Token);
            }

            while (!context.Token.IsCancellationRequested &&
                   context.Pending.TryDequeue(out var candidate))
            {
                var attempt = await TryConnectAsync(
                    context,
                    candidate.Address);

                context.Attempts.Enqueue((candidate.Index, attempt));

                if (attempt.Status != DiagnosticCheckStatus.Passed)
                {
                    continue;
                }

                context.Winner.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (
            context.Token.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Captures one connection outcome without exporting exception text.
    /// </summary>
    private static async Task<DiagnosticConnectionEvidence> TryConnectAsync(
        ProbeContext context,
        IPAddress address)
    {
        var clock = Stopwatch.StartNew();

        try
        {
            context.Token.ThrowIfCancellationRequested();

            await context.Connect(address, context.Port, context.Token);

            return FinishAttempt(
                context,
                address,
                clock,
                DiagnosticCheckStatus.Passed,
                "tcp_connected");
        }
        catch (OperationCanceledException)
        {
            return CancelledAttempt(context, address, clock);
        }
        catch (SocketException exception)
        {
            if (context.Token.IsCancellationRequested)
            {
                return CancelledAttempt(context, address, clock);
            }

            var (status, code) = ClassifySocketFailure(exception.SocketErrorCode);

            return FinishAttempt(context, address, clock, status, code);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (context.Token.IsCancellationRequested)
            {
                return CancelledAttempt(context, address, clock);
            }

            return FinishAttempt(
                context,
                address,
                clock,
                DiagnosticCheckStatus.Error,
                "tcp_probe_error");
        }
    }

    /// <summary>
    /// Distinguishes caller, run, alternative-success, and TCP interruptions.
    /// </summary>
    private static DiagnosticConnectionEvidence CancelledAttempt(
        ProbeContext context,
        IPAddress address,
        Stopwatch clock)
    {
        if (context.UserCancellation.IsCancellationRequested)
        {
            return FinishAttempt(
                context,
                address,
                clock,
                DiagnosticCheckStatus.Skipped,
                "cancelled");
        }

        if (context.RunCancellation.IsCancellationRequested)
        {
            return FinishAttempt(
                context,
                address,
                clock,
                DiagnosticCheckStatus.Skipped,
                "deadline_exceeded");
        }

        if (context.Winner.IsCancellationRequested)
        {
            return FinishAttempt(
                context,
                address,
                clock,
                DiagnosticCheckStatus.Skipped,
                "alternative_succeeded");
        }

        if (context.Deadline.IsCancellationRequested)
        {
            return FinishAttempt(
                context,
                address,
                clock,
                DiagnosticCheckStatus.Failed,
                "connection_timeout");
        }

        return FinishAttempt(
            context,
            address,
            clock,
            DiagnosticCheckStatus.Error,
            "unexpected_cancellation");
    }

    /// <summary>
    /// Creates immutable evidence for a completed connection attempt.
    /// </summary>
    private static DiagnosticConnectionEvidence FinishAttempt(
        ProbeContext context,
        IPAddress address,
        Stopwatch clock,
        DiagnosticCheckStatus status,
        string code)
    {
        return new DiagnosticConnectionEvidence(
            address.ToString(),
            context.Port,
            status,
            code,
            clock.ElapsedMilliseconds);
    }

    /// <summary>
    /// Aggregates attempts while preserving successful fallback evidence.
    /// </summary>
    private static DiagnosticCheckResult CreateOverallCheck(
        ProbeContext context,
        DiagnosticConnectionEvidence[] attempts,
        long durationMs)
    {
        if (attempts.Any(attempt =>
            attempt.Status == DiagnosticCheckStatus.Passed))
        {
            return NetworkProbe.CreateCheck(
                "tcp.connect",
                DiagnosticCheckStatus.Passed,
                "tcp_connected",
                "Direct TCP connectivity was observed.",
                durationMs);
        }

        if (context.UserCancellation.IsCancellationRequested ||
            context.RunCancellation.IsCancellationRequested)
        {
            return NetworkProbe.InterruptedCheck(
                "tcp.connect",
                context.UserCancellation,
                attempts.Length == 0 ? null : durationMs);
        }

        var timedOut = attempts.Any(attempt =>
            attempt.Code == "connection_timeout");

        if (timedOut ||
            (context.Deadline.IsCancellationRequested &&
             !context.Pending.IsEmpty))
        {
            return NetworkProbe.CreateCheck(
                "tcp.connect",
                DiagnosticCheckStatus.Failed,
                "connection_timeout",
                context.Deadline.IsCancellationRequested
                    ? "Direct TCP inspection exhausted its configured deadline."
                    : "A direct TCP connection attempt timed out.",
                durationMs,
                ["Check the configured endpoint and the direct network path."]);
        }

        if (attempts.Length == 0)
        {
            return NetworkProbe.CreateCheck(
                "tcp.connect",
                DiagnosticCheckStatus.Error,
                "tcp_probe_error",
                "Direct TCP inspection produced no connection evidence.",
                durationMs);
        }

        var codes = attempts
            .Select(attempt => attempt.Code)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var status = attempts.Any(attempt =>
            attempt.Status == DiagnosticCheckStatus.Error)
                ? DiagnosticCheckStatus.Error
                : DiagnosticCheckStatus.Failed;

        var code = codes.Length == 1
            ? codes[0]
            : status == DiagnosticCheckStatus.Error
                ? "tcp_probe_error"
                : "connection_failed";

        return NetworkProbe.CreateCheck(
            "tcp.connect",
            status,
            code,
            GetFailureSummary(code),
            durationMs,
            GetSuggestions(code));
    }

    /// <summary>
    /// Classifies selected socket failures into stable observation codes.
    /// </summary>
    private static (
        DiagnosticCheckStatus Status,
        string Code) ClassifySocketFailure(SocketError error)
    {
        return error switch
        {
            SocketError.ConnectionRefused =>
                (DiagnosticCheckStatus.Failed, "connection_refused"),
            SocketError.TimedOut =>
                (DiagnosticCheckStatus.Failed, "connection_timeout"),
            SocketError.NetworkUnreachable =>
                (DiagnosticCheckStatus.Failed, "network_unreachable"),
            SocketError.HostUnreachable =>
                (DiagnosticCheckStatus.Failed, "host_unreachable"),
            SocketError.AccessDenied =>
                (DiagnosticCheckStatus.Error, "connection_access_denied"),
            SocketError.AddressFamilyNotSupported or
            SocketError.ProtocolNotSupported or
            SocketError.OperationNotSupported =>
                (DiagnosticCheckStatus.Error, "address_family_unsupported"),
            _ => (DiagnosticCheckStatus.Failed, "connection_failed")
        };
    }

    /// <summary>
    /// Describes observed connection failures without guessing a server cause.
    /// </summary>
    private static string GetFailureSummary(string code)
    {
        return code switch
        {
            "connection_refused" =>
                "The configured port refused direct TCP connections.",
            "connection_timeout" =>
                "Direct TCP connections did not complete within the allowed interval.",
            "network_unreachable" =>
                "The network was unreachable from this client.",
            "host_unreachable" =>
                "The target address was unreachable from this client.",
            "connection_access_denied" =>
                "The client was denied permission to open a direct TCP connection.",
            "address_family_unsupported" =>
                "The client could not inspect the returned address family.",
            "tcp_probe_error" =>
                "The client could not complete direct TCP inspection.",
            _ => "The client could not establish a direct TCP connection."
        };
    }

    /// <summary>
    /// Supplies next steps appropriate to the observed failure category.
    /// </summary>
    private static string[] GetSuggestions(string code)
    {
        return code switch
        {
            "connection_refused" =>
            [
                "Check the target URL, listening address, port, and service state."
            ],
            "connection_access_denied" =>
            [
                "Check client process permissions and local network access policy."
            ],
            "address_family_unsupported" =>
            [
                "Check client support for the resolved IP address families."
            ],
            _ =>
            [
                "Check the configured endpoint and the direct network path."
            ]
        };
    }

    /// <summary>
    /// Shares bounded work and cancellation state between two connection workers.
    /// </summary>
    private sealed class ProbeContext
    {
        public required int Port { get; init; }

        public required Func<
            IPAddress,
            int,
            CancellationToken,
            Task> Connect
        { get; init; }

        public required CancellationToken UserCancellation { get; init; }

        public required CancellationToken RunCancellation { get; init; }

        public required CancellationToken Deadline { get; init; }

        public required CancellationTokenSource Winner { get; init; }

        public required CancellationToken Token { get; init; }

        public required ConcurrentQueue<(
            int Index,
            IPAddress Address)> Pending
        { get; init; }

        public ConcurrentQueue<(
            int Index,
            DiagnosticConnectionEvidence Evidence)> Attempts
        { get; } = new();
    }
}