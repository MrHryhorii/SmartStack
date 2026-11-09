using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Networking;
using Tsubaki.ServiceCheck.Core.Profiles;
using Tsubaki.ServiceCheck.Core.Reports;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>
/// Verifies network inspection within an active diagnostic run.
/// </summary>
internal static class NetworkIntegrationChecks
{
    private const string ToolVersion = "0.1.0-fixture";

    /// <summary>Returns network integration regression checks.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks()
    {
        return
        [
            (
                "Network failures preserve one speech request",
                VerifyNetworkFailuresAsync
            ),
            (
                "Network stage deadlines preserve the speech request",
                VerifyStageDeadlinesAsync
            ),
            (
                "Unexpected probe errors preserve the speech request",
                VerifyProbeErrorsAsync
            ),
            (
                "Run deadline during networking prevents speech",
                () => VerifyInterruptionAsync(cancelCaller: false)
            ),
            (
                "Caller cancellation during networking prevents speech",
                () => VerifyInterruptionAsync(cancelCaller: true)
            ),
            (
                "Missing credentials prevent network inspection",
                VerifyMissingCredentialsAsync
            ),
            (
                "Exported network evidence redacts bearer secrets",
                VerifySecretRedactionAsync
            )
        ];
    }

    /// <summary>Preserves the active request after DNS or TCP failure.</summary>
    private static async Task VerifyNetworkFailuresAsync()
    {
        bool[] dnsFailures = [true, false];

        foreach (var dnsFailure in dnsFailures)
        {
            var probe = new NetworkProbe(
                resolve: (_, _) => dnsFailure
                    ? Task.FromException<IPAddress[]>(
                        new SocketException((int)SocketError.HostNotFound))
                    : Task.FromResult(new[] { IPAddress.Loopback }),
                connect: (_, _, _) => Task.FromException(
                    new SocketException((int)SocketError.ConnectionRefused)));

            using var handler = new SpeechHandler();

            var result = await DiagnosticRunner.CheckAsync(
                CreateProfile(),
                ToolVersion,
                handler,
                networkProbe: probe)
                .WaitAsync(TimeSpan.FromSeconds(15));

            RequirePassedSpeech(result, handler);

            RequireNetworkCheck(
                result,
                dnsFailure ? "dns.resolve" : "tcp.connect",
                DiagnosticCheckStatus.Failed,
                dnsFailure ? "dns_resolution_failed" : "connection_refused");

            var network = result.Network
                ?? throw new InvalidOperationException(
                    "Network evidence was not preserved.");

            Require(
                network.Port == 5045,
                "The configured target port was lost.");

            if (dnsFailure)
            {
                RequireNetworkCheck(
                    result,
                    "tcp.connect",
                    DiagnosticCheckStatus.Skipped,
                    "dependency_failed");

                Require(
                    network.ResolvedAddresses.Count == 0 &&
                    network.ConnectionAttempts.Count == 0,
                    "A failed DNS lookup produced connection evidence.");

                continue;
            }

            Require(
                network.ResolvedAddresses.Count == 1 &&
                network.ConnectionAttempts.Count == 1 &&
                network.ConnectionAttempts[0].Code == "connection_refused",
                "The failed TCP attempt was not preserved.");
        }
    }

