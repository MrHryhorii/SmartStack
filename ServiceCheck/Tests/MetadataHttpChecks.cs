using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Http;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>
/// Verifies bounded metadata HTTP exchanges with controlled responses.
/// </summary>
internal static class MetadataHttpChecks
{
    private static readonly byte[] Payload =
        Encoding.UTF8.GetBytes("""{"fixture":true}""");

    /// <summary>Returns metadata transport regression checks.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks()
    {
        return
        [
            ("Metadata GET routes and request headers verified", VerifyRoutesAsync),
            ("Metadata HTTP failures returned without retries", VerifyHttpFailuresAsync),
            ("Metadata redirect evidence sanitized", VerifyRedirectAsync),
            ("Metadata response byte limits enforced", VerifyLimitsAsync),
            ("Metadata Content-Length mismatch rejected", VerifyLengthMismatchAsync),
            ("Metadata bearer headers and error evidence sanitized", VerifyErrorEvidenceAsync),
            ("Metadata body deadline preserves partial evidence",
                () => VerifyBodyDeadlineAsync(runDeadline: false)),
            ("Run deadline preserves partial metadata evidence",
                () => VerifyBodyDeadlineAsync(runDeadline: true)),
            ("Caller and shared run cancellation stop metadata reads", VerifyCancellationAsync),
            ("Metadata transport failures omit private exception text", VerifyTransportFailuresAsync)
        ];
    }

    /// <summary>Verifies the configured GET routes, headers, and response evidence.</summary>
    private static async Task VerifyRoutesAsync()
    {
        (MetadataEndpoint Endpoint, string Route)[] endpoints =
        [
            (MetadataEndpoint.Health, "health"),
            (MetadataEndpoint.HealthFallback, "v1/health"),
            (MetadataEndpoint.Status, "tsbk/server/status"),
            (MetadataEndpoint.Voices, "tsbk/audio/voices")
        ];

        foreach (var (endpoint, route) in endpoints)
        {
            var expectedUri = "https://tts.example.test/proxy/" + route;

            using var handler = new FixtureHandler((request, _) =>
            {
                Require(
                    request.Method == HttpMethod.Get &&
                    request.Content is null,
                    "Metadata must use GET without a request body.");

                Require(
                    request.RequestUri?.AbsoluteUri == expectedUri,
                    "The metadata route or proxy prefix changed.");

                Require(
                    request.Headers.Accept.Any(value =>
                        value.MediaType == "application/json") &&
                    request.Headers.Authorization is null,
                    "The metadata request headers are incorrect.");

                return Task.FromResult(CreateResponse(HttpStatusCode.OK));
            });

            using var client = new TsubakiClient(CreateProfile(), handler);

            var result = await client.RequestMetadataAsync(endpoint)
                .WaitAsync(TimeSpan.FromSeconds(15));

            Require(
                handler.RequestCount == 1 &&
                result.Endpoint == endpoint &&
                result.Route == route &&
                result.RequestUri.AbsoluteUri == expectedUri &&
                result.State == MetadataRequestState.Completed,
                "The configured metadata exchange did not complete once.");

            Require(
                result.StatusCode == 200 &&
                result.BodyComplete &&
                result.BodyBytesRead == Payload.Length &&
                result.Payload?.SequenceEqual(Payload) == true &&
                result.HeadersDurationMs is not null &&
                result.BodyDurationMs is not null,
                "Complete metadata response evidence is missing.");

            Require(
                !JsonSerializer.Serialize(result).Contains(
                    "\"Payload\"",
                    StringComparison.Ordinal),
                "Raw metadata bytes were included in JSON.");
        }
    }

    /// <summary>Preserves unsuccessful statuses without retry or alias fallback.</summary>
    private static async Task VerifyHttpFailuresAsync()
    {
        (HttpStatusCode Status, string Code)[] cases =
        [
            (HttpStatusCode.Accepted, "http_unexpected_status"),
            (HttpStatusCode.NoContent, "http_unexpected_status"),
            (HttpStatusCode.PartialContent, "http_unexpected_status"),
            (HttpStatusCode.Unauthorized, "http_unauthorized"),
            (HttpStatusCode.Forbidden, "http_forbidden"),
            (HttpStatusCode.NotFound, "http_not_found"),
            (HttpStatusCode.MethodNotAllowed, "http_method_not_allowed"),
            (HttpStatusCode.TooManyRequests, "http_rate_limited"),
            (HttpStatusCode.TemporaryRedirect, "http_redirect"),
            (HttpStatusCode.ServiceUnavailable, "http_server_error")
        ];

        foreach (var (status, code) in cases)
        {
            var result = await SendAsync(CreateResponse(status));

            Require(
                result.State == MetadataRequestState.Failed &&
                result.StatusCode == (int)status &&
                result.Code == code &&
                result.Payload is null,
                "An unsuccessful metadata status was accepted or misclassified.");
        }
    }

