// Derived from OpenAL Soft 1.23.1 core/filters/biquad.{h,cpp}.
// C# adaptation for Tsubaki TTS, 2026-10-31.
// SPDX-License-Identifier: GPL-3.0-or-later
// This adapted copy uses GPL-3.0-or-later under LGPL-2.0 section 3; see LICENSE.
// Modified 2026-10-31: integrated into the Tsubaki spatial service namespace.
// Modified 2026-10-31: low-pass cutoff updates retain the existing filter history.
namespace ONNX_Runner.Services.Spatial;

public enum SpatialFilterType { HighShelf, LowShelf, Peaking, LowPass, HighPass, BandPass }

/// <summary>One second-order filter with independent, request-local state.</summary>
public sealed class SpatialFilterKernel
{
    private SpatialBiquad _filter;
    private SpatialFilterKernel(in SpatialBiquad filter) => _filter = filter;

    /// <summary>
    /// Matches the original slope API. Reference gain is linear; a shelf's plateau
    /// or peaking filter's center gain is its square. Slope 1 gives Butterworth cutoff filters.
    /// </summary>
    public static SpatialFilterKernel FromSlope(int sampleRate, SpatialFilterType type, float frequencyHz,
        float referenceGain = 1, float slope = 1)
    {
        Validate(sampleRate, type, frequencyHz, referenceGain);
        if (!float.IsFinite(slope) || slope <= 0 || slope > 1) throw new ArgumentOutOfRangeException(nameof(slope));
        return Create(SpatialBiquad.FromSlope(type, frequencyHz / sampleRate, referenceGain, slope));
    }

    /// <summary>Matches the original bandwidth API; width is in octaves.</summary>
    public static SpatialFilterKernel FromBandwidth(int sampleRate, SpatialFilterType type, float frequencyHz,
        float referenceGain = 1, float bandwidthOctaves = 1)
    {
        Validate(sampleRate, type, frequencyHz, referenceGain);
        if (!float.IsFinite(bandwidthOctaves) || bandwidthOctaves <= 0)
            throw new ArgumentOutOfRangeException(nameof(bandwidthOctaves));
        return Create(SpatialBiquad.FromBandwidth(type, frequencyHz / sampleRate, referenceGain, bandwidthOctaves));
    }

    private static SpatialFilterKernel Create(in SpatialBiquad filter)
    {
        ValidateCoefficients(filter);
        return new SpatialFilterKernel(filter);
    }

    /// <summary>Updates a Butterworth low-pass cutoff without clearing its delay state or allocating.</summary>
    public void SetLowPass(int sampleRate, float frequencyHz)
    {
        Validate(sampleRate, SpatialFilterType.LowPass, frequencyHz, 1);
        var filter = SpatialBiquad.FromSlope(SpatialFilterType.LowPass, frequencyHz / sampleRate, 1, 1);
        ValidateCoefficients(filter);
        _filter.CopyCoefficientsFrom(filter);
    }

    private static void ValidateCoefficients(in SpatialBiquad filter)
    {
        var c = filter.Coefficients;
        if (!float.IsFinite(c.B0) || !float.IsFinite(c.B1) || !float.IsFinite(c.B2)
            || !float.IsFinite(c.A1) || !float.IsFinite(c.A2))
            throw new ArgumentException("Filter parameters produce nonfinite coefficients.");
    }

    private static void Validate(int sampleRate, SpatialFilterType type, float frequencyHz, float gain)
    {
        if (sampleRate < 8000 || sampleRate > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (type < SpatialFilterType.HighShelf || type > SpatialFilterType.BandPass) throw new ArgumentOutOfRangeException(nameof(type));
        if (!float.IsFinite(frequencyHz) || frequencyHz <= 0 || frequencyHz >= sampleRate * .5f)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        if (!float.IsFinite(gain) || gain < 0 || gain > 16) throw new ArgumentOutOfRangeException(nameof(gain));
    }

    /// <summary>Processes finite PCM without clipping or level makeup.</summary>
    public float Process(float input) => _filter.Process(input);
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        SpatialBuffer.Validate(input, output);
        for (int i = 0; i < input.Length; i++) output[i] = _filter.Process(input[i]);
    }
    public void Reset() => _filter.Reset();
}

internal static class SpatialBuffer
{
    internal static void Validate(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != output.Length) throw new ArgumentException("Signal lengths differ.");
        if (input.Overlaps(output, out int offset) && offset != 0)
            throw new ArgumentException("Signals partially overlap.", nameof(output));
    }
}
