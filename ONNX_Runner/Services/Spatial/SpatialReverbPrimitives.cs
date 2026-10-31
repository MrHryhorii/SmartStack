// Derived from OpenAL Soft 1.23.1 core/filters/biquad.{h,cpp} and alc/effects/reverb.cpp.
// Copyright (C) 2008-2017 Chris Robinson and Christopher Fitzgerald and OpenAL Soft contributors.
// C# adaptation for Tsubaki TTS, 2026-10-31.
// SPDX-License-Identifier: GPL-3.0-or-later
// This adapted copy uses GPL-3.0-or-later under LGPL-2.0 section 3; see LICENSE.
// Modified 2026-10-31: integrated into the Tsubaki spatial service namespace.
// Modified 2026-10-31: coefficient copying supports filter updates without clearing delay state.
using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services.Spatial;

/// <summary>EFX shelf gain is the response at the reference frequency, not the asymptotic shelf gain.</summary>
internal struct SpatialBiquad
{
    private float _b0, _b1, _b2, _a1, _a2, _z1, _z2;

    internal static SpatialBiquad Shelf(bool high, float normalizedFrequency, float gain)
        => FromSlope(high ? SpatialFilterType.HighShelf : SpatialFilterType.LowShelf, normalizedFrequency, gain, 1);

    internal static SpatialBiquad FromSlope(SpatialFilterType type, float normalizedFrequency, float gain, float slope)
    {
        gain = MathF.Max(gain, .001f);
        float inverseQ = MathF.Sqrt((gain + 1 / gain) * (1 / slope - 1) + 2);
        return Create(type, normalizedFrequency, gain, inverseQ);
    }

    internal static SpatialBiquad FromBandwidth(SpatialFilterType type, float normalizedFrequency, float gain, float bandwidth)
    {
        float w = MathF.PI * 2 * normalizedFrequency;
        float inverseQ = 2 * MathF.Sinh(MathF.Log(2) / 2 * bandwidth * w / MathF.Sin(w));
        return Create(type, normalizedFrequency, gain, inverseQ);
    }

    private static SpatialBiquad Create(SpatialFilterType type, float normalizedFrequency, float gain, float inverseQ)
    {
        gain = MathF.Max(gain, .00001f);
        float w = MathF.PI * 2 * normalizedFrequency;
        float sin = MathF.Sin(w), cos = MathF.Cos(w);
        float alpha = sin / 2 * inverseQ;
        float beta = 2 * MathF.Sqrt(gain) * alpha;
        float b0, b1, b2, a0, a1, a2;
        if (type == SpatialFilterType.HighShelf)
        {
            b0 = gain * ((gain + 1) + (gain - 1) * cos + beta);
            b1 = -2 * gain * ((gain - 1) + (gain + 1) * cos);
            b2 = gain * ((gain + 1) + (gain - 1) * cos - beta);
            a0 = (gain + 1) - (gain - 1) * cos + beta;
            a1 = 2 * ((gain - 1) - (gain + 1) * cos);
            a2 = (gain + 1) - (gain - 1) * cos - beta;
        }
        else if (type == SpatialFilterType.LowShelf)
        {
            b0 = gain * ((gain + 1) - (gain - 1) * cos + beta);
            b1 = 2 * gain * ((gain - 1) - (gain + 1) * cos);
            b2 = gain * ((gain + 1) - (gain - 1) * cos - beta);
            a0 = (gain + 1) + (gain - 1) * cos + beta;
            a1 = -2 * ((gain - 1) + (gain + 1) * cos);
            a2 = (gain + 1) + (gain - 1) * cos - beta;
        }
        else
        {
            a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
            switch (type)
            {
                case SpatialFilterType.Peaking:
                    b0 = 1 + alpha * gain; b1 = -2 * cos; b2 = 1 - alpha * gain;
                    a0 = 1 + alpha / gain; a2 = 1 - alpha / gain;
                    break;
                case SpatialFilterType.LowPass:
                    b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2;
                    break;
                case SpatialFilterType.HighPass:
                    b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2;
                    break;
                case SpatialFilterType.BandPass:
                    b0 = alpha; b1 = 0; b2 = -alpha;
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(type));
            }
        }
        return new SpatialBiquad { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0, _a1 = a1 / a0, _a2 = a2 / a0 };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal float Process(float input)
    {
        float output = input * _b0 + _z1;
        _z1 = input * _b1 - output * _a1 + _z2;
        _z2 = input * _b2 - output * _a2;
        return output;
    }
    // Copy only coefficients so a cutoff update preserves causal filter history.
    internal void CopyCoefficientsFrom(in SpatialBiquad filter)
    {
        _b0 = filter._b0; _b1 = filter._b1; _b2 = filter._b2;
        _a1 = filter._a1; _a2 = filter._a2;
    }
    internal void Reset() => _z1 = _z2 = 0;
    internal (float B0, float B1, float B2, float A1, float A2) Coefficients => (_b0, _b1, _b2, _a1, _a2);
}

