using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services.Audio.Levels;

/// <summary>Request-local harmony correction with continuous clean-reference confidence and no audio lookahead.</summary>
internal sealed class HarmonizerLevelMatcher
{
    private const double MinimumPower = 1e-12;
    private const double HalfReferencePower = 1e-8;
    private const double MaximumGain = 4;
    private readonly int _hopSamples, _riseSamples, _fallSamples;
    private readonly double _inverseHop, _attack, _release, _confidenceAttack, _confidenceRelease, _maximumRiseFactor;
    private int _pendingSamples, _rampRemaining;
    private double _referencePower, _processedPower, _referenceSum, _processedSum, _referenceMean;
    private double _activityConfidence;
    private double _nominalGain = 1, _gain = 1, _targetGain = 1, _gainStep;

    internal double Gain => _gain;

    internal HarmonizerLevelMatcher(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _hopSamples = Math.Max(1, (int)Math.Round(sampleRate * .002));
        _riseSamples = Math.Max(1, (int)Math.Round(sampleRate * .006));
        _fallSamples = Math.Max(1, (int)Math.Round(sampleRate * .002));
        _inverseHop = 1d / _hopSamples;
        _attack = 1 - Math.Exp(-_hopSamples / (sampleRate * .002));
        _release = 1 - Math.Exp(-_hopSamples / (sampleRate * .008));
        _confidenceAttack = 1 - Math.Exp(-_hopSamples / (double)_riseSamples);
        _confidenceRelease = 1 - Math.Exp(-_hopSamples / (double)_fallSamples);
        _maximumRiseFactor = 1 + 480 * (Math.Log(10) / 20) * _riseSamples / sampleRate;
    }

    /// <summary>Prepares the independent-voice power baseline without measuring or blending PCM.</summary>
    internal void Prepare(double directWeight, double harmonyWeight)
    {
        double power = directWeight * directWeight + 2 * harmonyWeight * harmonyWeight;
        if (!double.IsFinite(power) || power <= 0) throw new ArgumentOutOfRangeException(nameof(directWeight));
        _nominalGain = Math.Clamp(1 / Math.Sqrt(power), 1, MaximumGain);
        if (_pendingSamples != 0 || _referencePower != 0 || _processedPower != 0) return;
        _gain = _targetGain = _nominalGain;
    }

    /// <summary>Applies only a bounded scalar; it neither mixes clean audio nor clips floating-point headroom.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal float Process(float reference, float processed)
    {
        _referenceSum += reference * (double)reference;
        _processedSum += processed * (double)processed;
        _referenceMean += reference;
        if (++_pendingSamples == _hopSamples) Advance();
        float output = (float)(processed * _gain);
        if (_rampRemaining > 0)
        {
            _gain += _gainStep;
            if (--_rampRemaining == 0) _gain = _targetGain;
        }
        return output;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Advance()
    {
        double reference = _referenceSum * _inverseHop, processed = _processedSum * _inverseHop;
        double mean = _referenceMean * _inverseHop;
        double activity = Math.Max(0, reference - mean * mean);
        // Fast power attack removes stale boost when a cancellation ends; release follows the dip.
        _referencePower += (reference - _referencePower) * (reference > _referencePower ? _attack : _release);
        _processedPower += (processed - _processedPower) * (processed > _processedPower ? _attack : _release);
        _referenceSum = _processedSum = _referenceMean = 0;
        _pendingSamples = 0;
        // AC confidence grows and decays continuously instead of counting active hops.
        // A single DC edge loses confidence as soon as its constant samples follow.
        double confidence = activity / (activity + MinimumPower);
        _activityConfidence += (confidence - _activityConfidence) *
            (confidence > _activityConfidence ? _confidenceAttack : _confidenceRelease);

        // Half confidence at -80 dBFS RMS; no reference-level threshold switches correction on.
        // The processed voice cannot classify a cancellation as original silence.
        double squaredReference = reference * reference;
        double levelWeight = squaredReference / (squaredReference + HalfReferencePower * HalfReferencePower);
        double recoverableWeight = processed / (processed + MinimumPower);
        double weight = levelWeight * _activityConfidence * recoverableWeight;
        if (weight == 0)
        {
            SetTarget(_nominalGain);
            return;
        }
        // Correct cancellation above the nominal baseline. A lingering harmony must not turn
        // a quiet reference into severe attenuation carried into the next speech onset.
        double measured = Math.Clamp(Math.Sqrt(_referencePower / Math.Max(_processedPower, MinimumPower)), _nominalGain, MaximumGain);
        double target = _nominalGain + weight * (measured - _nominalGain);
        target = Math.Min(target, _gain * _maximumRiseFactor);
        SetTarget(target);
    }

    private void SetTarget(double target)
    {
        if (target == _targetGain) return;
        _targetGain = target;
        _rampRemaining = target > _gain ? _riseSamples : _fallSamples;
        _gainStep = (_targetGain - _gain) / _rampRemaining;
    }
}
