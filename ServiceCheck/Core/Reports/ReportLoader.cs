using System.Text;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Diagnostics;

namespace Tsubaki.ServiceCheck.Core.Reports;

/// <summary>Loads bounded diagnostic reports without following stored references.</summary>
public static class ReportLoader
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = 32,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    /// <summary>Reads and validates one UTF-8 diagnostic report.</summary>
    public static async Task<DiagnosticRunResult> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[ReportJson.MaximumInputBytes + 1];
        var length = 0;

        while (length < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(
                buffer.AsMemory(length),
                cancellationToken);

            if (bytesRead == 0)
                break;

            length += bytesRead;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (length > ReportJson.MaximumInputBytes)
            throw new JsonException(
                $"Report exceeds the maximum size of {ReportJson.MaximumInputBytes} bytes.");

        return Parse(buffer.AsMemory(0, length), cancellationToken);
    }

    /// <summary>Checks JSON structure before creating the typed report.</summary>
    private static DiagnosticRunResult Parse(
        ReadOnlyMemory<byte> json,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (json.Span.StartsWith("\uFEFF"u8))
            json = json[3..];

        try
        {
            using var document = JsonDocument.Parse(json, DocumentOptions);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("The report must be a JSON object.");

            if (!root.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var version) || version != 1)
            {
                throw new JsonException("Unsupported report schemaVersion. Expected 1.");
            }

            if (root.TryGetProperty("audioPayload", out _))
                throw new JsonException("Canonical reports must not contain audioPayload.");

            ValidateStructure(root, cancellationToken);
            ValidateSnapshotSettings(root);
            cancellationToken.ThrowIfCancellationRequested();

            var result = ReportJson.Deserialize(json.Span);
            ReportValidator.Validate(result, cancellationToken);
            return result;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or FormatException or DecoderFallbackException)
        {
            throw new JsonException("The report contains invalid JSON field values.", exception);
        }
    }

    /// <summary>Bounds strings and collections before typed deserialization.</summary>
    private static void ValidateStructure(
        JsonElement value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                RequireStringBound(value.GetString()!);
                return;

            case JsonValueKind.Array:
                if (value.GetArrayLength() > ReportValidator.MaximumCollectionItems)
                    throw new JsonException("A report array exceeds the supported item limit.");

                foreach (var item in value.EnumerateArray())
                    ValidateStructure(item, cancellationToken);

                return;

            case JsonValueKind.Object:
                var count = 0;

                foreach (var property in value.EnumerateObject())
                {
                    if (++count > ReportValidator.MaximumCollectionItems)
                        throw new JsonException("A report object exceeds the supported property limit.");

                    RequireStringBound(property.Name);

                    if (property.Name is "startedAtUtc" or "finishedAtUtc" or "observedAtUtc"
                        or "readinessObservedAtUtc" or "serverTimestampUtc")
                    {
                        ValidateTimestamp(property.Value);
                    }

                    ValidateStructure(property.Value, cancellationToken);
                }

                return;
        }
    }

    /// <summary>Requires an explicit time-zone offset for recorded timestamps.</summary>
    private static void ValidateTimestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return;

        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out _))
            throw new JsonException("Report timestamps must use ISO 8601 strings.");

        var text = value.GetString()!;

        if (text.EndsWith('Z'))
            return;

        if (text.Length >= 6 && (text[^6] is '+' or '-') && text[^3] == ':')
            return;

        throw new JsonException("Report timestamps must include Z or an explicit time-zone offset.");
    }

    /// <summary>Prevents missing exported settings from acquiring current defaults.</summary>
    private static void ValidateSnapshotSettings(JsonElement root)
    {
        var profile = RequireObject(root, "profile");

        RequireProperties(RequireObject(profile, "routes"),
            "health", "healthFallback", "status", "voices", "speech");
        RequireProperties(RequireObject(profile, "timeoutsMs"),
            "dns", "tcp", "metadata", "speech", "run");
        RequireProperties(RequireObject(profile, "limits"),
            "maxMetadataBytes", "maxAudioBytes", "maxInputCharacters");
    }

    /// <summary>Returns a required JSON object without supplying a default.</summary>
    private static JsonElement RequireObject(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Report {propertyName} must be a JSON object.");
        }

        return value;
    }

    /// <summary>Requires every exported member of a settings object.</summary>
    private static void RequireProperties(JsonElement value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!value.TryGetProperty(propertyName, out _))
                throw new JsonException($"Report settings are missing '{propertyName}'.");
        }
    }

    /// <summary>Rejects oversized decoded strings and property names.</summary>
    private static void RequireStringBound(string value)
    {
        if (value.Length > ReportValidator.MaximumStringCharacters)
            throw new JsonException("A report string exceeds the supported character limit.");
    }
}
