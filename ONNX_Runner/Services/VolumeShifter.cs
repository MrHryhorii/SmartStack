using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services;

/// <summary>Request-local volume control with 5 ms lookahead peak protection and a complete-buffer helper.</summary>
public sealed class VolumeShifter
{
    public const float Ceiling = .95f;

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

    public int LatencySamples { get; }

    public VolumeShifter(int sampleRate, float volume)
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
    public int Process(Span<float> samples)
    {
        if (_flushing) throw new InvalidOperationException("The volume processor is already being flushed.");

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

    /// <summary>Applies the same gain and peak protection to a complete buffer without changing its duration.</summary>
    public static void ApplyVolume(Span<float> samples, float volume, int sampleRate)
    {
        if (samples.IsEmpty) return;

        var processor = new VolumeShifter(sampleRate, volume);
        int written = processor.Process(samples);
        while (written < samples.Length)
            written += processor.Flush(samples[written..]);
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

    /// <summary>Preserves existing complete-buffer reference preprocessing; request playback uses Process and Flush.</summary>
    public static void ApplyVolume(Span<float> buffer, float volume, bool forceLimiter = false)
    {
        if (!forceLimiter && MathF.Abs(volume - 1.0f) < 0.001f) return;
        // The threshold is where the soft-knee curve starts. Below this, the signal is unchanged.
        const float threshold = 0.8f;
        const float headroom = 1.0f - threshold;
        // For volume > 1.0, the effective threshold is reduced, making the curve more aggressive.
        for (int i = 0; i < buffer.Length; i++)
        {
            float x = buffer[i] * volume;
            float absX = MathF.Abs(x);

            if (absX <= threshold)
            {
                buffer[i] = x;
            }
            else
            {
                // Soft-knee curve: y = threshold + headroom * (excess / (1 + excess))
                float excess = (absX - threshold) / headroom;
                float softPeak = threshold + headroom * (excess / (1.0f + excess));
                buffer[i] = MathF.Sign(x) * softPeak;
            }
        }
    }
}
