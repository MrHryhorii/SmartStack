using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services;

/// <summary>Slow cassette voice-level correction; additive hiss never enters its detector.</summary>
internal struct TapeVoiceNormalizer
{
    private readonly int _windowSamples;
    private readonly float _slew;
    private readonly double _powerSlew;
    private int _count;
    private double _referenceEnergy, _voiceEnergy;
    private double _referencePower, _voicePower;
    private double _powerRatio;
    private float _gain, _targetGain;

    internal readonly float Gain => _gain;

    internal TapeVoiceNormalizer(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _windowSamples = Math.Max(1, (int)Math.Round(sampleRate * .020));
        _slew = 1f - MathF.Exp(-1f / (.100f * sampleRate));
        _powerSlew = 1 - Math.Exp(-_windowSamples / (.250 * sampleRate));
        _gain = _targetGain = 1;
        _powerRatio = 1;
    }

    internal void SetAmount(float amount)
    {
        const double fullPowerRatio = 1.2589254117941673; // +1 dB at full intensity.
        _powerRatio = 1 + amount * (fullPowerRatio - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal float Process(float voice, float reference)
    {
        _referenceEnergy += reference * (double)reference * _powerRatio;
        _voiceEnergy += voice * (double)voice;
        if (++_count == _windowSamples) UpdateTarget();
        _gain += _slew * (_targetGain - _gain);
        return voice * _gain;
    }

    // Keep the 50 Hz detector update outside the per-sample inline path.
    private void UpdateTarget()
    {
        _referencePower += _powerSlew * (_referenceEnergy / _count - _referencePower);
        _voicePower += _powerSlew * (_voiceEnergy / _count - _voicePower);
        // Fade correction continuously near inaudible voice or a strongly rejected band.
        // The floor affects detector confidence only; it never gates audio or measures hiss.
        double minimumPower = Math.Min(_referencePower, _voicePower);
        double confidence = minimumPower / (minimumPower + 1e-7);
        double gain = PowerMatcher.CalculateGain(_referencePower, _voicePower).Gain;
        _targetGain = (float)(1 + confidence * (Math.Clamp(gain, .25, 4) - 1));
        _count = 0;
        _referenceEnergy = _voiceEnergy = 0;
    }
}
