// Ring-buffer delay adapted from OpenAL Soft 1.23.1 alc/effects/echo.cpp.
// Copyright (C) 2009 by Chris Robinson.
// C# adaptation for Tsubaki TTS, 2026-10-31.
// SPDX-License-Identifier: GPL-3.0-or-later
// This adapted copy uses GPL-3.0-or-later under LGPL-2.0 section 3; see LICENSE.
// Modified 2026-10-31: integrated into the Tsubaki spatial service namespace.
namespace ONNX_Runner.Services.Spatial;

/// <summary>
/// One fixed integer-sample feed-forward delay. Time conversion, tap gains,
/// feedback, direct-signal mixing and all profile logic belong to the caller.
/// Zero delay is exact bypass; zero input drains the retained samples.
/// </summary>
public sealed class SpatialTapDelay
{
    private readonly float[] _samples;
    private readonly int _mask;
    private int _position;
    public int DelaySamples { get; }

    public SpatialTapDelay(int delaySamples)
    {
        if (delaySamples < 0 || delaySamples > 192000 * 4) throw new ArgumentOutOfRangeException(nameof(delaySamples));
        DelaySamples = delaySamples;
        if (delaySamples == 0) { _samples = []; return; }
        int capacity = 1;
        while (capacity <= delaySamples) capacity <<= 1;
        _samples = new float[capacity]; _mask = capacity - 1;
    }

    public float Process(float input)
    {
        if (DelaySamples == 0) return input;
        float output = Read();
        Write(input);
        return output;
    }

    /// <summary>Reads the return for the next sample without advancing time; requires a positive delay.</summary>
    public float Read()
    {
        if (DelaySamples == 0) throw new InvalidOperationException("A zero-delay path has no retained return.");
        return _samples[unchecked(_position - DelaySamples) & _mask];
    }

    /// <summary>Stores one sample and advances time. Allows the caller to apply causal feedback after Read.</summary>
    public void Write(float input)
    {
        if (DelaySamples == 0) throw new InvalidOperationException("Use Process for zero-delay bypass.");
        _samples[_position & _mask] = input;
        _position = unchecked(_position + 1);
    }
    public void Process(ReadOnlySpan<float> input, Span<float> delayed)
    {
        SpatialBuffer.Validate(input, delayed);
        for (int i = 0; i < input.Length; i++) delayed[i] = Process(input[i]);
    }
    public void Reset() { Array.Clear(_samples); _position = 0; }
}