    /// <summary>Removes credentials, query parameters, and fragments from redirects.</summary>
    private static async Task VerifyRedirectAsync()
    {
        var response = CreateResponse(HttpStatusCode.TemporaryRedirect);

        response.Headers.Location = new Uri(
            "https://private:secret@redirect.example.test/login?token=hidden#fragment");

        var result = await SendAsync(response);

        Require(
            result.State == MetadataRequestState.Failed &&
            result.Code == "http_redirect" &&
            result.RedirectLocation == "https://redirect.example.test/login",
            "Metadata redirect evidence retained sensitive URI components.");
    }

    /// <summary>Accepts the exact cap and bounds declared and unknown-length bodies.</summary>
    private static async Task VerifyLimitsAsync()
    {
        var profile = CreateProfile() with
        {
            Limits = new ProfileLimits
            {
                MaxMetadataBytes = Payload.Length
            }
        };

        var exact = await SendAsync(
            CreateResponse(HttpStatusCode.OK),
            profile);

        Require(
            exact.State == MetadataRequestState.Completed &&
            exact.BodyComplete &&
            exact.BodyBytesRead == Payload.Length &&
            exact.Payload?.SequenceEqual(Payload) == true,
            "A metadata body at the exact size limit was rejected.");

        var declared = await SendAsync(
            CreateResponse(
                HttpStatusCode.OK,
                new byte[Payload.Length + 1]),
            profile);

        Require(
            declared.State == MetadataRequestState.Failed &&
            declared.Code == "response_limit_exceeded" &&
            declared.BodyBytesRead == 0 &&
            !declared.BodyComplete &&
            declared.Payload is null,
            "An oversized declared metadata body was not rejected before reading.");

        using var stream = new FixtureStream(
            new byte[Payload.Length + 64]);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        };

        var streamed = await SendAsync(response, profile);

