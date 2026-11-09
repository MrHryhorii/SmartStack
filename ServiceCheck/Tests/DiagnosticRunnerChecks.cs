using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Tsubaki.ServiceCheck.Core.Audio;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>
/// Verifies diagnostic execution with controlled HTTP responses.
/// </summary>
internal static class DiagnosticRunnerChecks
{
    private const string ToolVersion = "0.1.0-fixture";

    /// <summary>Returns diagnostic execution regression checks.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks()
    {
        return
        [
            ("Diagnostic run verifies one speech response", VerifySuccessfulRunAsync),
            ("Failed speech skips dependent audio checks", VerifyHttpFailureAsync),
            ("HTTP 200 error document fails audio verification", VerifyInvalidPayloadAsync),
            ("Silent audio fails the required signal check", VerifySilentAudioAsync),
            ("Unsupported audio leaves the run incomplete", VerifyUnsupportedAudioAsync),
            ("WAV media-type discrepancies remain warnings", VerifyMediaTypesAsync),
            ("WAV header overrides reported sample rate", VerifySampleRateWarningAsync),
            ("Disabled retention preserves audio metrics", VerifyDisabledRetentionAsync),
            ("Run deadline preserves partial HTTP evidence", VerifyRunDeadlineAsync),
            ("Run cancellation preserves partial HTTP evidence", VerifyCancellationAsync)
        ];
    }

    /// <summary>Verifies the complete successful speech and audio path.</summary>
    private static async Task VerifySuccessfulRunAsync()
    {
        var payload = CreatePcm16(0, 8192, -8192, 0);
        var result = await RunFixtureAsync(payload);

        Require(
            result.ExecutionState == DiagnosticExecutionState.Completed,
            "The successful run did not complete.");

        Require(
            result.Outcome == DiagnosticOutcome.PassedWithWarnings,
            "Missing supporting coverage must remain visible.");

        RequireRequiredPasses(result);

        Require(
            result.Audio?.Inspection.SignalState == AudioSignalState.Nonzero,
            "Nonzero audio evidence is missing.");

        Require(
            result.AudioPayload is { } retained &&
            retained.AsSpan().SequenceEqual(payload),
            "The supported WAV was not preserved for artifact publication.");
    }

    /// <summary>Verifies that an HTTP failure blocks dependent audio checks.</summary>
    private static async Task VerifyHttpFailureAsync()
    {
        var payload = Encoding.UTF8.GetBytes(
            """{"error":"Fixture service unavailable."}""");

        var result = await RunFixtureAsync(
            payload,
            statusCode: HttpStatusCode.ServiceUnavailable,
            mediaType: "application/json");

        Require(
            result.ExecutionState == DiagnosticExecutionState.Completed &&
            result.Outcome == DiagnosticOutcome.Failed,
            "An HTTP failure must produce a completed failed run.");

        Require(
            FindCheck(result, "speech.request").Status ==
                DiagnosticCheckStatus.Failed,
            "The HTTP failure was not recorded.");

        Require(
            FindCheck(result, "speech.request").Http?.StatusCode == 503,
            "The HTTP status evidence is missing.");

        Require(
            FindCheck(result, "audio.container").Code == "dependency_failed" &&
            FindCheck(result, "audio.signal").Code == "dependency_failed",
            "Dependent checks were not explicitly skipped.");

        Require(
            result.Audio is null && result.AudioPayload is null,
            "Failed HTTP responses must not become audio artifacts.");
    }

