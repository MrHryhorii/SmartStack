using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tsubaki.ServiceCheck.Core.Diagnostics;

namespace Tsubaki.ServiceCheck.Core.Reports;

/// <summary>Serializes and parses canonical diagnostic reports.</summary>
public static class ReportJson
{
    internal const int MaximumInputBytes = 8 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        RespectNullableAnnotations = true,
        MaxDepth = 32,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.SnakeCaseLower,
                allowIntegerValues: false),
            new UtcDateTimeOffsetConverter()
        }
    };

    private static readonly JsonSerializerOptions ReadOptions = new(Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true
    };

    /// <summary>Serializes a supported result as UTF-8 JSON without a BOM.</summary>
    public static byte[] Serialize(DiagnosticRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.SchemaVersion != 1)
            throw new ArgumentException(
                "Unsupported diagnostic result schema version.",
                nameof(result));

        return JsonSerializer.SerializeToUtf8Bytes(result, Options);
    }

    /// <summary>Parses bounded report JSON before semantic validation.</summary>
    internal static DiagnosticRunResult Deserialize(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaximumInputBytes)
            throw new JsonException(
                $"Report exceeds the maximum size of {MaximumInputBytes} bytes.");

        if (json.StartsWith("\uFEFF"u8))
            json = json[3..];

        ValidateUtf8(json);

        var result = JsonSerializer.Deserialize<DiagnosticRunResult>(
            json,
            ReadOptions)
            ?? throw new JsonException("The report must be a JSON object.");

        if (result.SchemaVersion != 1)
            throw new JsonException(
                $"Unsupported report schemaVersion {result.SchemaVersion}. Expected 1.");

        return result;
    }

    /// <summary>Rejects malformed UTF-8 instead of replacing invalid bytes.</summary>
    private static void ValidateUtf8(ReadOnlySpan<byte> json)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(json);
        }
        catch (DecoderFallbackException exception)
        {
            throw new JsonException(
                "The report must contain valid UTF-8 text.",
                exception);
        }
    }

    /// <summary>Normalizes report timestamps to UTC.</summary>
    private sealed class UtcDateTimeOffsetConverter
        : JsonConverter<DateTimeOffset>
    {
        /// <summary>Reads an ISO 8601 timestamp and normalizes it to UTC.</summary>
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            return reader.GetDateTimeOffset().ToUniversalTime();
        }

        /// <summary>Writes an ISO 8601 timestamp with the UTC designator.</summary>
        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.UtcDateTime);
        }
    }
}
