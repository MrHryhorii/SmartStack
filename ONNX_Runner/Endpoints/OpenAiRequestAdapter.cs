using ONNX_Runner.Models;

namespace ONNX_Runner.Endpoints;

/// <summary>
/// Translates an incoming OpenAI-shaped request into the engine's wire-agnostic
/// SynthesisRequest. Parsing/validating the response_format string is done here, not in
/// SpeechSynthesisService, since the accepted format names can differ per external API.
/// </summary>
public static class OpenAiRequestAdapter
{
    public static (SynthesisRequest? Request, string? FormatError) ToSynthesisRequest(OpenAiSpeechRequest dto)
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
            Stream = dto.Stream
            // DSP effects and cloning tuning are Tsubaki-specific extensions, not
            // part of the official OpenAI schema — left at their SynthesisRequest defaults
            // (null) here, so the server's own config defaults apply downstream instead.
        }, null);
    }
}