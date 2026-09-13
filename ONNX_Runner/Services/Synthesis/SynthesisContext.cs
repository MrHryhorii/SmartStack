using ONNX_Runner.Models;

namespace ONNX_Runner.Services.Synthesis;

/// <summary>
/// Bundles everything the generation pipeline needs so producer/consumer code does not
/// have to thread a dozen separate parameters through every stage. HTTP response shape,
/// JSON wrapping, buffering, and network streaming deliberately do not belong here.
/// </summary>
internal sealed class SynthesisContext
{
    public required SynthesisRequest Request;
    public required PiperConfig PiperConfig;
    public required ClonerSettings ClonerConfig;
    public required DspSettings DspConfig;
    public required EffectsSettings EffectsConfig;
    public required ChunkerSettings ChunkerConfig;
    public required TextChunker TextChunker;
    public required UnifiedPhonemizer Phonemizer;
    public required PiperRunner PiperRunner;
    public required bool CanClone;
    public required OpenVoiceRunner? OpenVoice;
    public required AudioProcessor? AudioProc;
    public required AudioFormat AudioFormat;
    public required int OutSampleRate;
    public required int FinalSampleRate;
    public required int DisplaySampleRate;
}
