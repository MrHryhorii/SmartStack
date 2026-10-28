using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services;

/// <summary>
/// Causal power matching over a fixed audio-time window. Buffer boundaries do not reset analysis
/// or gain transitions. The caller supplies the clean reference and the processed signal.
/// </summary>
public sealed class WindowedPowerMatcher
{
    private readonly record struct Frame(double Reference, double Processed, bool Active);
    private readonly Frame[] _frames;
    private readonly int _hopSamples;
    private readonly int _warmupFrames;
    private readonly int _transitionSamples;
    private readonly double _maximumGain;
    private readonly double _maximumRiseFactor;
    private readonly double _minimumPower;
    private int _position, _filledFrames, _activeFrames, _pendingSamples, _rampRemaining;
    private double _referenceSum, _processedSum, _pendingReference, _pendingProcessed, _pendingReferenceMean;
    private double _gain, _targetGain, _gainStep;

    public double Gain => _gain;
    public bool HasSamples => _filledFrames != 0 || _pendingSamples != 0;

    public WindowedPowerMatcher(int sampleRate, double initialGain = 1, float windowMs = 200,
        float updateMs = 10, float warmupMs = 70, float transitionMs = 30,
        double maximumGain = 4, double minimumPower = 1e-12, double maximumRiseDbPerSecond = 12)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (!double.IsFinite(maximumGain) || maximumGain < 1) throw new ArgumentOutOfRangeException(nameof(maximumGain));
        if (!double.IsFinite(minimumPower) || minimumPower <= 0) throw new ArgumentOutOfRangeException(nameof(minimumPower));
        if (!double.IsFinite(maximumRiseDbPerSecond) || maximumRiseDbPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRiseDbPerSecond));
        int windowSamples = Samples(windowMs, sampleRate, nameof(windowMs));
        _hopSamples = Samples(updateMs, sampleRate, nameof(updateMs));
        int warmupSamples = Samples(warmupMs, sampleRate, nameof(warmupMs));
        if (_hopSamples > windowSamples) throw new ArgumentOutOfRangeException(nameof(updateMs));
        if (warmupSamples > windowSamples) throw new ArgumentOutOfRangeException(nameof(warmupMs));
        _frames = new Frame[(int)Math.Ceiling(windowSamples / (double)_hopSamples)];
        _warmupFrames = (int)Math.Ceiling(warmupSamples / (double)_hopSamples);
        _transitionSamples = Samples(transitionMs, sampleRate, nameof(transitionMs));
        _maximumRiseFactor = 1 + maximumRiseDbPerSecond * (Math.Log(10) / 20) * _transitionSamples / sampleRate;
        _maximumGain = maximumGain;
        _minimumPower = minimumPower;
        Reset(initialGain);
    }

    public void Reset(double initialGain = 1)
    {
        if (!double.IsFinite(initialGain) || initialGain < 0 || initialGain > _maximumGain)
            throw new ArgumentOutOfRangeException(nameof(initialGain));
        Array.Clear(_frames);
        _position = _filledFrames = _activeFrames = _pendingSamples = _rampRemaining = 0;
        _referenceSum = _processedSum = _pendingReference = _pendingProcessed = _pendingReferenceMean = _gainStep = 0;
        _gain = _targetGain = initialGain;
    }

    /// <summary>Processes finite PCM without allocating, delaying samples or clipping floating-point headroom.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float reference, float processed)
    {
        _pendingReference += reference * (double)reference;
        _pendingReferenceMean += reference;
        _pendingProcessed += processed * (double)processed;
        if (++_pendingSamples == _hopSamples) AdvanceWindow();
        float output = (float)(processed * _gain);
        if (_rampRemaining > 0)
        {
            _gain += _gainStep;
            if (--_rampRemaining == 0) _gain = _targetGain;
        }
        return output;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AdvanceWindow()
    {
        Frame previous = _frames[_position];
        _referenceSum = Math.Max(0, _referenceSum - previous.Reference) + _pendingReference;
        _processedSum = Math.Max(0, _processedSum - previous.Processed) + _pendingProcessed;
        // A constant reference offset is not usable signal for makeup-gain adaptation.
        double varianceEnergy = Math.Max(0, _pendingReference - _pendingReferenceMean * _pendingReferenceMean / _hopSamples);
        bool active = varianceEnergy > _minimumPower * _hopSamples;
        _activeFrames += (active ? 1 : 0) - (previous.Active ? 1 : 0);
        _frames[_position] = new(_pendingReference, _pendingProcessed, active);
        if (++_position == _frames.Length) _position = 0;
        _filledFrames = Math.Min(_filledFrames + 1, _frames.Length);
        _pendingReference = _pendingProcessed = _pendingReferenceMean = 0;
        _pendingSamples = 0;
        if (_activeFrames < _warmupFrames) return;
        double count = _filledFrames * (double)_hopSamples;
        double referencePower = _referenceSum / count, processedPower = _processedSum / count;
        if (!(referencePower > _minimumPower) || !(processedPower > _minimumPower)) return;
        double targetGain = Math.Min(_maximumGain, PowerMatcher.CalculateGain(referencePower, processedPower).Gain);
        // Quiet lead-ins must not establish a large makeup gain immediately before speech starts.
        if (_gain > 0) targetGain = Math.Min(targetGain, _gain * _maximumRiseFactor);
        if (targetGain == _targetGain) return;
        _targetGain = targetGain;
        _gainStep = (_targetGain - _gain) / _transitionSamples;
        _rampRemaining = _transitionSamples;
    }

    private static int Samples(float milliseconds, int rate, string name)
    {
        if (!float.IsFinite(milliseconds) || milliseconds <= 0) throw new ArgumentOutOfRangeException(name);
        double samples = Math.Round(rate * (double)milliseconds / 1000);
        if (samples > int.MaxValue) throw new ArgumentOutOfRangeException(name);
        return Math.Max(1, (int)samples);
    }
}
