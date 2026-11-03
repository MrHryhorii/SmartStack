using System.Globalization;
using System.Text.Json;
using ONNX_Runner.Models.Audio;
using ONNX_Runner.Models.Requests;

namespace ONNX_Runner.Endpoints;

/// <summary>Normalizes input to text and ignores optional values that cannot be read in their supported format.</summary>
internal static class SpeechRequestJson
{
    internal static async Task<(T? Request, IResult? Error)> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType()) return (default, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        try
        {
            using var document = await request.ReadFromJsonAsync<JsonDocument>(JsonSerializerOptions.Web, cancellationToken);
            if (document == null || document.RootElement.ValueKind != JsonValueKind.Object)
                return (default, Results.BadRequest(new { error = "A JSON request object is required." }));

            object parsed = typeof(T) == typeof(TsubakiSpeechRequest) ? Tsubaki(document.RootElement)
                : typeof(T) == typeof(OpenAiSpeechRequest) ? OpenAi(document.RootElement)
                : throw new NotSupportedException("Unsupported speech request type.");
            string input = parsed is TsubakiSpeechRequest tsubaki ? tsubaki.Input : ((OpenAiSpeechRequest)parsed).Input;
            return string.IsNullOrWhiteSpace(input)
                ? (default, Results.BadRequest(new { error = "Input text cannot be empty." }))
                : ((T)parsed, null);
        }
        catch (JsonException)
        {
            return (default, Results.BadRequest(new { error = "The request must contain valid JSON." }));
        }
    }

    private static TsubakiSpeechRequest Tsubaki(JsonElement root)
    {
        var request = new TsubakiSpeechRequest { Input = string.Empty };
        foreach (JsonProperty property in root.EnumerateObject())
        {
            JsonElement value = property.Value;
            switch (property.Name.ToLowerInvariant())
            {
                case "input": request.Input = Input(value); break;
                case "voice": request.Voice = Text(value) ?? "piper_base"; break;
                case "response_format": request.ResponseFormat = Text(value) ?? "mp3"; break;
                case "stream_format": request.StreamFormat = Text(value); break;
                case "speed": request.Speed = Single(value) ?? 1; break;
                case "stream": request.Stream = Boolean(value); break;
                case "early_split": request.EarlySplit = Boolean(value); break;
                case "samplerate": request.SampleRate = Integer(value); break;
                case "noise_scale": request.NoiseScale = Single(value); break;
                case "noise_w": request.NoiseW = Single(value); break;
                case "equalizer": request.Equalizer = Profiles(value); break;
                case "effect": request.Effect = Text(value); break;
                case "effect_intensity": request.EffectIntensity = Single(value); break;
                case "environment": request.Environment = Text(value); break;
                case "environment_intensity": request.EnvironmentIntensity = Single(value); break;
                case "pitch": request.Pitch = Single(value); break;
                case "volume": request.Volume = Single(value); break;
                case "language": request.Language = Text(value); break;
                case "clone_intensity": request.CloneIntensity = Single(value); break;
                case "tone_temperature": request.ToneTemperature = Single(value); break;
                case "low_pass_q_factor": request.LowPassQFactor = Single(value); break;
                case "extend_reverb_tail": request.ExtendReverbTail = Boolean(value); break;
            }
        }
        return request;
    }

    private static OpenAiSpeechRequest OpenAi(JsonElement root)
    {
        var request = new OpenAiSpeechRequest { Input = string.Empty };
        foreach (JsonProperty property in root.EnumerateObject())
        {
            JsonElement value = property.Value;
            switch (property.Name.ToLowerInvariant())
            {
                case "input": request.Input = Input(value); break;
                case "voice": request.Voice = new OpenAiVoice(Voice(value) ?? "piper_base"); break;
                case "response_format": request.ResponseFormat = Text(value) ?? "mp3"; break;
                case "stream_format": request.StreamFormat = Text(value); break;
                case "speed": request.Speed = Single(value) ?? 1; break;
                case "stream": request.Stream = Boolean(value); break;
            }
        }
        return request;
    }

    private static string Input(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.GetRawText()
    };
    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? Voice(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out JsonElement id) ? Text(id) : Text(value);

    private static float? Single(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float number) && float.IsFinite(number)) return number;
        if (value.ValueKind == JsonValueKind.String && float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && float.IsFinite(number)) return number;
        return null;
    }
    private static int? Integer(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
    }
    private static bool? Boolean(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool flag) ? flag : null;
    }
    private static string?[]? Profiles(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return null;
        int count = Math.Min(value.GetArrayLength(), AudioEqualizerProfiles.MaxProfilesPerRequest);
        if (count == 0) return [];
        var profiles = new string?[count];
        for (int i = 0; i < count; i++)
        {
            if (value[i].ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return null;
            profiles[i] = value[i].GetString();
        }
        return profiles;
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
}
