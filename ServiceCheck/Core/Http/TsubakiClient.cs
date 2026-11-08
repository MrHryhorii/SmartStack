using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Core.Http;

/// <summary>Executes bounded speech requests against one configured server.</summary>
public sealed class TsubakiClient : IDisposable
{
    private const int MaxErrorBytes = 4096;
    private const int MaxTokenCharacters = 16_384;

    private readonly TargetProfile _profile;
    private readonly HttpClient _http;
    private readonly string? _token;

    private bool _disposed;

    /// <summary>Validates configuration and prepares an owned HTTP client.</summary>
    public TsubakiClient(
        TargetProfile profile,
        HttpMessageHandler? handler = null)
    {
        _profile = ProfileValidator.ValidateAndNormalize(profile);
        _token = ResolveToken(_profile.Authentication);

        _http = new HttpClient(
            handler ?? new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                MaxResponseDrainSize = 0,
                MaxResponseHeadersLength = 16
            },
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>Removes the configured bearer token from diagnostic text.</summary>
    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_token is null)
            return text;

        return text
            .Replace(_token, "***", StringComparison.Ordinal)
            .Replace(
                Uri.EscapeDataString(_token),
                "***",
                StringComparison.Ordinal)
            .Replace(
                JsonEncodedText.Encode(_token).ToString(),
                "***",
                StringComparison.Ordinal);
    }

    /// <summary>Sends one speech request under caller and configured deadlines.</summary>
    public async Task<SpeechRequestResult> RequestSpeechAsync(
        CancellationToken cancellationToken = default,
        CancellationToken runCancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var uri = ProfileUrlRules.Resolve(
            _profile.Target.BaseUrl,
            _profile.Routes.Speech);

        var exchange = new Exchange(uri);

        using var runDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(runCancellationToken);

        runDeadline.CancelAfter(_profile.TimeoutsMs.Run);

        using var speechDeadline =
            new CancellationTokenSource(_profile.TimeoutsMs.Speech);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            runDeadline.Token,
            speechDeadline.Token);

        try
        {
            return await ExecuteSpeechAsync(exchange, linked.Token);
        }
        catch (OperationCanceledException exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Finish(
                    exchange,
                    SpeechRequestState.Cancelled,
                    "cancelled",
                    "Speech request was cancelled.",
                    exception);
            }

            if (linked.IsCancellationRequested)
            {
                return Finish(
                    exchange,
                    SpeechRequestState.TimedOut,
                    "deadline_exceeded",
                    "Speech request exceeded its configured deadline.",
                    exception);
            }

            return Finish(
                exchange,
                SpeechRequestState.Error,
                "unexpected_cancellation",
                "The HTTP operation was cancelled for an unknown reason.",
                exception);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException)
        {
            var (code, summary) = ClassifyTransportFailure(exception);

            return Finish(
                exchange,
                SpeechRequestState.Failed,
                code,
                summary,
                exception);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            return Finish(
                exchange,
                SpeechRequestState.Error,
                "client_error",
                "The client could not complete the HTTP operation.",
                exception);
        }
    }

    /// <summary>Sends the request and captures its bounded response.</summary>
    private async Task<SpeechRequestResult> ExecuteSpeechAsync(
        Exchange exchange,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var request = CreateSpeechRequest(exchange.Result.RequestUri);
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        CaptureHeaders(response, exchange);

        var isExpectedStatus = response.StatusCode == HttpStatusCode.OK;

        if (isExpectedStatus
            && response.Content.Headers.ContentLength is long declaredLength
            && declaredLength > _profile.Limits.MaxAudioBytes)
        {
            exchange.BodyBytesRead = 0;

            return Finish(
                exchange,
                SpeechRequestState.Failed,
                "response_limit_exceeded",
                "Declared speech response size exceeds limits.maxAudioBytes.");
        }

        var complete = await ReadBodyAsync(
            response,
            exchange,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (!isExpectedStatus)
        {
            return Finish(
                exchange,
                SpeechRequestState.Failed,
                GetHttpFailureCode((int)response.StatusCode),
                $"Speech request returned HTTP {(int)response.StatusCode}.");
        }

        if (!complete)
        {
            return Finish(
                exchange,
                SpeechRequestState.Failed,
                "response_limit_exceeded",
                "Speech response exceeded limits.maxAudioBytes.");
        }

        if (exchange.Result.DeclaredContentLength is long expectedLength
            && exchange.BodyBytesRead != expectedLength)
        {
            exchange.Result = exchange.Result with
            {
                BodyComplete = false,
                Payload = null
            };

            return Finish(
                exchange,
                SpeechRequestState.Failed,
                "response_length_mismatch",
                "Received body length does not match Content-Length.");
        }

        return Finish(
            exchange,
            SpeechRequestState.Completed,
            "speech_response_received",
            "Speech response received.");
    }

    /// <summary>Maps validated synthesis settings to the API request body.</summary>
    private HttpRequestMessage CreateSpeechRequest(Uri uri)
    {
        var settings = _profile.Synthesis;

        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new
            {
                model = settings.Model,
                input = settings.Input,
                voice = settings.Voice,
                response_format = settings.ResponseFormat,
                speed = settings.Speed,
                stream = settings.Stream
            })
        };

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("audio/wav"));

        if (_token is not null)
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _token);

        return request;
    }

    /// <summary>Captures selected response headers and header timing.</summary>
    private void CaptureHeaders(
        HttpResponseMessage response,
        Exchange exchange)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;

        exchange.Result = exchange.Result with
        {
            StatusCode = (int)response.StatusCode,
            MediaType = mediaType is null ? null : Redact(mediaType),
            DeclaredContentLength = response.Content.Headers.ContentLength,
            HeadersDurationMs = exchange.Clock.ElapsedMilliseconds,
            ReportedSampleRateHz = ReadReportedSampleRate(response),
            RedirectLocation = ReadRedirectLocation(
                response,
                exchange.Result.RequestUri)
        };
    }

    /// <summary>Reads a bounded body while retaining partial byte counts.</summary>
    private async Task<bool> ReadBodyAsync(
        HttpResponseMessage response,
        Exchange exchange,
        CancellationToken cancellationToken)
    {
        exchange.BodyClock = Stopwatch.StartNew();
        exchange.BodyBytesRead = 0;

        try
        {
            var isExpectedStatus = response.StatusCode == HttpStatusCode.OK;
            var limit = isExpectedStatus
                ? _profile.Limits.MaxAudioBytes
                : MaxErrorBytes;

            var capacity = (int)Math.Min(
                response.Content.Headers.ContentLength ?? 4096,
                limit + 1L);

            await using var source =
                await response.Content.ReadAsStreamAsync(cancellationToken);

            using var output = new MemoryStream(capacity);

            var buffer = new byte[Math.Min(16_384, limit + 1)];
            var complete = false;

            while (output.Length <= limit)
            {
                var remaining = limit + 1 - (int)output.Length;

                var count = await source.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    cancellationToken);

                if (count == 0)
                {
                    complete = true;
                    break;
                }

                exchange.BodyBytesRead += count;
                output.Write(buffer, 0, count);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var bytes = output.ToArray();
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var textual = IsTextual(mediaType);
            var excerptTruncated = bytes.Length > MaxErrorBytes || !complete;

            exchange.Result = exchange.Result with
            {
                BodyComplete = complete,
                Payload = isExpectedStatus && complete ? bytes : null,
                ErrorExcerpt = textual
                    ? CreateExcerpt(bytes, excerptTruncated)
                    : null,
                ErrorExcerptTruncated = textual && excerptTruncated
            };

            return complete;
        }
        finally
        {
            exchange.BodyClock?.Stop();
        }
    }

    /// <summary>Builds a bounded plain-text excerpt with sensitive text removed.</summary>
    private string CreateExcerpt(byte[] bytes, bool truncated)
    {
        if (truncated)
            return "Response text exceeded the diagnostic excerpt limit.";

        var text = Redact(Encoding.UTF8.GetString(bytes));
        var input = _profile.Synthesis.Input;

        text = text
            .Replace(input, "***", StringComparison.Ordinal)
            .Replace(
                JsonEncodedText.Encode(input).ToString(),
                "***",
                StringComparison.Ordinal);

        return new string(text
            .Select(character =>
                char.IsControl(character)
                && character is not ('\r' or '\n' or '\t')
                    ? ' '
                    : character)
            .Take(MaxErrorBytes)
            .ToArray());
    }

    /// <summary>Recognizes media types suitable for a diagnostic text excerpt.</summary>
    private static bool IsTextual(string? mediaType)
    {
        if (mediaType is null)
            return false;

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals(
                "application/json",
                StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Records an HTTP redirect without credentials, query, or fragment.</summary>
    private string? ReadRedirectLocation(
        HttpResponseMessage response,
        Uri requestUri)
    {
        if ((int)response.StatusCode is < 300 or >= 400
            || !response.Headers.TryGetValues("Location", out var values)
            || !Uri.TryCreate(requestUri, values.FirstOrDefault(), out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var safe = new UriBuilder(uri)
        {
            UserName = "",
            Password = "",
            Query = "",
            Fragment = ""
        };

        return Redact(safe.Uri.AbsoluteUri);
    }

    /// <summary>Reads the optional server-reported audio sample rate.</summary>
    private static int? ReadReportedSampleRate(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(
                "X-Audio-Sample-Rate",
                out var values))
        {
            return null;
        }

        return int.TryParse(
                values.FirstOrDefault(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var rate)
            && rate > 0
                ? rate
                : null;
    }

    /// <summary>Resolves only the explicitly configured credential variable.</summary>
    private static string? ResolveToken(ProfileAuthentication authentication)
    {
        if (authentication.Type == "none")
            return null;

        var name = authentication.TokenEnvironmentVariable;
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var token = Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException(
                $"Environment variable '{name}' is missing or empty.");

        if (token.Length > MaxTokenCharacters
            || token.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or '.' or '_' or '~' or '+' or '/' or '=')))
        {
            throw new ArgumentException(
                "The configured bearer token has an unsupported length or characters.");
        }

        return token;
    }

    /// <summary>Maps observable transport failures to stable diagnostic codes.</summary>
    private static (string Code, string Summary) ClassifyTransportFailure(
        Exception exception)
    {
        var socketError = FindSocketError(exception);

        if (socketError == SocketError.ConnectionRefused)
            return ("connection_refused", "The endpoint refused the connection.");

        if (socketError == SocketError.TimedOut)
            return ("connection_timeout", "The connection attempt timed out.");

        var error = exception switch
        {
            HttpRequestException request => request.HttpRequestError,
            HttpIOException content => content.HttpRequestError,
            _ => HttpRequestError.Unknown
        };

        return error switch
        {
            HttpRequestError.NameResolutionError =>
                ("dns_resolution_failed", "The server name could not be resolved."),

            HttpRequestError.SecureConnectionError =>
                ("tls_handshake_failed", "The TLS handshake failed."),

            HttpRequestError.ConnectionError =>
                ("connection_failed", "The HTTP connection failed."),

            HttpRequestError.ResponseEnded =>
                ("response_truncated", "The HTTP response ended prematurely."),

            HttpRequestError.ConfigurationLimitExceeded =>
                ("response_limit_exceeded", "An HTTP response limit was exceeded."),

            HttpRequestError.InvalidResponse or HttpRequestError.HttpProtocolError =>
                ("http_protocol_error", "The HTTP response or protocol was invalid."),

            _ => ("request_failed", "The HTTP operation failed; the exact cause is unknown.")
        };
    }

    /// <summary>Finds a platform socket error in the exception chain.</summary>
    private static SocketError? FindSocketError(Exception exception)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is SocketException socket)
                return socket.SocketErrorCode;
        }

        return null;
    }

    /// <summary>Maps unsuccessful HTTP statuses to stable diagnostic codes.</summary>
    private static string GetHttpFailureCode(int statusCode) => statusCode switch
    {
        401 => "http_unauthorized",
        403 => "http_forbidden",
        404 => "http_not_found",
        405 => "http_method_not_allowed",
        429 => "http_rate_limited",
        >= 300 and < 400 => "http_redirect",
        >= 500 => "http_server_error",
        _ => "http_unexpected_status"
    };

    /// <summary>Finalizes timestamps, timings, and the request outcome.</summary>
    private SpeechRequestResult Finish(
        Exchange exchange,
        SpeechRequestState state,
        string code,
        string summary,
        Exception? exception = null)
    {
        exchange.Clock.Stop();

        return exchange.Result with
        {
            State = state,
            Code = code,
            Summary = summary,
            FinishedAtUtc = DateTimeOffset.UtcNow,
            TotalDurationMs = exchange.Clock.ElapsedMilliseconds,
            BodyDurationMs = exchange.BodyClock?.ElapsedMilliseconds,
            BodyBytesRead = exchange.BodyBytesRead,
            ExceptionType = exception is null
                ? null
                : Redact(exception.GetType().Name)
        };
    }

    /// <summary>Releases the owned HTTP handler and its connections.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _http.Dispose();
    }

    /// <summary>Retains observations while an HTTP exchange is in progress.</summary>
    private sealed class Exchange
    {
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public Stopwatch? BodyClock { get; set; }
        public long? BodyBytesRead { get; set; }
        public SpeechRequestResult Result { get; set; }

        /// <summary>Starts an exchange with an empty evidence snapshot.</summary>
        public Exchange(Uri uri)
        {
            var started = DateTimeOffset.UtcNow;

            Result = new SpeechRequestResult
            {
                State = SpeechRequestState.Error,
                Code = "request_incomplete",
                Summary = "The request has not completed.",
                RequestUri = uri,
                StartedAtUtc = started,
                FinishedAtUtc = started,
                TotalDurationMs = 0
            };
        }
    }
}