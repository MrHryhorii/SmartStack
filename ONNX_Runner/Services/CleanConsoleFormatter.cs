using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace ONNX_Runner.Services;

/// <summary>
/// Per-request timing state shared by the synthesis, encoder, transport, and HTTP sender.
/// Every timestamp is recorded at most once. The hot path only performs a cheap already-set
/// check after the first audio chunk.
/// </summary>
internal sealed class RequestDiagnostics(long requestId, long requestStartedTimestamp)
{
    private long _generationStartedTimestamp;
    private long _piperReadyTimestamp;
    private long _cloneReadyTimestamp;
    private long _processedReadyTimestamp;
    private long _encoderInputTimestamp;
    private long _encodedAudioTimestamp;
    private long _transportQueuedTimestamp;
    private long _httpAudioFlushedTimestamp;

    public long RequestId { get; } = requestId;
    public long RequestStartedTimestamp { get; } = requestStartedTimestamp;

    public bool HasEncoderInput => Volatile.Read(ref _encoderInputTimestamp) != 0;
    public bool HasEncodedAudio => Volatile.Read(ref _encodedAudioTimestamp) != 0;

    public void StartGeneration(long timestamp)
    {
        Volatile.Write(ref _generationStartedTimestamp, timestamp);
    }

    public void MarkPiperReady() => MarkOnce(ref _piperReadyTimestamp);
    public void MarkCloneReady() => MarkOnce(ref _cloneReadyTimestamp);
    public void MarkProcessedReady() => MarkOnce(ref _processedReadyTimestamp);
    public void MarkEncoderInput() => MarkOnce(ref _encoderInputTimestamp);
    public void MarkEncodedAudio() => MarkOnce(ref _encodedAudioTimestamp);
    public void MarkTransportQueued() => MarkOnce(ref _transportQueuedTimestamp);
    public void MarkHttpAudioFlushed() => MarkOnce(ref _httpAudioFlushedTimestamp);

    public double? PiperMs => FromGeneration(ref _piperReadyTimestamp);
    public double? CloneMs => FromGeneration(ref _cloneReadyTimestamp);
    public double? ProcessedMs => FromGeneration(ref _processedReadyTimestamp);
    public double? EncoderInputMs => FromGeneration(ref _encoderInputTimestamp);
    public double? EncodedAudioMs => FromGeneration(ref _encodedAudioTimestamp);
    public double? TransportQueuedMs => FromGeneration(ref _transportQueuedTimestamp);
    public double? HttpAudioMs => FromGeneration(ref _httpAudioFlushedTimestamp);

    /// <summary>
    /// Server-side time from request acceptance until the first audio-bearing HTTP chunk
    /// has been written and flushed. Network transit and client playback buffering are not included.
    /// </summary>
    public double? TtfaMs
    {
        get
        {
            long timestamp = Volatile.Read(ref _httpAudioFlushedTimestamp);
            return timestamp == 0
                ? null
                : Stopwatch.GetElapsedTime(RequestStartedTimestamp, timestamp).TotalMilliseconds;
        }
    }

    private double? FromGeneration(ref long timestamp)
    {
        long start = Volatile.Read(ref _generationStartedTimestamp);
        long end = Volatile.Read(ref timestamp);

        return start == 0 || end == 0
            ? null
            : Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
    }

    private static void MarkOnce(ref long target)
    {
        if (Volatile.Read(ref target) != 0)
        {
            return;
        }

        long timestamp = Stopwatch.GetTimestamp();
        Interlocked.CompareExchange(ref target, timestamp, 0);
    }
}

/// <summary>
/// Carries one request's identifier and timing state across async continuations and Task.Run.
/// The ambient object is shared by every CleanConsoleFormatter instance, including loggers
/// created by the bootstrap LoggerFactory before the main DI container exists.
/// </summary>
internal static class RequestLogContext
{
    private static readonly AsyncLocal<RequestDiagnostics?> CurrentRequest = new();

    public static RequestDiagnostics? Current => CurrentRequest.Value;
    public static long? RequestId => CurrentRequest.Value?.RequestId;

    /// <summary>
    /// Creates one lightweight diagnostics object for the request and restores the previous
    /// ambient value when the returned scope is disposed.
    /// </summary>
    public static Scope Push(long requestId, long requestStartedTimestamp)
    {
        RequestDiagnostics? previous = CurrentRequest.Value;
        var diagnostics = new RequestDiagnostics(requestId, requestStartedTimestamp);
        CurrentRequest.Value = diagnostics;
        return new Scope(previous, diagnostics);
    }

    public readonly struct Scope(
        RequestDiagnostics? previous,
        RequestDiagnostics diagnostics) : IDisposable
    {
        public RequestDiagnostics Diagnostics { get; } = diagnostics;

        public void Dispose()
        {
            CurrentRequest.Value = previous;
        }
    }
}

/// <summary>
/// Minimal console formatter for Tsubaki diagnostics. Request IDs are read from the ambient
/// request context, while timestamp and ID formatting use stack buffers to avoid creating an
/// additional combined log string for every message.
/// </summary>
public sealed class CleanConsoleFormatter : ConsoleFormatter
{
    public CleanConsoleFormatter() : base("clean") { }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        string? message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        string level = logEntry.LogLevel switch
        {
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error or LogLevel.Critical => "fail",
            LogLevel.Debug => "dbug",
            _ => "trce"
        };

        Span<char> timestampBuffer = stackalloc char[8];
        DateTime.Now.TryFormat(
            timestampBuffer,
            out int timestampLength,
            "HH:mm:ss",
            CultureInfo.InvariantCulture);

        textWriter.Write(timestampBuffer[..timestampLength]);
        textWriter.Write(' ');
        textWriter.Write(level);
        textWriter.Write(": ");

        long? requestId = RequestLogContext.RequestId;
        if (requestId.HasValue)
        {
            textWriter.Write("[R");

            Span<char> requestIdBuffer = stackalloc char[20];
            requestId.Value.TryFormat(
                requestIdBuffer,
                out int requestIdLength,
                "D4",
                CultureInfo.InvariantCulture);

            textWriter.Write(requestIdBuffer[..requestIdLength]);
            textWriter.Write("] ");
        }

        textWriter.WriteLine(message);

        if (logEntry.Exception != null)
        {
            textWriter.WriteLine(logEntry.Exception);
        }
    }
}
