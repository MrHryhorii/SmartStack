using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ONNX_Runner.Models.Audio;
using ONNX_Runner.Models.Requests;

namespace ONNX_Runner.Endpoints;

/// <summary>Reads speech requests field by field, preserving defaults when an optional value is unusable.</summary>
internal static class SpeechRequestJson
{
    // These options belong only to incoming speech bodies; response serialization and API schemas stay typed.
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    internal static async Task<(T? Request, IResult? Error)> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType()) return (default, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        try
        {
            T? value = await request.ReadFromJsonAsync<T>(Options, cancellationToken);
            return value == null
                ? (default, Results.BadRequest(new { error = "A JSON request body is required." }))
                : (value, null);
        }
        catch (JsonException)
        {
            return (default, Results.BadRequest(new { error = "The request must contain valid JSON and a string input field." }));
        }
    }

    internal static string? EnumName<T>(string? value) where T : struct, Enum
    {
        if (!Enum.TryParse(value, true, out T parsed) || !Enum.IsDefined(parsed)) return null;
        string name = Enum.GetName(parsed)!;
        return string.Equals(value?.Trim(), name, StringComparison.OrdinalIgnoreCase) ? name : null;
    }

    internal static string?[]? Equalizer(string?[]? profiles)
    {
        if (profiles == null || profiles.Length == 0) return profiles;
        foreach (string? profile in profiles)
            if (AudioEqualizerProfiles.Find(profile) != null) return profiles;
        return null;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(TsubakiSpeechRequest) && info.Type != typeof(OpenAiSpeechRequest)) return;
            foreach (JsonPropertyInfo property in info.Properties)
            {
                if (property.Name == "input") continue;
                property.CustomConverter = Converter(property.PropertyType);
            }
        });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = resolver };
        options.MakeReadOnly();
        return options;
    }

    private static JsonConverter? Converter(Type type)
    {
        if (type == typeof(float?)) return new OptionalSingle();
        if (type == typeof(float)) return new Speed();
        if (type == typeof(int?)) return new OptionalInteger();
        if (type == typeof(bool?)) return new OptionalBoolean();
        if (type == typeof(string)) return new OptionalString();
        if (type == typeof(string[])) return new OptionalProfiles();
        if (type == typeof(OpenAiVoice)) return new OptionalVoice();
        return null;
    }

    private static float? Single(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetSingle(out float number) && float.IsFinite(number)) return number;
        if (reader.TokenType == JsonTokenType.String && float.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && float.IsFinite(number)) return number;
        reader.Skip();
        return null;
    }

    private sealed class OptionalSingle : JsonConverter<float?>
    {
        public override float? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => Single(ref reader);
        public override void Write(Utf8JsonWriter writer, float? value, JsonSerializerOptions options)
        {
            if (value.HasValue) writer.WriteNumberValue(value.Value); else writer.WriteNullValue();
        }
    }

    private sealed class Speed : JsonConverter<float>
    {
        public override bool HandleNull => true;
        public override float Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => Single(ref reader) ?? 1;
        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    private sealed class OptionalInteger : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value)) return value;
            reader.Skip();
            return null;
        }
        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
        {
            if (value.HasValue) writer.WriteNumberValue(value.Value); else writer.WriteNullValue();
        }
    }

    private sealed class OptionalBoolean : JsonConverter<bool?>
    {
        public override bool? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.True) return true;
            if (reader.TokenType == JsonTokenType.False) return false;
            reader.Skip();
            return null;
        }
        public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
        {
            if (value.HasValue) writer.WriteBooleanValue(value.Value); else writer.WriteNullValue();
        }
    }

    private sealed class OptionalString : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return reader.GetString();
            reader.Skip();
            return null;
        }
        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    private sealed class OptionalProfiles : JsonConverter<string?[]?>
    {
        public override string?[]? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return null; }
            var profiles = new List<string?>();
            int count = 0;
            bool invalid = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return invalid ? null : profiles.ToArray();
                if (count++ < AudioEqualizerProfiles.MaxProfilesPerRequest)
                {
                    if (reader.TokenType is JsonTokenType.String or JsonTokenType.Null) profiles.Add(reader.GetString());
                    else invalid = true;
                }
                reader.Skip();
            }
            throw new JsonException("The profile array is incomplete.");
        }
        public override void Write(Utf8JsonWriter writer, string?[]? value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value);
    }

    private sealed class OptionalVoice : JsonConverter<OpenAiVoice>
    {
        public override OpenAiVoice Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return new OpenAiVoice(reader.GetString() ?? "piper_base");
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                using var document = JsonDocument.ParseValue(ref reader);
                if (document.RootElement.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String) return new OpenAiVoice(id.GetString()!);
                return new OpenAiVoice("piper_base");
            }
            reader.Skip();
            return new OpenAiVoice("piper_base");
        }
        public override void Write(Utf8JsonWriter writer, OpenAiVoice value, JsonSerializerOptions options) => writer.WriteStringValue(value.Id);
    }
}