    /// <summary>Verifies that HTTP success alone cannot establish audio success.</summary>
    private static async Task VerifyInvalidPayloadAsync()
    {
        var payload = Encoding.UTF8.GetBytes(
            "<html><body>Fixture login page.</body></html>");

        var result = await RunFixtureAsync(
            payload,
            mediaType: "text/html");

        Require(
            FindCheck(result, "speech.request").Status ==
                DiagnosticCheckStatus.Passed,
            "The complete HTTP 200 response was not recorded.");

        Require(
            FindCheck(result, "audio.container").Status ==
                DiagnosticCheckStatus.Failed,
            "The error document was accepted as WAV.");

        Require(
            FindCheck(result, "audio.signal").Status ==
                DiagnosticCheckStatus.Skipped,
            "Signal analysis did not respect its container dependency.");

        Require(
            result.Outcome == DiagnosticOutcome.Failed &&
            result.AudioPayload is null,
            "Invalid audio must fail the run and remain unavailable for retention.");
    }

    /// <summary>Verifies that a valid all-zero WAV fails signal verification.</summary>
    private static async Task VerifySilentAudioAsync()
    {
        var result = await RunFixtureAsync(CreatePcm16(0, 0, 0, 0));

        Require(
            FindCheck(result, "audio.container").Status ==
                DiagnosticCheckStatus.Passed,
            "The valid silent WAV container was rejected.");

        Require(
            FindCheck(result, "audio.signal").Status ==
                DiagnosticCheckStatus.Failed,
            "Silent audio did not fail signal verification.");

        Require(
            result.Outcome == DiagnosticOutcome.Failed,
            "Silent audio must fail the active check.");

        Require(
            result.AudioPayload is { Length: > 0 },
            "A supported silent WAV should remain available as retained evidence.");
    }

    /// <summary>Verifies that unsupported audio produces incomplete coverage.</summary>
    private static async Task VerifyUnsupportedAudioAsync()
    {
        var payload = CreatePcmWav([128, 160, 96, 128], 8);
        var result = await RunFixtureAsync(payload);

        Require(
            result.ExecutionState == DiagnosticExecutionState.Completed &&
            result.Outcome == DiagnosticOutcome.Incomplete,
            "Unsupported audio must produce a completed but incomplete run.");

        Require(
            FindCheck(result, "audio.container").Status ==
                DiagnosticCheckStatus.Error,
            "Unsupported container coverage was not recorded.");

        Require(
            FindCheck(result, "audio.signal").Status ==
                DiagnosticCheckStatus.Skipped,
            "Unsupported audio must skip signal analysis.");

        Require(
            result.AudioPayload is null,
            "Unsupported audio must not become a retained WAV artifact.");
    }

    /// <summary>Preserves successful audio checks despite media-type discrepancies.</summary>
    private static async Task VerifyMediaTypesAsync()
    {
        string?[] mediaTypes = [null, "text/html"];

        foreach (var mediaType in mediaTypes)
        {
            var result = await RunFixtureAsync(
                CreatePcm16(0, 8192, -8192, 0),
                mediaType: mediaType);

            RequireRequiredPasses(result);

            Require(
                FindCheck(result, "speech.media_type").Status ==
                    DiagnosticCheckStatus.Warning,
                "A missing or incorrect WAV media type must produce a warning.");

            Require(
                result.Outcome == DiagnosticOutcome.PassedWithWarnings,
                "Media-type discrepancies must preserve successful audio verification.");
        }
    }

    /// <summary>Uses the WAV header as the authority for received sample rate.</summary>
    private static async Task VerifySampleRateWarningAsync()
    {
        var result = await RunFixtureAsync(
            CreatePcm16(0, 8192, -8192, 0),
            reportedSampleRate: 16000);

        RequireRequiredPasses(result);

        Require(
            result.Audio?.Inspection.SampleRateHz == 8000,
            "The response header replaced the observed WAV sample rate.");

        Require(
            FindCheck(result, "audio.sample_rate_header").Status ==
                DiagnosticCheckStatus.Warning,
            "The conflicting sample-rate header was not reported.");
    }