    /// <summary>Separates local network deadlines from the overall run deadline.</summary>
    private static async Task VerifyStageDeadlinesAsync()
    {
        bool[] dnsStages = [true, false];

        foreach (var stallDns in dnsStages)
        {
            var probe = new NetworkProbe(
                resolve: async (_, cancellationToken) =>
                {
                    if (stallDns)
                    {
                        await Task.Delay(
                            Timeout.InfiniteTimeSpan,
                            cancellationToken);
                    }

                    return [IPAddress.Loopback];
                },
                connect: (_, _, cancellationToken) => Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken));

            using var handler = new SpeechHandler();

            var result = await DiagnosticRunner.CheckAsync(
                CreateProfile(),
                ToolVersion,
                handler,
                networkProbe: probe)
                .WaitAsync(TimeSpan.FromSeconds(15));

            RequirePassedSpeech(result, handler);

            RequireNetworkCheck(
                result,
                stallDns ? "dns.resolve" : "tcp.connect",
                DiagnosticCheckStatus.Failed,
                stallDns ? "dns_timeout" : "connection_timeout");
        }
    }

    /// <summary>Keeps unexpected probe failures separate from speech execution.</summary>
    private static async Task VerifyProbeErrorsAsync()
    {
        const string privateDetail = "Fixture private endpoint detail.";

        Exception[] failures =
        [
            new InvalidOperationException(privateDetail),
            new OperationCanceledException(privateDetail)
        ];

        foreach (var failure in failures)
        {
            var probe = new FixtureNetworkProbe(
                (_, _, _) => Task.FromException<NetworkProbeResult>(failure));

            using var handler = new SpeechHandler();

            var result = await DiagnosticRunner.CheckAsync(
                CreateProfile(),
                ToolVersion,
                handler,
                networkProbe: probe)
                .WaitAsync(TimeSpan.FromSeconds(15));

            RequirePassedSpeech(result, handler);

            var expectedCode = failure is OperationCanceledException
                ? "unexpected_cancellation"
                : "network_probe_error";

            RequireNetworkCheck(
                result,
                "dns.resolve",
                DiagnosticCheckStatus.Error,
                expectedCode);

            RequireNetworkCheck(
                result,
                "tcp.connect",
                DiagnosticCheckStatus.Error,
                expectedCode);

            var json = Encoding.UTF8.GetString(ReportJson.Serialize(result));

            Require(
                !json.Contains(privateDetail, StringComparison.Ordinal),
                "Private probe exception text leaked into the report.");
        }
    }

    /// <summary>Preserves DNS evidence and prevents speech after interruption.</summary>
    private static async Task VerifyInterruptionAsync(bool cancelCaller)
    {
        var profile = CreateProfile() with
        {
            TimeoutsMs = new ProfileTimeouts
            {
                Dns = 5000,
                Tcp = 5000,
                Run = cancelCaller ? 10000 : 1000
            }
        };

        var connectionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var probe = new NetworkProbe(
            resolve: (_, _) => Task.FromResult(
                new[] { IPAddress.Loopback }),
            connect: async (_, _, cancellationToken) =>
            {
                connectionStarted.TrySetResult();

                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            });

        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));

        using var handler = new SpeechHandler();

        var pending = DiagnosticRunner.CheckAsync(
            profile,
            ToolVersion,
            handler,
            cancellation.Token,
            networkProbe: probe);

        try
        {
            await connectionStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            if (cancelCaller)
            {
                cancellation.Cancel();
            }

            var result = await pending.WaitAsync(
                TimeSpan.FromSeconds(5));

            var expectedState = cancelCaller
                ? DiagnosticExecutionState.Cancelled
                : DiagnosticExecutionState.TimedOut;

            var expectedCode = cancelCaller
                ? "cancelled"
                : "deadline_exceeded";

            Require(
                result.ExecutionState == expectedState &&
                result.Outcome == DiagnosticOutcome.Incomplete,
                "Network interruption did not produce an incomplete run.");

            Require(
                handler.RequestCount == 0,
                "Speech was requested after network interruption.");

            RequireNetworkCheck(
                result,
                "dns.resolve",
                DiagnosticCheckStatus.Passed,
                "dns_resolved");

            RequireNetworkCheck(
                result,
                "tcp.connect",
                DiagnosticCheckStatus.Error,
                expectedCode);

            var network = result.Network
                ?? throw new InvalidOperationException(
                    "Partial network evidence was lost.");

            Require(
                network.ResolvedAddresses.Count == 1 &&
                network.ConnectionAttempts.Count == 1 &&
                network.ConnectionAttempts[0].Code == expectedCode,
                "The completed DNS lookup or interrupted TCP attempt was lost.");

            var speech = FindCheck(result, "speech.request");

            Require(
                speech.Status == DiagnosticCheckStatus.Skipped &&
                speech.Code == expectedCode &&
                speech.Http is null,
                "The unrequested speech check was not explicitly skipped.");

            Require(
                result.Timings.SpeechTotalMs is null &&
                result.Audio is null &&
                result.AudioPayload is null,
                "Unrequested speech produced timing or audio evidence.");
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    /// <summary>Rejects missing credentials before DNS, TCP, or HTTP activity.</summary>
    private static async Task VerifyMissingCredentialsAsync()
    {
        var variableName = $"TSUBAKI_CHECK_MISSING_{Guid.NewGuid():N}";
        var previous = Environment.GetEnvironmentVariable(variableName);

        Environment.SetEnvironmentVariable(variableName, null);

        try
        {
            var profile = CreateProfile() with
            {
                Authentication = new ProfileAuthentication
                {
                    Type = "bearer",
                    TokenEnvironmentVariable = variableName
                }
            };

            var probeCalls = 0;

            var probe = new FixtureNetworkProbe((_, _, _) =>
            {
                probeCalls++;

                return Task.FromException<NetworkProbeResult>(
                    new InvalidOperationException(
                        "The network probe must not be invoked."));
            });

            using var handler = new SpeechHandler();

            var rejected = false;

            try
            {
                await DiagnosticRunner.CheckAsync(
                    profile,
                    ToolVersion,
                    handler,
                    networkProbe: probe);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            Require(
                rejected &&
                probeCalls == 0 &&
                handler.RequestCount == 0,
                "Missing credentials did not prevent network activity.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
        }
    }

    /// <summary>Redacts known credentials from network text and address evidence.</summary>
    private static async Task VerifySecretRedactionAsync()
    {
        const string token = "127.0.0.1";

        var variableName = $"TSUBAKI_CHECK_NETWORK_{Guid.NewGuid():N}";
        var previous = Environment.GetEnvironmentVariable(variableName);

        Environment.SetEnvironmentVariable(variableName, token);

        try
        {
            var profile = CreateProfile() with
            {
                Authentication = new ProfileAuthentication
                {
                    Type = "bearer",
                    TokenEnvironmentVariable = variableName
                }
            };

            var probe = new FixtureNetworkProbe(
                async (target, callerCancellation, runCancellation) =>
                {
                    var source = await FixtureNetworkProbe.Connected.ProbeAsync(
                        target,
                        callerCancellation,
                        runCancellation);

                    return source with
                    {
                        Dns = source.Dns with
                        {
                            Summary = $"Fixture resolver returned {token}.",
                            Suggestions = [$"Review fixture address {token}."]
                        },
                        Tcp = source.Tcp with
                        {
                            Summary = $"Fixture connection reached {token}.",
                            Suggestions = [$"Review fixture endpoint {token}."]
                        }
                    };
                });

            using var handler = new SpeechHandler();

            var result = await DiagnosticRunner.CheckAsync(
                profile,
                ToolVersion,
                handler,
                networkProbe: probe)
                .WaitAsync(TimeSpan.FromSeconds(15));

            RequirePassedSpeech(result, handler);

            var network = result.Network
                ?? throw new InvalidOperationException(
                    "Sanitized network evidence is missing.");

            Require(
                network.ResolvedAddresses.Count == 1 &&
                network.ResolvedAddresses[0] == "***" &&
                network.ConnectionAttempts.Count == 1 &&
                network.ConnectionAttempts[0].Address == "***",
                "The bearer value remained visible in network addresses.");

            string[] networkIds = ["dns.resolve", "tcp.connect"];

            foreach (var id in networkIds)
            {
                var check = FindCheck(result, id);

                Require(
                    check.Role == DiagnosticCheckRole.Supporting &&
                    check.Summary.Contains("***", StringComparison.Ordinal) &&
                    check.Suggestions.Count == 1 &&
                    check.Suggestions[0].Contains(
                        "***",
                        StringComparison.Ordinal),
                    "Network check text was not sanitized.");
            }

            var json = Encoding.UTF8.GetString(ReportJson.Serialize(result));

            Require(
                !json.Contains(token, StringComparison.Ordinal),
                "The bearer value leaked into the canonical report.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
        }
    }

    /// <summary>Creates a profile whose HTTP transport is supplied by the fixture.</summary>
    private static TargetProfile CreateProfile()
    {
        return new TargetProfile
        {
            SchemaVersion = 1,
            Name = "Network integration fixture",
            Target = new ProfileTarget
            {
                Id = "network-fixture",
                BaseUrl = "http://fixture.example.test:5045/proxy/"
            },
            TimeoutsMs = new ProfileTimeouts
            {
                Dns = 100,
                Tcp = 100,
                Run = 10000
            }
        };
    }

    /// <summary>Requires exactly one successful speech and audio verification.</summary>
    private static void RequirePassedSpeech(
        DiagnosticRunResult result,
        SpeechHandler handler)
    {
        Require(
            handler.RequestCount == 1 &&
            result.ExecutionState == DiagnosticExecutionState.Completed &&
            result.Outcome == DiagnosticOutcome.PassedWithWarnings,
            "Supporting network observations blocked or repeated speech.");

        string[] ids = ["speech.request", "audio.container", "audio.signal"];

        foreach (var id in ids)
        {
            var check = FindCheck(result, id);

            Require(
                check.Role == DiagnosticCheckRole.Required &&
                check.Status == DiagnosticCheckStatus.Passed,
                $"Required check '{id}' did not pass.");
        }
    }

    /// <summary>Requires one supporting network result with the expected category.</summary>
    private static void RequireNetworkCheck(
        DiagnosticRunResult result,
        string id,
        DiagnosticCheckStatus status,
        string code)
    {
        var check = FindCheck(result, id);

        Require(
            check.Role == DiagnosticCheckRole.Supporting &&
            check.Status == status &&
            check.Code == code,
            $"Network check '{id}' has an unexpected role, status, or code.");
    }

    /// <summary>Finds one check by its stable identifier.</summary>
    private static DiagnosticCheckResult FindCheck(
        DiagnosticRunResult result,
        string id)
    {
        return result.Checks.Single(check => check.Id == id);
    }

    /// <summary>Builds a valid mono PCM16 WAV containing nonzero samples.</summary>
    private static byte[] CreateWav()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(
            stream,
            Encoding.UTF8,
            leaveOpen: true);

        writer.Write("RIFF"u8);
        writer.Write(44);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(8000);
        writer.Write(16000);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(8);
        writer.Write((short)0);
        writer.Write((short)8192);
        writer.Write((short)-8192);
        writer.Write((short)0);
        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>Fails the current regression check when its condition is false.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>Counts speech requests and returns a supported WAV response.</summary>
    private sealed class SpeechHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        /// <summary>Returns one complete nonzero WAV without external HTTP access.</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;

            Require(
                request.Method == HttpMethod.Post,
                "The active fixture received an unexpected HTTP method.");

            cancellationToken.ThrowIfCancellationRequested();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(CreateWav())
            };

            response.Content.Headers.ContentType =
                new MediaTypeHeaderValue("audio/wav");

            return Task.FromResult(response);
        }
    }
}