        Require(
            streamed.State == MetadataRequestState.Failed &&
            streamed.Code == "response_limit_exceeded" &&
            streamed.DeclaredContentLength is null &&
            streamed.BodyBytesRead == Payload.Length + 1 &&
            !streamed.BodyComplete &&
            streamed.Payload is null &&
            stream.Disposed,
            "Unknown-length metadata exceeded its bounded read budget.");
    }

    /// <summary>Rejects complete bytes that disagree with Content-Length.</summary>
    private static async Task VerifyLengthMismatchAsync()
    {
        var response = CreateResponse(HttpStatusCode.OK);
        response.Content.Headers.ContentLength = Payload.Length + 1;

        var result = await SendAsync(response);

        Require(
            result.State == MetadataRequestState.Failed &&
            result.Code == "response_length_mismatch" &&
            result.BodyBytesRead == Payload.Length &&
            !result.BodyComplete &&
            result.Payload is null,
            "Inconsistent metadata Content-Length was accepted.");
    }

    /// <summary>Verifies bearer authentication, redaction, and bounded error bodies.</summary>
    private static async Task VerifyErrorEvidenceAsync()
    {
        const string token = "fixture-meta-token_123+/=";

        var variableName = $"TSUBAKI_CHECK_METADATA_{Guid.NewGuid():N}";
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

            using var handler = new FixtureHandler((request, _) =>
            {
                Require(
                    request.Headers.Authorization?.Scheme == "Bearer" &&
                    request.Headers.Authorization?.Parameter == token,
                    "Metadata bearer authentication was not applied.");

                var body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    error = $"Token: {token}; escaped: {Uri.EscapeDataString(token)}; input: {profile.Synthesis.Input}"
                });

                return Task.FromResult(CreateResponse(
                    HttpStatusCode.Unauthorized,
                    body));
            });

            using var client = new TsubakiClient(profile, handler);

            var result = await client.RequestMetadataAsync(
                MetadataEndpoint.Health);

            var excerpt = result.ErrorExcerpt
                ?? throw new InvalidOperationException(
                    "The textual metadata error excerpt is missing.");

            Require(
                handler.RequestCount == 1 &&
                result.State == MetadataRequestState.Failed &&
                result.Code == "http_unauthorized" &&
                excerpt.Contains("***", StringComparison.Ordinal) &&
                !excerpt.Contains(token, StringComparison.Ordinal) &&
                !excerpt.Contains(
                    Uri.EscapeDataString(token),
                    StringComparison.Ordinal) &&
                !excerpt.Contains(
                    JsonEncodedText.Encode(token).ToString(),
                    StringComparison.Ordinal) &&
                !excerpt.Contains(
                    profile.Synthesis.Input,
                    StringComparison.Ordinal),
                "Sensitive text remained in metadata error evidence.");

            var limitedProfile = profile with
            {
                Limits = profile.Limits with
                {
                    MaxMetadataBytes = 64
                }
            };

            var large = await SendAsync(
                CreateResponse(
                    HttpStatusCode.ServiceUnavailable,
                    Encoding.UTF8.GetBytes(new string('x', 10000)),
                    "text/plain"),
                limitedProfile);

            Require(
                large.State == MetadataRequestState.Failed &&
                large.Code == "http_server_error" &&
                large.StatusCode == 503 &&
                large.BodyBytesRead == 65 &&
                large.ErrorExcerptTruncated &&
                large.ErrorExcerpt is { Length: <= 4096 } &&
                large.Payload is null,
                "Metadata error evidence exceeded its configured size budget.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
        }
    }

    /// <summary>Distinguishes metadata and overall deadlines during body reading.</summary>
    private static async Task VerifyBodyDeadlineAsync(bool runDeadline)
    {
        var profile = CreateProfile() with
        {
            TimeoutsMs = new ProfileTimeouts
            {
                Metadata = runDeadline ? 5000 : 500,
                Run = runDeadline ? 500 : 5000
            }
        };

        var prefix = Encoding.UTF8.GetBytes("{\"fixture\":");

        using var stream = new FixtureStream(
            prefix,
            stallAfterData: true);

        var result = await SendAsync(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            },
            profile);

        RequirePartial(
            result,
            stream,
            prefix.Length,
            MetadataRequestState.TimedOut,
            runDeadline ? "deadline_exceeded" : "metadata_timeout");
    }

    /// <summary>Verifies caller and shared run cancellation after partial body data.</summary>
    private static async Task VerifyCancellationAsync()
    {
        bool[] callerModes = [true, false];

        foreach (var callerCancellation in callerModes)
        {
            var prefix = Encoding.UTF8.GetBytes("{\"fixture\":");

            using var stream = new FixtureStream(
                prefix,
                stallAfterData: true);

            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromSeconds(15));

            using var handler = new FixtureHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream)
                }));

            using var client = new TsubakiClient(CreateProfile(), handler);

            var pending = client.RequestMetadataAsync(
                MetadataEndpoint.Health,
                callerCancellation ? cancellation.Token : default,
                callerCancellation ? default : cancellation.Token);

            try
            {
                await stream.ReadStalled.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));

                cancellation.Cancel();

                var result = await pending.WaitAsync(
                    TimeSpan.FromSeconds(5));

                RequirePartial(
                    result,
                    stream,
                    prefix.Length,
                    callerCancellation
                        ? MetadataRequestState.Cancelled
                        : MetadataRequestState.TimedOut,
                    callerCancellation ? "cancelled" : "deadline_exceeded");

                Require(
                    handler.RequestCount == 1,
                    "An interrupted metadata request was repeated.");
            }
            finally
            {
                cancellation.Cancel();
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        using var unusedHandler = new FixtureHandler((_, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK)));

        using var unusedClient = new TsubakiClient(
            CreateProfile(),
            unusedHandler);

        var skipped = await unusedClient.RequestMetadataAsync(
            MetadataEndpoint.Health,
            cancelled.Token);

        Require(
            skipped.State == MetadataRequestState.Cancelled &&
            skipped.Code == "cancelled" &&
            skipped.StatusCode is null &&
            unusedHandler.RequestCount == 0,
            "A pre-cancelled metadata request reached the HTTP handler.");
    }

    /// <summary>Classifies transport failures without retaining exception messages.</summary>
    private static async Task VerifyTransportFailuresAsync()
    {
        const string privateDetail = "Private metadata transport detail.";

        (Exception Failure, MetadataRequestState State, string Code)[] cases =
        [
            (
                new HttpRequestException(
                    HttpRequestError.NameResolutionError,
                    privateDetail,
                    null,
                    null),
                MetadataRequestState.Failed,
                "dns_resolution_failed"
            ),
            (
                new HttpRequestException(
                    HttpRequestError.SecureConnectionError,
                    privateDetail,
                    null,
                    null),
                MetadataRequestState.Failed,
                "tls_handshake_failed"
            ),
            (
                new OperationCanceledException(privateDetail),
                MetadataRequestState.Error,
                "unexpected_cancellation"
            ),
            (
                new InvalidOperationException(privateDetail),
                MetadataRequestState.Error,
                "client_error"
            )
        ];

        foreach (var (failure, state, code) in cases)
        {
            using var handler = new FixtureHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(failure));

            using var client = new TsubakiClient(CreateProfile(), handler);

            var result = await client.RequestMetadataAsync(
                MetadataEndpoint.Health);

            Require(
                handler.RequestCount == 1 &&
                result.State == state &&
                result.Code == code &&
                result.StatusCode is null &&
                result.ExceptionType is { Length: > 0 } &&
                !JsonSerializer.Serialize(result).Contains(
                    privateDetail,
                    StringComparison.Ordinal),
                "A metadata transport failure was misclassified or exposed private text.");
        }
    }

    /// <summary>Requires preserved partial evidence and a released response stream.</summary>
    private static void RequirePartial(
        MetadataRequestResult result,
        FixtureStream stream,
        int bytesRead,
        MetadataRequestState state,
        string code)
    {
        Require(
            result.State == state &&
            result.Code == code &&
            result.StatusCode == 200 &&
            result.BodyBytesRead == bytesRead &&
            !result.BodyComplete &&
            result.Payload is null &&
            result.HeadersDurationMs is not null &&
            result.BodyDurationMs is not null &&
            stream.Disposed,
            "Interrupted metadata lost partial evidence or retained its stream.");
    }

    /// <summary>Receives one prepared response and enforces the no-retry contract.</summary>
    private static async Task<MetadataRequestResult> SendAsync(
        HttpResponseMessage response,
        TargetProfile? profile = null)
    {
        using var handler = new FixtureHandler((_, _) =>
            Task.FromResult(response));

        using var client = new TsubakiClient(
            profile ?? CreateProfile(),
            handler);

        var result = await client.RequestMetadataAsync(
            MetadataEndpoint.Health)
            .WaitAsync(TimeSpan.FromSeconds(15));

        Require(
            handler.RequestCount == 1,
            "The metadata operation retried or requested another endpoint.");

        return result;
    }

    /// <summary>Creates a profile for controlled metadata exchanges.</summary>
    private static TargetProfile CreateProfile()
    {
        return new TargetProfile
        {
            SchemaVersion = 1,
            Name = "Metadata HTTP fixture",
            Target = new ProfileTarget
            {
                Id = "metadata-fixture",
                BaseUrl = "https://tts.example.test/proxy/"
            },
            TimeoutsMs = new ProfileTimeouts
            {
                Metadata = 5000,
                Run = 10000
            }
        };
    }

    /// <summary>Creates a response with controlled bytes and media type.</summary>
    private static HttpResponseMessage CreateResponse(
        HttpStatusCode status,
        byte[]? body = null,
        string? mediaType = "application/json")
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(body ?? Payload)
        };

        if (mediaType is not null)
        {
            response.Content.Headers.ContentType =
                new MediaTypeHeaderValue(mediaType);
        }

        return response;
    }

    /// <summary>Fails the current regression check when its condition is false.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>Counts outgoing requests and returns controlled responses.</summary>
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> _send;

        public int RequestCount { get; private set; }

        /// <summary>Stores the response factory used by the handler.</summary>
        public FixtureHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        /// <summary>Counts and dispatches one outgoing request.</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;

            return _send(request, cancellationToken);
        }
    }

    /// <summary>Provides unknown-length bytes and an optional cancellable stall.</summary>
    private sealed class FixtureStream : Stream
    {
        private readonly MemoryStream _data;
        private readonly bool _stallAfterData;

        public bool Disposed { get; private set; }

        public TaskCompletionSource ReadStalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => !Disposed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>Stores the available bytes and whether reading stalls afterward.</summary>
        public FixtureStream(byte[] bytes, bool stallAfterData = false)
        {
            _data = new MemoryStream(bytes, writable: false);
            _stallAfterData = stallAfterData;
        }

        /// <summary>Returns available bytes, then ends or waits for cancellation.</summary>
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = await _data.ReadAsync(buffer, cancellationToken);

            if (count != 0 || !_stallAfterData)
            {
                return count;
            }

            ReadStalled.TrySetResult();

            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);

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
            {
                _data.Dispose();
            }

            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
