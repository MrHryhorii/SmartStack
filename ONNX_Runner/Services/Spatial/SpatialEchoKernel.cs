// Derived from OpenAL Soft 1.23.1 alc/effects/echo.cpp.
// Copyright (C) 2009 by Chris Robinson.
// C# adaptation for Tsubaki TTS, 2026-10-31.
// SPDX-License-Identifier: GPL-3.0-or-later
// This adapted copy uses GPL-3.0-or-later under LGPL-2.0 section 3; see LICENSE.
// Modified 2026-10-31: integrated into the Tsubaki spatial service namespace.
namespace ONNX_Runner.Services.Spatial;

/// <summary>Two sequential echo taps; feedback is taken from the second tap and damped.</summary>
public readonly record struct SpatialEchoParameters(float DelaySeconds, float AdditionalDelaySeconds, float Damping, float Feedback);

/// <summary>
/// Original EFX two-tap echo, reduced to mono. Emits the sum of both returns.
/// The caller owns direct-signal mixing, profile gains, intensity and tail policy.
/// Mono decoding is independent of the original stereo spread setting.
/// Each configured delay may be 0-2 seconds; the original EFX range is a subset.
/// Feedback 1 can sustain a return indefinitely; the caller controls the maximum tail duration.
/// </summary>
public sealed class SpatialEchoKernel
{
    private readonly float[] _samples;
    private readonly int _mask, _firstTap, _secondTap;
    private readonly float _feedback;
    private SpatialBiquad _damping;
    private int _position;

    public SpatialEchoKernel(int sampleRate, in SpatialEchoParameters parameters)
    {
        if (sampleRate < 8000 || sampleRate > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        // Retain the original DSP while allowing sparse outdoor echoes beyond the EFX API limits.
        Range(parameters.DelaySeconds, 0, 2, nameof(parameters.DelaySeconds));
        Range(parameters.AdditionalDelaySeconds, 0, 2, nameof(parameters.AdditionalDelaySeconds));
        Range(parameters.Damping, 0, .99f, nameof(parameters.Damping));
        Range(parameters.Feedback, 0, 1, nameof(parameters.Feedback));
        _firstTap = Math.Max((int)(parameters.DelaySeconds * sampleRate + .5f), 1);
        _secondTap = (int)(parameters.AdditionalDelaySeconds * sampleRate + .5f) + _firstTap;
        int capacity = 1;
        while (capacity <= _secondTap) capacity <<= 1;
        _samples = new float[capacity]; _mask = capacity - 1;
        _feedback = parameters.Feedback;
        // Unlike the original echo, keep its 5 kHz damping reference below Nyquist at low rates.
        _damping = SpatialBiquad.Shelf(true, MathF.Min(5000f / sampleRate, .49f), MathF.Max(1 - parameters.Damping, .0625f));
    }

    public int FirstTapDelaySamples => _firstTap;
    public int SecondTapDelaySamples => _secondTap;

    public float Process(float input)
    {
        int position = _position, write = position & _mask;
        _samples[write] = input;
        float first = _samples[unchecked(position - _firstTap) & _mask];
        float second = _samples[unchecked(position - _secondTap) & _mask];
        _samples[write] += _damping.Process(second) * _feedback;
        _position = unchecked(position + 1);
        return first + second;
    }

    public void Process(ReadOnlySpan<float> input, Span<float> reflected)
    {
        SpatialBuffer.Validate(input, reflected);
        for (int i = 0; i < input.Length; i++) reflected[i] = Process(input[i]);
    }
    public void Reset() { Array.Clear(_samples); _damping.Reset(); _position = 0; }
    private static void Range(float value, float minimum, float maximum, string name)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum) throw new ArgumentOutOfRangeException(name);
    }
}
