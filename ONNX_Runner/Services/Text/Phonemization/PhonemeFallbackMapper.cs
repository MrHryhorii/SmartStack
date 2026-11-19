using ONNX_Runner.Models.Inference;
using ONNX_Runner.Services.Text.Phonemization.Adaptation;

namespace ONNX_Runner.Services.Text.Phonemization;

/// <summary>Compatibility facade for the prepared structural and feature-based adapter.</summary>
public sealed class PhonemeFallbackMapper(string csvPath, PiperConfig piperConfig)
{
    public PhonemeAdapter Adapter { get; } = new(csvPath, piperConfig);
    public int MaxSequenceLength => Adapter.MaxSourceLength;

    public bool TryGetSequenceFallback(ReadOnlySpan<char> sequence, out string fallback)
    {
        if (Adapter.TryGetPlan(sequence, out var plan)) { fallback = plan.Text; return true; }
        fallback = string.Empty;
        return false;
    }

    public bool TryGetClosestPhoneme(ReadOnlySpan<char> phoneme, out string fallback)
    {
        if (Adapter.TryGetKnown(phoneme, out var plan)) { fallback = plan.Text; return true; }
        fallback = string.Empty;
        return false;
    }

    internal static int GetPhonemeUnitLength(ReadOnlySpan<char> source) => IpaReader.ReadLength(source);
}
