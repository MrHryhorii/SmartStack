using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Core.Http;

public sealed partial class TsubakiClient
{
    /// <summary>Requests one configured metadata endpoint under bounded deadlines.</summary>
    public async Task<MetadataRequestResult> RequestMetadataAsync(
        MetadataEndpoint endpoint,
        CancellationToken cancellationToken = default,
        CancellationToken runCancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var route = endpoint switch
        {
            MetadataEndpoint.Health => _profile.Routes.Health,
            MetadataEndpoint.HealthFallback => _profile.Routes.HealthFallback,
            MetadataEndpoint.Status => _profile.Routes.Status,
            MetadataEndpoint.Voices => _profile.Routes.Voices,
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
        };

        var uri = ProfileUrlRules.Resolve(
            _profile.Target.BaseUrl,
            route);

        var exchange = new MetadataExchange(endpoint, route, uri);

        using var runDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(runCancellationToken);

        runDeadline.CancelAfter(_profile.TimeoutsMs.Run);

        using var metadataDeadline =
            new CancellationTokenSource(_profile.TimeoutsMs.Metadata);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            runDeadline.Token,
            metadataDeadline.Token);

        try
        {
            return await ExecuteMetadataAsync(exchange, linked.Token);
        }
        catch (OperationCanceledException exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return FinishMetadata(
                    exchange,
                    MetadataRequestState.Cancelled,
                    "cancelled",
                    "Metadata request was cancelled.",
                    exception);
            }

            if (runDeadline.IsCancellationRequested)
            {
                return FinishMetadata(
                    exchange,
                    MetadataRequestState.TimedOut,
                    "deadline_exceeded",
                    "Metadata request exceeded the overall run deadline.",
                    exception);
            }

            if (metadataDeadline.IsCancellationRequested)
            {
                return FinishMetadata(
                    exchange,
                    MetadataRequestState.TimedOut,
                    "metadata_timeout",
                    "Metadata request exceeded its configured deadline.",
                    exception);
            }

