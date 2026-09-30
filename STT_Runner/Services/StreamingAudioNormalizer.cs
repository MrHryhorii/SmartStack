namespace STT_Runner.Services;

/// <summary>
/// Applies gated RMS gain control in place. No lookahead, whole-file buffering,
/// or samples are added; gain state belongs to one decoded stream.
/// </summary>
internal sealed class StreamingAudioNormalizer
{
    private readonly AudioNormalizationSettings _settings;
    private readonly double _target;
    private readonly double _noiseFloor;
    private readonly double _minimumGain;
    private readonly double _maximumGain;
    private readonly float _peakLimit;
    private double _power;
    private double _gain = 1;
    private bool _hasSpeechLevel;

    public StreamingAudioNormalizer(AudioNormalizationSettings settings)
    {
        _settings = settings;
        _target = Math.Pow(10, settings.TargetRmsDbfs / 20);
        _noiseFloor = Math.Pow(10, settings.NoiseFloorDbfs / 20);
        _minimumGain = Math.Pow(10, settings.MinGainDb / 20);
        _maximumGain = Math.Pow(10, settings.MaxGainDb / 20);
        _peakLimit = (float)Math.Pow(10, settings.PeakDbfs / 20);
    }

    public void Process(Span<float> samples)
    {
        if (samples.IsEmpty) return;
        double power = 0;
        float peak = 0;
        for (int index = 0; index < samples.Length; index++)
        {
            float value = float.IsFinite(samples[index]) ? samples[index] : 0;
            samples[index] = value;
            power += (double)value * value;
            peak = Math.Max(peak, Math.Abs(value));
        }
        if (!_settings.Enabled) return;
        power /= samples.Length;
        bool aboveFloor = power >= _noiseFloor * _noiseFloor;
        double desired = 1;
        if (aboveFloor)
        {
            double weight = Math.Exp(-samples.Length / (16d * _settings.RmsWindowMs));
            _power = _hasSpeechLevel ? weight * _power + (1 - weight) * power : power;
            desired = Math.Clamp(_target / Math.Sqrt(_power), _minimumGain, _maximumGain);
        }
        // Frame peaks constrain the requested gain before the final sample limiter.
        if (peak > 0) desired = Math.Min(desired, _peakLimit / peak);
        int timeMs = desired < _gain ? _settings.AttackMs : _settings.ReleaseMs;
        double response = 1 - Math.Exp(-samples.Length / (16d * timeMs));
        double nextGain = aboveFloor && !_hasSpeechLevel ? desired : _gain + response * (desired - _gain);
        double initialGain = aboveFloor ? _gain : Math.Min(1, _gain);
        double finalGain = aboveFloor ? nextGain : Math.Min(1, nextGain);
        double step = (finalGain - initialGain) / samples.Length;
        for (int index = 0; index < samples.Length; index++)
        {
            float value = (float)(samples[index] * (initialGain + step * (index + 1)));
            samples[index] = Math.Clamp(value, -_peakLimit, _peakLimit);
        }
        _gain = nextGain;
        _hasSpeechLevel |= aboveFloor;
    }
}