    /// <summary>Preserves inspection results when audio retention is disabled.</summary>
    private static async Task VerifyDisabledRetentionAsync()
    {
        var profile = CreateProfile() with
        {
            Output = new ProfileOutput { RetainAudio = false }
        };

        var result = await RunFixtureAsync(
            CreatePcm16(0, 8192, -8192, 0),
            profile: profile);

        RequireRequiredPasses(result);

        Require(
            result.Audio?.Inspection.PayloadSha256 is { Length: 64 },
            "Audio metrics and its payload hash must remain available.");

        Require(
            result.AudioPayload is null,
            "Disabled retention must discard the payload before publication.");
    }

    /// <summary>Preserves response evidence when the overall deadline expires.</summary>
    private static async Task VerifyRunDeadlineAsync()
    {
        var profile = CreateProfile() with
        {
            TimeoutsMs = new ProfileTimeouts
            {
                Speech = 5000,
                Run = 1000
            }
        };

        var stream = new StalledBodyStream(CreatePcm16(0, 8192)[..16]);
        var handler = CreateStalledHandler(stream);

        var result = await DiagnosticRunner.CheckAsync(
            profile,
            ToolVersion,
            new FixtureMetadataHandler(handler),
            networkProbe: FixtureNetworkProbe.Connected)
            .WaitAsync(TimeSpan.FromSeconds(10));

        RequirePartialResult(result, DiagnosticExecutionState.TimedOut);

        Require(
            handler.RequestCount == 1 && stream.Disposed,
            "The timed-out request was repeated or its body was not disposed.");
    }

