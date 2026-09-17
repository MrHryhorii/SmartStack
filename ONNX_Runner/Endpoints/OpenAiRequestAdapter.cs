using ONNX_Runner.Models;

namespace ONNX_Runner.Endpoints;

/// <summary>
/// Translates an incoming OpenAI-shaped request into the engine's wire-agnostic
/// SynthesisRequest. Wire-specific compatibility and validation stay here so the
/// synthesis service never needs to know which external API shape was used.
/// </summary>
public static class OpenAiRequestAdapter
{
    public static (SynthesisRequest? Request, string? ValidationError) ToSynthesisRequest(
        OpenAiSpeechRequest dto)
    {
        AudioFormat format;
        string formatStr = dto.ResponseFormat?.Trim().ToLowerInvariant() ?? "mp3";

        if (formatStr == "b64_json")
        {
            format = AudioFormat.B64Json;
        }
        else if (!Enum.TryParse(formatStr, true, out format))
        {
            return (
                null,
                $"Unsupported response_format: '{dto.ResponseFormat}'. " +
                "Supported formats are: wav, mp3, opus, flac, pcm, b64_json.");
        }

        string? streamFormat = dto.StreamFormat?.Trim().ToLowerInvariant();

        if (!string.IsNullOrEmpty(streamFormat) && streamFormat != "audio")
        {
            if (streamFormat == "sse")
            {
                return (
                    null,
                    "Unsupported stream_format: 'sse'. " +
                    "Tsubaki currently supports only 'audio'; SSE event streaming is not implemented.");
            }

            return (
                null,
                $"Unsupported stream_format: '{dto.StreamFormat}'. Supported value is: audio.");
        }

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
            Stream = dto.Stream

            // DSP effects and cloning tuning are Tsubaki-specific extensions, not
            // part of the OpenAI compatibility surface. Their null defaults allow
            // the server configuration to resolve them downstream.
        }, null);
    }
}