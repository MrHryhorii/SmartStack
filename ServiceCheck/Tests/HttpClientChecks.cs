using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Http;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>Defines regression checks for the speech HTTP client.</summary>
internal static class HttpClientChecks
{
    private static readonly byte[] Payload = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>Registers HTTP checks for the shared regression runner.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks() =>
    [
        ("Speech HTTP request contract verified", VerifyRequestAsync),
        ("Unexpected HTTP success status rejected", VerifyUnexpectedStatusAsync),
        ("HTTP 429 returned without retry", VerifyRateLimitAsync),
        ("Redirect evidence sanitized", VerifyRedirectAsync),
        ("Declared audio limit enforced", VerifyDeclaredLimitAsync),
        ("Unknown-length audio limit enforced", VerifyStreamingLimitAsync),
        ("Content-Length mismatch rejected", VerifyLengthMismatchAsync),
        ("Error excerpt bounded", VerifyErrorExcerptAsync),
        ("Speech body deadline enforced", () => VerifyBodyTimeoutAsync(false)),
        ("Run body deadline enforced", () => VerifyBodyTimeoutAsync(true)),
        ("Body read cancellation honored", VerifyCancellationAsync),
        ("Bearer header and secret redaction verified", VerifyBearerAsync),
        ("Missing token rejected before sending", VerifyMissingTokenAsync),
        ("DNS failure classified", () => VerifyTransportFailureAsync(
            HttpRequestError.NameResolutionError, "dns_resolution_failed")),
        ("TLS failure classified", () => VerifyTransportFailureAsync(
            HttpRequestError.SecureConnectionError, "tls_handshake_failed"))
    ];

    /// <summary>Creates a valid profile for prepared HTTP responses.</summary>
    private static TargetProfile CreateProfile() => new()
    {
        SchemaVersion = 1,
        Name = "HTTP fixture",
        Target = new ProfileTarget
        {
            Id = "http-fixture",
            BaseUrl = "https://tts.example.test/speech/"
        },
        TimeoutsMs = new ProfileTimeouts
        {
            Speech = 5_000,
            Run = 10_000
        }
    };

