// Derived from OpenAL Soft 1.23.1 alc/effects/reverb.cpp.
// Copyright (C) 2008-2017 Chris Robinson and Christopher Fitzgerald and OpenAL Soft contributors.
// C# adaptation for Tsubaki TTS, 2026-10-31.
// SPDX-License-Identifier: GPL-3.0-or-later
// This adapted copy uses GPL-3.0-or-later under LGPL-2.0 section 3; see LICENSE.
// Modified 2026-10-31: integrated into the Tsubaki spatial service namespace.
using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services.Spatial;

/// <summary>
/// Request-local mono reverb primitives: early reflections and a four-line modulated FDN.
/// Accepts one fixed numeric configuration and emits only the reflected signal.
/// The caller owns profiles, direct-signal mixing, intensity, output gain and peak protection.
/// All state follows audio samples across arbitrary input partitions; Reset starts a fresh stream.
/// </summary>
public sealed class SpatialReverbKernel
{
    private static readonly float[] EarlyTaps = [0, 2.0213520e-4f, 4.2531060e-4f, 6.7171600e-4f];
    private static readonly float[] EarlyAp = [9.7096800e-5f, 1.0720356e-4f, 1.1836234e-4f, 1.3068260e-4f];
    private static readonly float[] EarlyLines = [5.9850400e-4f, 1.0913150e-3f, 1.5376658e-3f, 1.9419362e-3f];
    private static readonly float[] LateAp = [1.6182800e-4f, 2.0389060e-4f, 2.8159360e-4f, 3.2365600e-4f];
    private static readonly float[] LateLines = [1.9419362e-3f, 2.4466860e-3f, 3.3791220e-3f, 3.8838720e-3f];
    private const uint ModulationCycle = 1u << 24;
    private static readonly float InverseSqrt2 = (float)(1 / Math.Sqrt(2));
    private readonly SpatialDelayLine4 _earlyInput, _earlySecondary, _lateInput, _lateFeedback;
    private readonly SpatialAllPass4 _earlyAllPass, _lateAllPass;
    private SpatialBiquad _inputHighShelf, _inputLowShelf;
    private readonly SpatialBiquad[] _decayHighShelf = new SpatialBiquad[4], _decayLowShelf = new SpatialBiquad[4];
    private readonly int[] _earlyTaps = new int[4], _earlyDelays = new int[4], _lateTaps = new int[4], _lateDelays = new int[4];
    private readonly float[] _earlyTapGains = new float[4], _earlyGains = new float[4], _lateMidGains = new float[4];
    private readonly float _mixX, _mixY, _densityGain, _earlyOutputGain, _lateOutputGain, _modulationDepth;
    private readonly uint _modulationStep;
    private uint _modulationIndex;
    private int _position;

