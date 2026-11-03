using ONNX_Runner.Models.Audio;
using ONNX_Runner.Models.Requests;

namespace ONNX_Runner.Endpoints;
/// <summary>
/// Translates a request to Tsubaki's own extended endpoint into the engine's wire-agnostic
/// SynthesisRequest. Nearly a 1:1 field copy, since SynthesisRequest was modeled directly
/// after this extended shape.
/// </summary>
public static class TsubakiRequestAdapter
{
    /// <summary>Resolves optional wire controls and preserves inherited defaults for unusable selections.</summary>
    public static (SynthesisRequest? Request, string? ValidationError) ToSynthesisRequest(TsubakiSpeechRequest dto)
    {
        if (!AudioFormatParser.TryParse(dto.ResponseFormat, out AudioFormat format)) format = AudioFormat.Mp3;
        if (!SpeechStreamFormatParser.TryParse(dto.StreamFormat, out SpeechStreamFormat streamFormat)) streamFormat = SpeechStreamFormat.Audio;

        // dto.Instructions is intentionally accepted but ignored.
        // It is a wire-compatibility field only and must not enter SynthesisRequest.
        string? cleanLanguage = dto.Language?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cleanLanguage) || cleanLanguage == "auto")
        {
            // If the client passed "auto", " AUTO " or just spaces - we reset to null.
            // Then the backend will understand that it is necessary to enable Lingua detection or the default model.
            cleanLanguage = null;
        }
        return (new SynthesisRequest
        {
            Input = dto.Input,
            Format = format,
            SampleRate = dto.SampleRate,
            Voice = string.IsNullOrWhiteSpace(dto.Voice) ? "piper_base" : dto.Voice.Trim(),
            Speed = dto.Speed,
            StreamFormat = streamFormat,
            Stream = dto.Stream,
            EarlySplit = dto.EarlySplit,
            NoiseScale = dto.NoiseScale,
            NoiseW = dto.NoiseW,
            Equalizer = SpeechRequestJson.Equalizer(dto.Equalizer),
            Effect = SpeechRequestJson.EnumName<VoiceEffectType>(dto.Effect),
            EffectIntensity = dto.EffectIntensity,
            Environment = SpeechRequestJson.EnumName<SpatialEnvironment>(dto.Environment),
            EnvironmentIntensity = dto.EnvironmentIntensity,
            Pitch = dto.Pitch,
            Volume = dto.Volume,
            Language = cleanLanguage,
            CloneIntensity = dto.CloneIntensity,
            ToneTemperature = dto.ToneTemperature,
            LowPassQFactor = dto.LowPassQFactor,
            ExtendReverbTail = dto.ExtendReverbTail
        }, null);
    }
}