    /// <summary>Verifies the wire request and a fully received response.</summary>
    private static async Task VerifyRequestAsync()
    {
        var profile = CreateProfile();

        using var handler = new StubHandler(async (request, token) =>
        {
            Require(request.Method == HttpMethod.Post, "Expected POST.");
            Require(
                request.RequestUri?.AbsoluteUri
                    == "https://tts.example.test/speech/v1/audio/speech",
                "The proxy prefix was not preserved.");

            var content = request.Content
                ?? throw new InvalidOperationException("Request body is missing.");

            Require(
                content.Headers.ContentType?.MediaType == "application/json",
                "Expected JSON request content.");

            using var document = JsonDocument.Parse(
                await content.ReadAsStringAsync(token));

            var root = document.RootElement;

            Require(root.GetProperty("model").GetString() == "tts-1",
                "Incorrect model.");
            Require(root.GetProperty("voice").GetString() == "piper_base",
                "Incorrect voice.");
            Require(root.GetProperty("input").GetString() == profile.Synthesis.Input,
                "Incorrect input.");
            Require(root.GetProperty("response_format").GetString() == "wav",
                "Incorrect response format mapping.");
            Require(root.GetProperty("speed").GetDouble() == 1.0,
                "Incorrect speed.");
            Require(!root.GetProperty("stream").GetBoolean(),
                "Streaming must be disabled.");
            Require(!root.TryGetProperty("responseFormat", out _),
                "Profile property names leaked into the wire contract.");

            var response = CreateResponse(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation(
                "X-Audio-Sample-Rate", "22050");

            return response;
        });

        using var client = new TsubakiClient(profile, handler);
        var result = await client.RequestSpeechAsync();

        Require(result.State == SpeechRequestState.Completed,
            "The request did not complete.");
        Require(result.StatusCode == 200 && result.BodyComplete,
            "HTTP evidence is incomplete.");
        Require(result.BodyBytesRead == Payload.Length,
            "Incorrect received byte count.");
        Require(result.Payload?.SequenceEqual(Payload) == true,
            "Response bytes changed.");
        Require(result.ReportedSampleRateHz == 22050,
            "The selected audio header was not captured.");
        Require(result.HeadersDurationMs is not null
            && result.BodyDurationMs is not null,
            "Request timings are missing.");
        Require(handler.Requests == 1, "Expected exactly one request.");
        Require(!JsonSerializer.Serialize(result).Contains(
            "\"Payload\"", StringComparison.Ordinal),
            "Binary payload was included in JSON.");
    }

    /// <summary>Requires HTTP 200 rather than any successful status.</summary>
    private static async Task VerifyUnexpectedStatusAsync()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.Created)));

        using var client = new TsubakiClient(CreateProfile(), handler);
        var result = await client.RequestSpeechAsync();

        Require(result.State == SpeechRequestState.Failed
            && result.Code == "http_unexpected_status"
            && result.StatusCode == 201,
            "HTTP 201 was incorrectly accepted.");
    }

    /// <summary>Verifies rate limiting without an automatic POST retry.</summary>
    private static async Task VerifyRateLimitAsync()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.TooManyRequests,
                Encoding.UTF8.GetBytes("Rate limited."),
                "text/plain")));

        using var client = new TsubakiClient(CreateProfile(), handler);
        var result = await client.RequestSpeechAsync();

        Require(result.State == SpeechRequestState.Failed
            && result.Code == "http_rate_limited",
            "Rate limiting was not classified.");
        Require(handler.Requests == 1, "The POST request was retried.");
    }

    /// <summary>Verifies safe redirect evidence without a second request.</summary>
    private static async Task VerifyRedirectAsync()
    {
        using var handler = new StubHandler((_, _) =>
        {
            var response = CreateResponse(HttpStatusCode.Redirect);

            response.Headers.Location = new Uri(
                "https://user:password@other.example.test/next?token=hidden#part");

            return Task.FromResult(response);
        });

        using var client = new TsubakiClient(CreateProfile(), handler);
        var result = await client.RequestSpeechAsync();

        Require(result.Code == "http_redirect",
            "The redirect was not classified.");
        Require(result.RedirectLocation == "https://other.example.test/next",
            "Redirect credentials, query, or fragment were retained.");
        Require(handler.Requests == 1, "The client sent another request.");
    }

    /// <summary>Rejects a declared oversized body before reading it.</summary>
    private static async Task VerifyDeclaredLimitAsync()
    {
        var profile = CreateProfile();
        profile = profile with
        {
            Limits = profile.Limits with { MaxAudioBytes = 4 }
        };

        using var handler = new StubHandler((_, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK)));

        using var client = new TsubakiClient(profile, handler);
        var result = await client.RequestSpeechAsync();

        Require(result.Code == "response_limit_exceeded"
            && result.BodyBytesRead == 0
            && result.BodyDurationMs is null
            && result.Payload is null,
            "The declared size limit was not enforced before reading.");
    }

    /// <summary>Enforces the byte limit when Content-Length is unavailable.</summary>
    private static async Task VerifyStreamingLimitAsync()
    {
        var profile = CreateProfile();
        profile = profile with
        {
            Limits = profile.Limits with { MaxAudioBytes = 4 }
        };

        using var stream = new FixtureStream(Payload);
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            }));

        using var client = new TsubakiClient(profile, handler);
        var result = await client.RequestSpeechAsync();

        Require(result.DeclaredContentLength is null,
            "The fixture unexpectedly declared a body length.");
        Require(result.Code == "response_limit_exceeded"
            && result.BodyBytesRead == 5
            && !result.BodyComplete
            && result.Payload is null,
            "The streamed response exceeded its read budget.");
        Require(stream.IsDisposed, "The response stream was not released.");
    }

    /// <summary>Rejects a body inconsistent with its declared length.</summary>
    private static async Task VerifyLengthMismatchAsync()
    {
        using var handler = new StubHandler((_, _) =>
        {
            var response = CreateResponse(HttpStatusCode.OK);
            response.Content.Headers.ContentLength = Payload.Length + 1;

            return Task.FromResult(response);
        });

        using var client = new TsubakiClient(CreateProfile(), handler);
        var result = await client.RequestSpeechAsync();

        Require(result.Code == "response_length_mismatch"
            && !result.BodyComplete
            && result.Payload is null,
            "The Content-Length mismatch was not rejected.");
    }

    /// <summary>Bounds error bodies while preserving their HTTP failure.</summary>
    private static async Task VerifyErrorExcerptAsync()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.ServiceUnavailable,
                Encoding.UTF8.GetBytes(new string('x', 10_000)),
                "text/plain")));

        using var client = new TsubakiClient(CreateProfile(), handler);
        var result = await client.RequestSpeechAsync();

        Require(result.Code == "http_server_error"
            && result.StatusCode == 503,
            "The HTTP failure was lost.");
        Require(result.BodyBytesRead == 4097
            && result.ErrorExcerptTruncated
            && result.ErrorExcerpt?.Length <= 4096
            && result.Payload is null,
            "The error excerpt was not bounded.");
    }

    /// <summary>Verifies that configured deadlines cover stalled body reads.</summary>
    private static async Task VerifyBodyTimeoutAsync(bool runDeadline)
    {
        var profile = CreateProfile();
        profile = profile with
        {
            TimeoutsMs = profile.TimeoutsMs with
            {
                Speech = runDeadline ? 5_000 : 500,
                Run = runDeadline ? 500 : 5_000
            }
        };

        using var stream = new FixtureStream(null);
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            }));

        using var client = new TsubakiClient(profile, handler);
        var result = await client.RequestSpeechAsync();

        Require(result.State == SpeechRequestState.TimedOut
            && result.Code == "deadline_exceeded"
            && result.StatusCode == 200
            && !result.BodyComplete,
            "The deadline did not interrupt the body read.");
        Require(stream.IsDisposed, "The stalled stream was not released.");
    }

    /// <summary>Verifies user cancellation after response headers arrive.</summary>
    private static async Task VerifyCancellationAsync()
    {
        using var stream = new FixtureStream(null);
        using var cancellation = new CancellationTokenSource();

        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            }));

        using var client = new TsubakiClient(CreateProfile(), handler);
        var pending = client.RequestSpeechAsync(cancellation.Token);

        try
        {
            await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            var result = await pending;

            Require(result.State == SpeechRequestState.Cancelled
                && result.StatusCode == 200
                && !result.BodyComplete,
                "User cancellation was not preserved.");
            Require(stream.IsDisposed, "The cancelled stream was not released.");
        }
        finally
        {
            cancellation.Cancel();
            await pending;
        }
    }

    /// <summary>Verifies bearer authentication and diagnostic redaction.</summary>
    private static async Task VerifyBearerAsync()
    {
        const string token = "fixture-token_123+/=";
        var variable = "TSUBAKI_CHECK_TEST_" + Guid.NewGuid().ToString("N");

        Environment.SetEnvironmentVariable(variable, token);

        try
        {
            var profile = CreateProfile() with
            {
                Authentication = new ProfileAuthentication
                {
                    Type = "bearer",
                    TokenEnvironmentVariable = variable
                }
            };

            using var handler = new StubHandler((request, _) =>
            {
                Require(request.Headers.Authorization?.Scheme == "Bearer"
                    && request.Headers.Authorization.Parameter == token,
                    "The bearer header is incorrect.");

                var text = $"Failure: {token}; input: {profile.Synthesis.Input}";

                var response = CreateResponse(
                    HttpStatusCode.Unauthorized,
                    Encoding.UTF8.GetBytes(text),
                    "text/plain");

                response.Headers.TryAddWithoutValidation(
                    "Set-Cookie", "session=private-fixture-cookie");

                return Task.FromResult(response);
            });

            using var client = new TsubakiClient(profile, handler);
            var result = await client.RequestSpeechAsync();
            var json = JsonSerializer.Serialize(result);

            Require(result.Code == "http_unauthorized",
                "The authentication failure was not classified.");
            Require(result.ErrorExcerpt is not null,
                "The error excerpt is missing.");
            Require(!json.Contains(token, StringComparison.Ordinal)
                && !json.Contains(profile.Synthesis.Input, StringComparison.Ordinal)
                && !json.Contains("private-fixture-cookie", StringComparison.Ordinal),
                "Sensitive data was retained in diagnostics.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>Rejects a missing credential before invoking the HTTP handler.</summary>
    private static Task VerifyMissingTokenAsync()
    {
        var profile = CreateProfile() with
        {
            Authentication = new ProfileAuthentication
            {
                Type = "bearer",
                TokenEnvironmentVariable =
                    "TSUBAKI_CHECK_MISSING_" + Guid.NewGuid().ToString("N")
            }
        };

        using var handler = new StubHandler((_, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK)));

        try
        {
            using var client = new TsubakiClient(profile, handler);
        }
        catch (ArgumentException)
        {
            Require(handler.Requests == 0,
                "A request was sent before credential validation.");

            return Task.CompletedTask;
        }

        throw new InvalidOperationException("The missing token was accepted.");
    }

    /// <summary>Verifies classification without exposing exception messages.</summary>
    private static async Task VerifyTransportFailureAsync(
        HttpRequestError error,
        string expectedCode)
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException(
                    error,
                    "Private fixture exception detail.",
                    null,
                    null)));

        using var client = new TsubakiClient(CreateProfile(), handler);
        var result = await client.RequestSpeechAsync();

        Require(result.State == SpeechRequestState.Failed
            && result.Code == expectedCode
            && result.StatusCode is null,
            "The transport failure was not classified.");
        Require(!result.Summary.Contains(
            "Private fixture exception detail.", StringComparison.Ordinal),
            "The raw exception message was exported.");
    }

    /// <summary>Creates a prepared response with controlled content.</summary>
    private static HttpResponseMessage CreateResponse(
        HttpStatusCode status,
        byte[]? bytes = null,
        string? mediaType = "audio/wav")
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(bytes ?? Payload)
        };

        if (mediaType is not null)
            response.Content.Headers.ContentType =
                new MediaTypeHeaderValue(mediaType);

        return response;
    }

    /// <summary>Fails a regression check when its condition is false.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    /// <summary>Returns prepared responses and counts outgoing requests.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> _send;

        public int Requests { get; private set; }

        /// <summary>Stores the response function used by this fixture.</summary>
        public StubHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        /// <summary>Counts and dispatches one outgoing request.</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            return _send(request, cancellationToken);
        }
    }

    /// <summary>Provides unknown-length data or a cancellable stalled read.</summary>
    private sealed class FixtureStream : Stream
    {
        private readonly MemoryStream? _data;

        public bool IsDisposed { get; private set; }

        public TaskCompletionSource<bool> ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>Uses null data to simulate a response that stops sending bytes.</summary>
        public FixtureStream(byte[]? bytes)
        {
            _data = bytes is null ? null : new MemoryStream(bytes, writable: false);
        }

        /// <summary>Signals reading and returns data or waits for cancellation.</summary>
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult(true);

            if (_data is not null)
                return await _data.ReadAsync(buffer, cancellationToken);

            await Task.Delay(Timeout.Infinite, cancellationToken);

            return 0;
        }

        /// <summary>Supports synchronous reads for data-backed fixtures.</summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_data is null)
                throw new NotSupportedException();

            return _data.Read(buffer, offset, count);
        }

        /// <summary>Provides a no-op flush for the read-only fixture.</summary>
        public override void Flush() { }

        /// <summary>Rejects seeking.</summary>
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        /// <summary>Rejects length changes.</summary>
        public override void SetLength(long value) =>
            throw new NotSupportedException();

        /// <summary>Rejects writing.</summary>
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        /// <summary>Records disposal and releases the backing stream.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _data?.Dispose();

            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}