using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services;

/// <summary>Streaming peak protection with 5 ms lookahead and a smoothly varying gain.</summary>
internal sealed class PeakLimiter
{
    internal const float Ceiling = .95f;

    private readonly float _volume;
    private readonly double _release;
    private readonly float[] _audio;
    private readonly float[] _peaks;
    private readonly long[] _peakIndices;
    private readonly double[] _attackFirst;
    private readonly double[] _attackSecond;
    private double _firstSum;
    private double _secondSum;
    private double _gain = 1;
    private int _audioPosition;
    private int _peakHead;
    private int _peakCount;
    private int _firstPosition;
    private int _secondPosition;
    private int _priming;
    private int _pending;
    private long _index;
    private bool _flushing;

    internal int LatencySamples { get; }

    internal PeakLimiter(int sampleRate, float volume)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (!float.IsFinite(volume)) throw new ArgumentOutOfRangeException(nameof(volume));

        int attackLength = Math.Max(2, (int)Math.Round(sampleRate * .0025) + 1);
        LatencySamples = 2 * (attackLength - 1);
        _volume = volume;
        _release = 1 - Math.Exp(-1 / (sampleRate * .08));
        _audio = new float[LatencySamples + 1];
        _peaks = new float[_audio.Length];
        _peakIndices = new long[_audio.Length];
        _attackFirst = new double[attackLength];
        _attackSecond = new double[attackLength];
        Array.Fill(_attackFirst, 1);
        Array.Fill(_attackSecond, 1);
        _firstSum = _secondSum = attackLength;
        _priming = LatencySamples;
    }

    /// <summary>Compacts ready output in place; priming samples are retained rather than emitted as silence.</summary>
    internal int Process(Span<float> samples)
    {
        if (_flushing) throw new InvalidOperationException("The peak limiter is already being flushed.");

        int written = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i] * _volume;
            _pending++;
            if (!Advance(sample, out float output)) continue;

            samples[written++] = output;
            _pending--;
        }
        return written;
    }

    /// <summary>Emits only retained input samples, preserving the exact input duration and final consonants.</summary>
    internal int Flush(Span<float> destination)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Advance(float sample, out float output)
    {
        _audio[_audioPosition] = sample;
        double peak = Peak(MathF.Abs(sample));
        double target = peak > Ceiling ? Ceiling / peak : 1;
        _gain = target < _gain ? target : _gain + _release * (target - _gain);

        // The detector holds each peak across the entire smoothing support. Every gain
        // averaged for a delayed sample is safe for that sample, so smoothing cannot overshoot.
        double first = Average(_gain, _attackFirst, ref _firstPosition, ref _firstSum);
        double gain = Average(first, _attackSecond, ref _secondPosition, ref _secondSum);
        int delayed = _audioPosition + 1;
        if (delayed == _audio.Length) delayed = 0;
        output = (float)(_audio[delayed] * gain);
        _audioPosition = delayed;
        _index++;

        if (_priming == 0) return true;
        _priming--;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Peak(float sample)
    {
        if (_peakCount > 0 && _peakIndices[_peakHead] < _index - LatencySamples)
        {
            if (++_peakHead == _peaks.Length) _peakHead = 0;
            _peakCount--;
        }
        while (_peakCount > 0)
        {
            int last = _peakHead + _peakCount - 1;
            if (last >= _peaks.Length) last -= _peaks.Length;
            if (_peaks[last] > sample) break;
            _peakCount--;
        }
        int position = _peakHead + _peakCount;
        if (position >= _peaks.Length) position -= _peaks.Length;
        _peaks[position] = sample;
        _peakIndices[position] = _index;
        _peakCount++;
        return _peaks[_peakHead];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Average(double value, double[] history, ref int position, ref double sum)
    {
        sum += value - history[position];
        history[position] = value;
        if (++position == history.Length) position = 0;
        return sum / history.Length;
    }
}
