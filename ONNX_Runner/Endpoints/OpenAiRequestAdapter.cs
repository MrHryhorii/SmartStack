using ONNX_Runner.Models.Audio;
using ONNX_Runner.Models.Requests;

namespace ONNX_Runner.Endpoints;
/// <summary>
/// Translates an incoming OpenAI-shaped request into the engine's wire-agnostic
/// SynthesisRequest. Wire-specific compatibility and validation stay here so the
/// synthesis service never needs to know which external API shape was used.
/// </summary>
public static class OpenAiRequestAdapter
{
    /// <summary>Resolves optional OpenAI controls while leaving engine extensions at server defaults.</summary>
    public static (SynthesisRequest? Request, string? ValidationError) ToSynthesisRequest(
        OpenAiSpeechRequest dto)
    {
        if (!AudioFormatParser.TryParse(dto.ResponseFormat, out AudioFormat format)) format = AudioFormat.Mp3;
        if (!SpeechStreamFormatParser.TryParse(dto.StreamFormat, out SpeechStreamFormat streamFormat)) streamFormat = SpeechStreamFormat.Audio;

        string voice = string.IsNullOrWhiteSpace(dto.Voice?.Id)
            ? "piper_base"
            : dto.Voice.Id.Trim();

        // dto.Instructions is intentionally accepted but ignored.
        // Piper/OpenVoice do not expose an equivalent natural-language style-control input.
        return (new SynthesisRequest
        {
            Input = dto.Input,
            Format = format,
            Voice = voice,
            Speed = dto.Speed,
            StreamFormat = streamFormat,
            Stream = dto.Stream

            // DSP effects and cloning tuning are Tsubaki-specific extensions, not
            // part of the OpenAI compatibility surface. Their null defaults allow
            // the server configuration to resolve them downstream.
        }, null);
    }
}