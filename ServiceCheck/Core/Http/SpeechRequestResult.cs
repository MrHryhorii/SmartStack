using System.Text.Json.Serialization;

namespace Tsubaki.ServiceCheck.Core.Http;

/// <summary>Describes how a speech request finished.</summary>
public enum SpeechRequestState
{
    Completed,
    Failed,
    TimedOut,
    Cancelled,
    Error
}

/// <summary>Captures transport evidence from one speech request.</summary>
public sealed record SpeechRequestResult
{
    public required SpeechRequestState State { get; init; }
    public required string Code { get; init; }
    public required string Summary { get; init; }

    public required Uri RequestUri { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset FinishedAtUtc { get; init; }
    public required long TotalDurationMs { get; init; }

    public int? StatusCode { get; init; }
    public string? MediaType { get; init; }
    public long? DeclaredContentLength { get; init; }
    public long? BodyBytesRead { get; init; }
    public bool BodyComplete { get; init; }

    public long? HeadersDurationMs { get; init; }
    public long? BodyDurationMs { get; init; }

    public int? ReportedSampleRateHz { get; init; }
    public string? RedirectLocation { get; init; }
    public string? ErrorExcerpt { get; init; }
    public bool ErrorExcerptTruncated { get; init; }
    public string? ExceptionType { get; init; }

    [JsonIgnore]
    public byte[]? Payload { get; init; }
}