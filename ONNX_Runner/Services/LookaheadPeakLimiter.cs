namespace ONNX_Runner.Services;

/// <summary>
/// Request-local sample-peak limiter with delayed audio and a smoothed envelope.
/// Hämäläinen, DAFx-02: clipping control, running maximum, and moving-average envelope.
/// The clipping bound propagates the preceding detector state through the complete smoothing support.
/// https://www.dafx.de/paper-archive/2002/DAFX02_Hamalainen_smoothing_peak_limiters.pdf
/// </summary>
public sealed class LookaheadPeakLimiter
{
    private readonly double _ceiling, _attack, _release, _beta, _inverseOneMinusBeta;
    private readonly float[] _audio;
    private readonly double[] _envelopes;
    private SlidingMaximum _maximum;
    private double _envelope, _average, _sum;
    private int _audioPosition, _envelopePosition, _priming, _pending;
    private long _index;
    private bool _flushing;

    public int LatencySamples { get; }
    public double MinimumGain { get; private set; } = 1;

    public LookaheadPeakLimiter(int sampleRate, float ceiling = .95f,
        double lookaheadSeconds = .005, double releaseSeconds = .08)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (!float.IsFinite(ceiling) || ceiling <= 0 || ceiling > 1)
            throw new ArgumentOutOfRangeException(nameof(ceiling));
        if (!double.IsFinite(lookaheadSeconds) || lookaheadSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(lookaheadSeconds));
        if (!double.IsFinite(releaseSeconds) || releaseSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(releaseSeconds));

        _ceiling = ceiling;
        LatencySamples = Math.Max(2, (int)Math.Round(sampleRate * lookaheadSeconds));
        int smoothingLength = Math.Max(2, (LatencySamples + 1) / 2);
        // Bound the clipping-control overshoot to 1.01 using the slowest term in equation 20.
        const double maximumOvershoot = 1.01;
        double pole = Math.Exp(Math.Log((maximumOvershoot - 1) / maximumOvershoot)
            / (LatencySamples + 2 - smoothingLength));
        _attack = 1 - pole;
        _release = Math.Min(_attack, 1 - Math.Exp(-1 / (sampleRate * releaseSeconds)));
        double beta = 0;
        for (int i = 0; i < smoothingLength; i++) beta += Math.Pow(pole, LatencySamples + 1 - i);
        _beta = beta / smoothingLength;
        _inverseOneMinusBeta = 1 / (1 - _beta);
        _audio = new float[LatencySamples + 1];
        _envelopes = new double[smoothingLength];
        Array.Fill(_envelopes, _ceiling);
        _envelope = _average = _ceiling;
        _sum = smoothingLength * _ceiling;
        _maximum = new SlidingMaximum(LatencySamples);
        _priming = LatencySamples;
    }

    /// <summary>Compacts ready samples in place; retains lookahead across calls.</summary>
    public int Process(Span<float> samples)
    {
        if (_flushing) throw new InvalidOperationException("The limiter is already being flushed.");
        int written = 0;
        foreach (float sample in samples)
        {
            _pending++;
            if (!Advance(sample, out float output)) continue;
            samples[written++] = output;
            _pending--;
        }
        return written;
    }

    /// <summary>Emits retained input only, without extending the signal or dropping its ending.</summary>
    public int Flush(Span<float> destination)
    {
        _flushing = true;
        int written = 0;
        while (_pending > 0 && written < destination.Length)
        {
            if (!Advance(0, out float output)) continue;
            destination[written++] = output;
            _pending--;
        }
        return written;
    }

    private bool Advance(float sample, out float output)
    {
        _audio[_audioPosition] = sample;
        double magnitude = Math.Abs((double)sample);
        double control = Math.Max(_ceiling, Math.Max(magnitude,
            (magnitude - _beta * _envelope) * _inverseOneMinusBeta));
        double peak = _maximum.Push(control, _index++);
        double coefficient = peak > _envelope ? _attack : _release;
        _envelope += coefficient * (peak - _envelope);
        _sum += _envelope - _envelopes[_envelopePosition];
        _envelopes[_envelopePosition] = _envelope;
        if (++_envelopePosition == _envelopes.Length) _envelopePosition = 0;
        _average = _sum / _envelopes.Length;
        double gain = Math.Min(1, _ceiling / _average);
        MinimumGain = Math.Min(MinimumGain, gain);
        int delayed = _audioPosition + 1;
        if (delayed == _audio.Length) delayed = 0;
        output = (float)(_audio[delayed] * gain);
        _audioPosition = delayed;
        if (_priming == 0) return true;
        _priming--;
        return false;
    }

    private struct SlidingMaximum
    {
        private readonly double[] _values;
        private readonly long[] _indices;
        private readonly int _lookahead;
        private int _head, _count;

        internal SlidingMaximum(int lookahead)
        {
            _lookahead = lookahead;
            _values = new double[lookahead + 1];
            _indices = new long[_values.Length];
        }

        internal double Push(double value, long index)
        {
            if (_count > 0 && _indices[_head] < index - _lookahead)
            {
                if (++_head == _values.Length) _head = 0;
                _count--;
            }
            while (_count > 0)
            {
                int last = _head + _count - 1;
                if (last >= _values.Length) last -= _values.Length;
                if (_values[last] > value) break;
                _count--;
            }
            int position = _head + _count;
            if (position >= _values.Length) position -= _values.Length;
            _values[position] = value;
            _indices[position] = index;
            _count++;
            return _values[_head];
        }
    }
}
