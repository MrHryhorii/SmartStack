using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace ONNX_Runner.Services;

/// <summary>
/// Carries the current synthesis request identifier across async continuations and Task.Run.
/// Only the request ID is kept here; timing instrumentation stays out of the hot audio path.
/// </summary>
internal static class RequestLogContext
{
    private static readonly AsyncLocal<long?> CurrentRequestId = new();

    public static long? RequestId => CurrentRequestId.Value;

    public static Scope Push(long requestId)
    {
        long? previousRequestId = CurrentRequestId.Value;
        CurrentRequestId.Value = requestId;
        return new Scope(previousRequestId);
    }

    public readonly struct Scope(long? previousRequestId) : IDisposable
    {
        public void Dispose()
        {
            CurrentRequestId.Value = previousRequestId;
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