            return FinishMetadata(
                exchange,
                MetadataRequestState.Error,
                "unexpected_cancellation",
                "The HTTP operation was cancelled for an unknown reason.",
                exception);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException)
        {
            var (code, summary) = ClassifyTransportFailure(exception);

            return FinishMetadata(
                exchange,
                MetadataRequestState.Failed,
                code,
                summary,
                exception);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            return FinishMetadata(
                exchange,
                MetadataRequestState.Error,
                "client_error",
                "The client could not complete the metadata operation.",
                exception);
        }
    }

    /// <summary>Sends one GET and captures its bounded response.</summary>
    private async Task<MetadataRequestResult> ExecuteMetadataAsync(
        MetadataExchange exchange,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var request = CreateMetadataRequest(exchange.Result.RequestUri);

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var mediaType = response.Content.Headers.ContentType?.MediaType;

        exchange.Result = exchange.Result with
        {
            StatusCode = (int)response.StatusCode,
            MediaType = mediaType is null ? null : Redact(mediaType),
            DeclaredContentLength = response.Content.Headers.ContentLength,
            HeadersDurationMs = exchange.Clock.ElapsedMilliseconds,
            RedirectLocation = ReadRedirectLocation(
                response,
                exchange.Result.RequestUri)
        };

        var isExpectedStatus = response.StatusCode == HttpStatusCode.OK;

        if (isExpectedStatus &&
            response.Content.Headers.ContentLength is long declaredLength &&
            declaredLength > _profile.Limits.MaxMetadataBytes)
        {
            exchange.BodyBytesRead = 0;

            return FinishMetadata(
                exchange,
                MetadataRequestState.Failed,
                "response_limit_exceeded",
                "Declared metadata response size exceeds limits.maxMetadataBytes.");
        }

        var complete = await ReadMetadataBodyAsync(
            response,
            exchange,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (!isExpectedStatus)
        {
            return FinishMetadata(
                exchange,
                MetadataRequestState.Failed,
                GetHttpFailureCode((int)response.StatusCode),
                $"Metadata request returned HTTP {(int)response.StatusCode}.");
        }

        if (!complete)
        {
            return FinishMetadata(
                exchange,
                MetadataRequestState.Failed,
                "response_limit_exceeded",
                "Metadata response exceeded limits.maxMetadataBytes.");
        }

        if (exchange.Result.DeclaredContentLength is long expectedLength &&
            exchange.BodyBytesRead != expectedLength)
        {
            exchange.Result = exchange.Result with
            {
                BodyComplete = false,
                Payload = null
            };

            return FinishMetadata(
                exchange,
                MetadataRequestState.Failed,
                "response_length_mismatch",
                "Received body length does not match Content-Length.");
        }

        return FinishMetadata(
            exchange,
            MetadataRequestState.Completed,
            "metadata_response_received",
            "Metadata response received.");
    }

    /// <summary>Creates an authenticated JSON metadata GET without a request body.</summary>
    private HttpRequestMessage CreateMetadataRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        if (_token is not null)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _token);
        }

        return request;
    }

    /// <summary>Reads metadata while preserving partial byte counts and timings.</summary>
    private async Task<bool> ReadMetadataBodyAsync(
        HttpResponseMessage response,
        MetadataExchange exchange,
        CancellationToken cancellationToken)
    {
        exchange.BodyClock = Stopwatch.StartNew();
        exchange.BodyBytesRead = 0;

        try
        {
            var isExpectedStatus = response.StatusCode == HttpStatusCode.OK;

            var limit = isExpectedStatus
                ? _profile.Limits.MaxMetadataBytes
                : Math.Min(MaxErrorBytes, _profile.Limits.MaxMetadataBytes);

            var (bytes, complete) = await ReadBoundedBytesAsync(
                response,
                limit,
                count => exchange.BodyBytesRead = count,
                cancellationToken);

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var textualError = !isExpectedStatus && IsTextual(mediaType);
            var excerptTruncated = bytes.Length > MaxErrorBytes || !complete;

            exchange.Result = exchange.Result with
            {
                BodyComplete = complete,
                Payload = isExpectedStatus && complete ? bytes : null,
                ErrorExcerpt = textualError
                    ? CreateExcerpt(bytes, excerptTruncated)
                    : null,
                ErrorExcerptTruncated = textualError && excerptTruncated
            };

            return complete;
        }
        finally
        {
            exchange.BodyClock?.Stop();
        }
    }

    /// <summary>Streams at most the byte limit plus one overflow-detection byte.</summary>
    private static async Task<(byte[] Bytes, bool Complete)> ReadBoundedBytesAsync(
        HttpResponseMessage response,
        int limit,
        Action<long> recordBytesRead,
        CancellationToken cancellationToken)
    {
        recordBytesRead(0);

        var capacity = (int)Math.Clamp(
            response.Content.Headers.ContentLength ?? 4096,
            0L,
            limit + 1L);

        await using var source =
            await response.Content.ReadAsStreamAsync(cancellationToken);

        using var output = new MemoryStream(capacity);

        var buffer = new byte[Math.Min(16_384, limit + 1)];
        var totalBytes = 0L;
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

            totalBytes += count;
            recordBytesRead(totalBytes);
            output.Write(buffer, 0, count);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return (output.ToArray(), complete);
    }

    /// <summary>Finalizes metadata evidence without exporting exception messages.</summary>
    private MetadataRequestResult FinishMetadata(
        MetadataExchange exchange,
        MetadataRequestState state,
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

    /// <summary>Preserves observations while a metadata exchange is in progress.</summary>
    private sealed class MetadataExchange
    {
        public Stopwatch Clock { get; } = Stopwatch.StartNew();

        public Stopwatch? BodyClock { get; set; }

        public long? BodyBytesRead { get; set; }

        public MetadataRequestResult Result { get; set; }

        /// <summary>Starts a metadata exchange with an empty evidence snapshot.</summary>
        public MetadataExchange(
            MetadataEndpoint endpoint,
            string route,
            Uri uri)
        {
            var started = DateTimeOffset.UtcNow;

            Result = new MetadataRequestResult
            {
                Endpoint = endpoint,
                State = MetadataRequestState.Error,
                Code = "request_incomplete",
                Summary = "The request has not completed.",
                Route = route,
                RequestUri = uri,
                StartedAtUtc = started,
                FinishedAtUtc = started,
                TotalDurationMs = 0
            };
        }
    }
}