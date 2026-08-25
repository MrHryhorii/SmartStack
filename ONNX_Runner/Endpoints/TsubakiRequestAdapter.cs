using ONNX_Runner.Models;

namespace ONNX_Runner.Endpoints;

/// <summary>
/// Translates a request to Tsubaki's own extended endpoint into the engine's wire-agnostic
/// SynthesisRequest. Nearly a 1:1 field copy, since SynthesisRequest was modeled directly
/// after this extended shape.
/// </summary>
public static class TsubakiRequestAdapter
{
    public static (SynthesisRequest? Request, string? FormatError) ToSynthesisRequest(TsubakiSpeechRequest dto)
    {
        if (!Enum.TryParse<AudioFormat>(dto.ResponseFormat, true, out var format))
        {
            return (null, $"Unsupported response_format: '{dto.ResponseFormat}'. Supported formats are: wav, mp3, opus, pcm.");
        }

        return (new SynthesisRequest
        {
            Input = dto.Input,
            Format = format,
            Voice = dto.Voice,
            Speed = dto.Speed,
            Stream = dto.Stream,
            NoiseScale = dto.NoiseScale,
            NoiseW = dto.NoiseW,
            Effect = dto.Effect,
            EffectIntensity = dto.EffectIntensity,
            Environment = dto.Environment,
            EnvironmentIntensity = dto.EnvironmentIntensity,
            Pitch = dto.Pitch,
            Volume = dto.Volume,
            CloneIntensity = dto.CloneIntensity,
            ToneTemperature = dto.ToneTemperature
        }, null);
    }
}
