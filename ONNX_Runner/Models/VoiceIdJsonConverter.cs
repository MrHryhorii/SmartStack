using System.Text.Json;
using System.Text.Json.Serialization;

namespace ONNX_Runner.Models;

/// <summary>
/// Normalizes supported JSON voice selectors to a string ID.
/// Accepts a string, a number, or an object containing a string or numeric "id" property.
/// </summary>
public sealed class VoiceIdJsonConverter : JsonConverter<string>
{
    public override string Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() ?? string.Empty;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            return ReadNumericId(ref reader);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                "The 'voice' field must be a string, a number, or an object containing an 'id'.");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("id", out JsonElement idElement))
        {
            throw new JsonException(
                "The 'voice' object must contain an 'id' property.");
        }

        return idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString() ?? string.Empty,
            JsonValueKind.Number => idElement.GetRawText(),
            _ => throw new JsonException(
                "The 'voice.id' property must be a string or a number.")
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        string value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }

    private static string ReadNumericId(ref Utf8JsonReader reader)
    {
        // Parse the token as JSON instead of converting through Int64/Double so large IDs
        // and their exact textual representation are preserved without precision loss.
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }
}
