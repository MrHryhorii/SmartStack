using System.Text.Json;
using System.Text.Json.Serialization;
using Tsubaki.ServiceCheck.Core.Diagnostics;

namespace Tsubaki.ServiceCheck.Core.Reports;

/// <summary>
/// Serializes canonical diagnostic reports.
/// </summary>
public static class ReportJson
{
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

    /// <summary>
    /// Serializes a supported result as UTF-8 JSON without a BOM.
    /// </summary>
    public static byte[] Serialize(DiagnosticRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.SchemaVersion != 1)
        {
            throw new ArgumentException(
                "Unsupported diagnostic result schema version.",
                nameof(result));
        }

        return JsonSerializer.SerializeToUtf8Bytes(result, Options);
    }

    /// <summary>
    /// Normalizes report timestamps to UTC.
    /// </summary>
    private sealed class UtcDateTimeOffsetConverter
        : JsonConverter<DateTimeOffset>
    {
        /// <summary>
        /// Reads an ISO 8601 timestamp and normalizes it to UTC.
        /// </summary>
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            return reader.GetDateTimeOffset().ToUniversalTime();
        }

        /// <summary>
        /// Writes an ISO 8601 timestamp with the UTC designator.
        /// </summary>
        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.UtcDateTime);
        }
    }
}