    public SpatialReverbKernel(int sampleRate, in SpatialReverbParameters parameters)
    {
        if (sampleRate < 8000 || sampleRate > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        parameters.Validate();
        float frequency = sampleRate;
        float density = MathF.Max(5, MathF.Cbrt(parameters.Density * 125000));
        float diffusion = parameters.Diffusion;
        float angle = diffusion * MathF.Atan(MathF.Sqrt(3));
        _mixX = MathF.Cos(angle); _mixY = MathF.Sin(angle) / MathF.Sqrt(3);
        float allPassGain = diffusion * diffusion * InverseSqrt2;
        float hfRatio = parameters.DecayHFRatio;
        if (parameters.DecayHFLimit && parameters.AirAbsorptionGainHF < 1)
        {
            float decayLength = MathF.Log10(parameters.AirAbsorptionGainHF) * parameters.DecaySeconds / -3;
            hfRatio = MathF.Min(1 / 343.3f / decayLength, hfRatio);
        }
        float lfTime = Math.Clamp(parameters.DecaySeconds * parameters.DecayLFRatio, .1f, 20);
        float hfTime = Math.Clamp(parameters.DecaySeconds * hfRatio, .1f, 20);
        float lf = MathF.Min(parameters.LFReferenceHz / frequency, .49f);
        float hf = MathF.Min(parameters.HFReferenceHz / frequency, .49f);
        _inputHighShelf = SpatialBiquad.Shelf(true, hf, parameters.GainHF);
        _inputLowShelf = SpatialBiquad.Shelf(false, lf, parameters.GainLF);
        _modulationStep = Math.Max((uint)MathF.Round(ModulationCycle / (frequency * parameters.ModulationSeconds)), 1u);
        _modulationDepth = .05f / 4 * MathF.Min(parameters.ModulationSeconds, .25f) * parameters.ModulationDepth * frequency;
        int[] earlyApOffsets = new int[4], lateApOffsets = new int[4];
        float apAverage = ((LateAp[0] + LateAp[1]) + LateAp[2] + LateAp[3]) / 4;
        float lineAverage = ((LateLines[0] + LateLines[1]) + LateLines[2] + LateLines[3]) / 4;
        float weightedTime = lf * (frequency / 20000) * lfTime
            + (hf - lf) * (frequency / 20000) * parameters.DecaySeconds
            + (1 - hf * (frequency / 20000)) * hfTime;
        float averageDecay = Decay((lineAverage + apAverage) * density, weightedTime);
        _densityGain = MathF.Sqrt(1 - averageDecay * averageDecay);
        for (int j = 0; j < 4; j++)
        {
            float length = EarlyTaps[j] * density;
            _earlyTaps[j] = (int)((parameters.ReflectionsDelaySeconds + length) * frequency);
            _earlyTapGains[j] = Decay(length, parameters.DecaySeconds);
            _earlyDelays[j] = (int)(EarlyLines[j] * density * frequency);
            _earlyGains[j] = Decay(EarlyLines[j] * density, parameters.DecaySeconds);
            earlyApOffsets[j] = (int)(EarlyAp[j] * density * frequency);
            lateApOffsets[j] = (int)(LateAp[j] * density * frequency);
            _lateTaps[j] = (int)(((LateLines[j] - LateLines[0]) / 4 * density + parameters.LateReverbDelaySeconds) * frequency);
            length = LateLines[j] * density;
            _lateDelays[j] = Math.Max((int)(length * frequency + .5f), 1) - 1;
            length += (LateAp[j] + (apAverage - LateAp[j]) * diffusion) * density + _modulationDepth / frequency;
            float midGain = Decay(length, parameters.DecaySeconds);
            _lateMidGains[j] = midGain;
            _decayLowShelf[j] = SpatialBiquad.Shelf(false, lf, Decay(length, lfTime) / midGain);
            _decayHighShelf[j] = SpatialBiquad.Shelf(true, hf, Decay(length, hfTime) / midGain);
        }
        _earlyInput = new SpatialDelayLine4(_earlyTaps.Max() + 1);
        _earlySecondary = new SpatialDelayLine4(_earlyDelays.Max() + 1);
        _lateInput = new SpatialDelayLine4(_lateTaps.Max() + 1);
        _lateFeedback = new SpatialDelayLine4(_lateDelays.Max() + (int)MathF.Ceiling(2 * _modulationDepth) + 5);
        _earlyAllPass = new SpatialAllPass4(earlyApOffsets, allPassGain);
        _lateAllPass = new SpatialAllPass4(lateApOffsets, allPassGain);
        _earlyOutputGain = .5f * (parameters.ReflectionsGain * parameters.Gain);
        _lateOutputGain = .5f * (parameters.LateReverbGain * parameters.Gain);
    }

    /// <summary>Processes finite mono float PCM in place or into a separate destination, preserving duration.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> reflected)
    {
        if (input.Length != reflected.Length) throw new ArgumentException("Signal lengths differ.");
        if (input.Overlaps(reflected, out int offset) && offset != 0)
            throw new ArgumentException("Signals partially overlap.", nameof(reflected));
        for (int i = 0; i < input.Length; i++) reflected[i] = Process(input[i]);
    }

    /// <summary>Processes one finite sample. Floating-point headroom is preserved; no clipping or makeup gain is applied.</summary>
    public float Process(float input)
    {
        int n = _position;
        // Mono W encodes the same value into all four internal channels.
        float filtered = _inputLowShelf.Process(_inputHighShelf.Process(input * .5f));
        _earlyInput.Write(n, new SpatialFrame4(filtered, filtered, filtered, filtered));
        var early = new SpatialFrame4(
            _earlyInput.Read(unchecked(n - _earlyTaps[0]), 0) * _earlyTapGains[0],
            _earlyInput.Read(unchecked(n - _earlyTaps[1]), 1) * _earlyTapGains[1],
            _earlyInput.Read(unchecked(n - _earlyTaps[2]), 2) * _earlyTapGains[2],
            _earlyInput.Read(unchecked(n - _earlyTaps[3]), 3) * _earlyTapGains[3]);
        early = _earlyAllPass.Process(early, n, _mixX, _mixY);
        _earlySecondary.Write(n, early.Reversed);
        early = new SpatialFrame4(
            early.A + _earlySecondary.Read(unchecked(n - _earlyDelays[0]), 0) * _earlyGains[0],
            early.B + _earlySecondary.Read(unchecked(n - _earlyDelays[1]), 1) * _earlyGains[1],
            early.C + _earlySecondary.Read(unchecked(n - _earlyDelays[2]), 2) * _earlyGains[2],
            early.D + _earlySecondary.Read(unchecked(n - _earlyDelays[3]), 3) * _earlyGains[3]);
        _lateInput.Write(n, SpatialFrame4.Scatter(early.Reversed, _mixX, _mixY));
        _modulationIndex = unchecked(_modulationIndex + _modulationStep);
        float modulation = _modulationDepth == 0 ? 0
            : (MathF.Sin((_modulationIndex & (ModulationCycle - 1)) * (MathF.PI * 2 / ModulationCycle)) + 1) * _modulationDepth;
        var late = new SpatialFrame4(LateInput(n, 0, modulation), LateInput(n, 1, modulation),
            LateInput(n, 2, modulation), LateInput(n, 3, modulation));
        late = _lateAllPass.Process(late, n, _mixX, _mixY);
        _lateFeedback.Write(n, SpatialFrame4.Scatter(late.Reversed, _mixX, _mixY));
        _position = unchecked(n + 1);
        float output = 0;
        if (MathF.Abs(_earlyOutputGain) > .00001f)
        {
            output += early.A * _earlyOutputGain; output += early.B * _earlyOutputGain;
            output += early.C * _earlyOutputGain; output += early.D * _earlyOutputGain;
        }
        if (MathF.Abs(_lateOutputGain) > .00001f)
        {
            output += late.A * _lateOutputGain; output += late.B * _lateOutputGain;
            output += late.C * _lateOutputGain; output += late.D * _lateOutputGain;
        }
        return output;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float LateInput(int position, int channel, float modulation)
    {
        float feedback = SpatialCubicDelay.Read(_lateFeedback, unchecked(position - _lateDelays[channel]), channel, modulation);
        float input = feedback * _lateMidGains[channel]
            + _lateInput.Read(unchecked(position - _lateTaps[channel]), channel) * (_densityGain * _lateMidGains[channel]);
        return _decayLowShelf[channel].Process(_decayHighShelf[channel].Process(input));
    }

    public void Reset()
    {
        _position = 0; _modulationIndex = 0;
        _earlyInput.Reset(); _earlySecondary.Reset(); _lateInput.Reset(); _lateFeedback.Reset();
        _earlyAllPass.Reset(); _lateAllPass.Reset();
        _inputHighShelf.Reset(); _inputLowShelf.Reset();
        for (int j = 0; j < 4; j++)
        {
            _decayHighShelf[j].Reset(); _decayLowShelf[j].Reset();
        }
    }

    private static float Decay(float seconds, float decaySeconds) => MathF.Pow(.001f, seconds / decaySeconds);
}
