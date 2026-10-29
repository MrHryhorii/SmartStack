namespace ONNX_Runner.Services;

/// <summary>
/// Request-local linear interpolation with causal power matching on a fixed audio-time clock.
/// Endpoint powers define the target; the clean AC reference gates adaptation during silence.
/// </summary>
public sealed class StreamingPowerMatchedMixer(int sampleRate)
{
    public const double MaximumGain = 16;
    private readonly WindowedPowerMatcher _level = new WindowedPowerMatcher(sampleRate, warmupMs: 20, transitionMs: 10, maximumRiseDbPerSecond: 120,
            maximumGain: MaximumGain, minimumPower: 1e-8);
    private float _position = float.NaN;
    private double _dryWeight, _wetWeight;

    public double Gain => _level.Gain;
    public bool HasSamples => _level.HasSamples;

    public void Reset()
    {
        _position = float.NaN;
        _level.Reset();
    }

    /// <summary>Changes the interpolation position without advancing audio or retaining old makeup gain.</summary>
    public void Prepare(float position)
    {
        if (!float.IsFinite(position) || position < 0 || position > 1)
            throw new ArgumentOutOfRangeException(nameof(position));
        if (position == _position) return;
        _position = position;
        _dryWeight = 1 - (double)position;
        _wetWeight = position;
        _level.Reset();
    }

    /// <summary>Preserves endpoint PCM and duration; finite input samples may use floating-point headroom.</summary>
    public void Mix(ReadOnlySpan<float> dry, Span<float> wet, float position)
    {
        if (dry.Length != wet.Length) throw new ArgumentException("Signal lengths differ.");
        if (dry.Overlaps(wet, out int offset) && offset != 0)
            throw new ArgumentException("Signals partially overlap.", nameof(wet));
        Prepare(position);
        if (position == 0) { dry.CopyTo(wet); return; }
        if (position == 1) return;
        double a = _dryWeight, b = _wetWeight;
        for (int i = 0; i < wet.Length; i++)
        {
            float x = dry[i], y = wet[i];
            float mixed = (float)(a * x + b * y);
            double targetPower = a * (x * (double)x) + b * (y * (double)y);
            wet[i] = _level.ProcessPower(targetPower, x, mixed);
        }
    }
}