    /// <summary>Preserves response evidence when the caller cancels execution.</summary>
    private static async Task VerifyCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));

        var stream = new StalledBodyStream(CreatePcm16(0, 8192)[..16]);
        var handler = CreateStalledHandler(stream);

        var pending = DiagnosticRunner.CheckAsync(
            CreateProfile(),
            ToolVersion,
            new FixtureMetadataHandler(handler),
            cancellation.Token,
            networkProbe: FixtureNetworkProbe.Connected);

        try
        {
            await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

            RequirePartialResult(result, DiagnosticExecutionState.Cancelled);

            Require(
                handler.RequestCount == 1 && stream.Disposed,
                "The cancelled request was repeated or its body was not disposed.");
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    /// <summary>Runs one response fixture and enforces the single-request contract.</summary>
    private static async Task<DiagnosticRunResult> RunFixtureAsync(
        byte[] payload,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? mediaType = "audio/wav",
        TargetProfile? profile = null,
        int? reportedSampleRate = null)
    {
        var handler = new FixtureHandler((_, _) =>
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(payload)
            };

            if (mediaType is not null)
            {
                response.Content.Headers.ContentType =
                    new MediaTypeHeaderValue(mediaType);
            }

            if (reportedSampleRate is { } sampleRate)
            {
                response.Headers.TryAddWithoutValidation(
                    "X-Audio-Sample-Rate",
                    sampleRate.ToString(CultureInfo.InvariantCulture));
            }

            return Task.FromResult(response);
        });

        var result = await DiagnosticRunner.CheckAsync(
            profile ?? CreateProfile(),
            ToolVersion,
            new FixtureMetadataHandler(handler),
            networkProbe: FixtureNetworkProbe.Connected);

        Require(
            handler.RequestCount == 1,
            "An active diagnostic run must send exactly one request.");

        return result;
    }

    /// <summary>Creates a response whose body stalls after a known prefix.</summary>
    private static FixtureHandler CreateStalledHandler(StalledBodyStream stream)
    {
        return new FixtureHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };

            response.Content.Headers.ContentType =
                new MediaTypeHeaderValue("audio/wav");

            return Task.FromResult(response);
        });
    }

    /// <summary>Creates a valid default diagnostic profile.</summary>
    private static TargetProfile CreateProfile()
    {
        return new TargetProfile
        {
            SchemaVersion = 1,
            Name = "Diagnostic fixture",
            Target = new ProfileTarget
            {
                Id = "fixture",
                BaseUrl = "http://127.0.0.1:5045/"
            }
        };
    }

    /// <summary>Builds a small mono PCM16 WAV fixture.</summary>
    private static byte[] CreatePcm16(params short[] samples)
    {
        var data = new byte[checked(samples.Length * 2)];

        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                data.AsSpan(index * 2, 2),
                samples[index]);
        }

        return CreatePcmWav(data, 16);
    }

    /// <summary>Builds a little-endian mono PCM WAV with an 8000 Hz sample rate.</summary>
    private static byte[] CreatePcmWav(byte[] data, ushort bitsPerSample)
    {
        const uint sampleRate = 8000;

        var blockAlign = (ushort)(bitsPerSample / 8);
        var padding = data.Length & 1;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write("RIFF"u8);
        writer.Write(checked(36 + data.Length + padding));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);

        if (padding != 0)
        {
            writer.Write((byte)0);
        }

        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>Finds one check by its stable identifier.</summary>
    private static DiagnosticCheckResult FindCheck(
        DiagnosticRunResult result,
        string id)
    {
        return result.Checks.Single(check => check.Id == id);
    }

    /// <summary>Requires all three active checks to have passed.</summary>
    private static void RequireRequiredPasses(DiagnosticRunResult result)
    {
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

    /// <summary>Requires incomplete execution with preserved partial HTTP evidence.</summary>
    private static void RequirePartialResult(
        DiagnosticRunResult result,
        DiagnosticExecutionState executionState)
    {
        Require(
            result.ExecutionState == executionState &&
            result.Outcome == DiagnosticOutcome.Incomplete,
            "Interrupted execution did not produce an incomplete result.");

        var speech = FindCheck(result, "speech.request");

        Require(
            speech.Status == DiagnosticCheckStatus.Error &&
            speech.Http is
            {
                StatusCode: 200,
                BodyBytesRead: 16,
                BodyComplete: false
            },
            "Partial HTTP status and body evidence were lost.");

        Require(
            result.Timings.SpeechHeadersMs is not null &&
            result.Timings.SpeechBodyMs is not null,
            "Observed HTTP timings were lost.");

        Require(
            FindCheck(result, "audio.container").Status ==
                DiagnosticCheckStatus.Skipped &&
            FindCheck(result, "audio.signal").Status ==
                DiagnosticCheckStatus.Skipped,
            "Interrupted responses must skip dependent audio checks.");

        Require(
            result.Audio is null && result.AudioPayload is null,
            "An incomplete response must not become an audio artifact.");
    }

    /// <summary>Fails the current regression check when its condition is false.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>Supplies controlled responses and counts requests.</summary>
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> _send;

        public int RequestCount { get; private set; }

        /// <summary>Stores the response factory used by this handler.</summary>
        public FixtureHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        /// <summary>Counts and dispatches one HTTP request.</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;

            return _send(request, cancellationToken);
        }
    }

    /// <summary>Returns a prefix, then waits for cancellation.</summary>
    private sealed class StalledBodyStream : Stream
    {
        private readonly byte[] _prefix;
        private int _position;

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public override bool CanRead => !Disposed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>Stores the bytes returned before the body stalls.</summary>
        public StalledBodyStream(byte[] prefix)
        {
            _prefix = prefix;
        }

        /// <summary>Returns available prefix bytes, then awaits cancellation.</summary>
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_position < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _position);

                _prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;

                return count;
            }

            ReadStarted.TrySetResult();

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return 0;
        }

        /// <summary>Rejects synchronous reads.</summary>
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        /// <summary>Rejects flushing a read-only fixture.</summary>
        public override void Flush() => throw new NotSupportedException();

        /// <summary>Rejects seeking.</summary>
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        /// <summary>Rejects resizing.</summary>
        public override void SetLength(long value) =>
            throw new NotSupportedException();

        /// <summary>Rejects writing.</summary>
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        /// <summary>Records disposal of the response stream.</summary>
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}