internal readonly record struct SpatialFrame4(float A, float B, float C, float D)
{
    internal float this[int index] => index switch { 0 => A, 1 => B, 2 => C, 3 => D, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
    internal SpatialFrame4 Reversed => new(D, C, B, A);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static SpatialFrame4 Scatter(in SpatialFrame4 value, float x, float y) => new(
        x * value.A + y * (value.B + -value.C + value.D),
        x * value.B + y * (-value.A + value.C + value.D),
        x * value.C + y * (value.A + -value.B + value.D),
        x * value.D + y * (-value.A + -value.B + -value.C));
}

/// <summary>Four interleaved channels, with absolute frame indexing and independent per-channel taps.</summary>
internal sealed class SpatialDelayLine4
{
    private readonly float[] _samples;
    private readonly int _mask;
    internal int CapacityFrames => _mask + 1;

    internal SpatialDelayLine4(int requiredFrames)
    {
        int count = 1;
        while (count < requiredFrames) count <<= 1;
        _mask = count - 1;
        _samples = new float[checked(count * 4)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal float Read(int frame, int channel) => _samples[((frame & _mask) << 2) + channel];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Write(int frame, in SpatialFrame4 value)
    {
        int index = (frame & _mask) << 2;
        _samples[index] = value.A; _samples[index + 1] = value.B;
        _samples[index + 2] = value.C; _samples[index + 3] = value.D;
    }
    internal void Reset() => Array.Clear(_samples);
}

/// <summary>Gerzon four-line all-pass with the original orthogonal diffusion matrix.</summary>
internal sealed class SpatialAllPass4
{
    private readonly SpatialDelayLine4 _delay;
    private readonly int[] _offsets;
    private readonly float _coefficient;

    internal SpatialAllPass4(int[] offsets, float coefficient)
    {
        if (offsets.Length != 4 || offsets.Any(x => x <= 0)) throw new ArgumentException("Four positive delays are required.", nameof(offsets));
        _offsets = (int[])offsets.Clone();
        _delay = new SpatialDelayLine4(offsets.Max() + 1);
        _coefficient = coefficient;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpatialFrame4 Process(in SpatialFrame4 input, int position, float x, float y)
    {
        float a = _delay.Read(unchecked(position - _offsets[0]), 0) - _coefficient * input.A;
        float b = _delay.Read(unchecked(position - _offsets[1]), 1) - _coefficient * input.B;
        float c = _delay.Read(unchecked(position - _offsets[2]), 2) - _coefficient * input.C;
        float d = _delay.Read(unchecked(position - _offsets[3]), 3) - _coefficient * input.D;
        var feedback = new SpatialFrame4(input.A + _coefficient * a, input.B + _coefficient * b,
            input.C + _coefficient * c, input.D + _coefficient * d);
        _delay.Write(position, SpatialFrame4.Scatter(feedback, x, y));
        return new SpatialFrame4(a, b, c, d);
    }
    internal void Reset() => _delay.Reset();
}

internal static class SpatialCubicDelay
{
    private static readonly float[] Table = CreateTable();
    private static float[] CreateTable()
    {
        var table = new float[513];
        for (int i = 0; i <= 256; i++)
        {
            double mu = i / 256.0, mu2 = mu * mu, mu3 = mu2 * mu;
            table[i] = (float)(1.5 * mu3 + -2.5 * mu2 + 1);
            table[256 + i] = (float)(-.5 * mu3 + mu2 + -.5 * mu);
        }
        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Read(SpatialDelayLine4 line, int position, int channel, float modulationSamples)
    {
        int fraction = (int)(modulationSamples * 256);
        int delay = unchecked(position - (fraction >> 8)), offset = fraction & 255;
        return line.Read(delay, channel) * Table[256 + offset]
            + line.Read(unchecked(delay - 1), channel) * Table[offset]
            + line.Read(unchecked(delay - 2), channel) * Table[256 - offset]
            + line.Read(unchecked(delay - 3), channel) * Table[512 - offset];
    }
}
