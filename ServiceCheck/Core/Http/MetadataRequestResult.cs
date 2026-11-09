using System.Text.Json.Serialization;

namespace Tsubaki.ServiceCheck.Core.Http;

/// <summary>Identifies one configured metadata endpoint.</summary>
public enum MetadataEndpoint
{
    Health,
    HealthFallback,
    Status,
    Voices
}

/// <summary>Describes how a metadata request finished.</summary>
public enum MetadataRequestState
{
    Completed,
    Failed,
    TimedOut,
    Cancelled,
    Error
}

/// <summary>Captures transport evidence from one metadata GET request.</summary>
public sealed record MetadataRequestResult
{
    public required MetadataEndpoint Endpoint { get; init; }

    public required MetadataRequestState State { get; init; }

    public required string Code { get; init; }

    public required string Summary { get; init; }

    public required string Route { get; init; }

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

    public string? RedirectLocation { get; init; }

    public string? ErrorExcerpt { get; init; }

    public bool ErrorExcerptTruncated { get; init; }

    public string? ExceptionType { get; init; }

    [JsonIgnore]
    public byte[]? Payload { get; init; }
}