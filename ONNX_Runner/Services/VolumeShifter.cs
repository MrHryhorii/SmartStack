namespace ONNX_Runner.Services;

/// <summary>Request-local output adapter composing independent linear gain and lookahead peak protection.</summary>
public sealed class VolumeShifter
{
    public const float Ceiling = .95f;

    private readonly float _volume;
    private readonly LookaheadPeakLimiter _limiter;
    private bool _flushing;

    public int LatencySamples => _limiter.LatencySamples;

    public VolumeShifter(int sampleRate, float volume)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (!float.IsFinite(volume)) throw new ArgumentOutOfRangeException(nameof(volume));

        _volume = volume;
        _limiter = new LookaheadPeakLimiter(sampleRate, Ceiling);
    }

    /// <summary>Compacts ready output in place; priming samples are retained rather than emitted as silence.</summary>
    public int Process(Span<float> samples)
    {
        if (_flushing) throw new InvalidOperationException("The volume processor is already being flushed.");

        LinearGain.Apply(samples, _volume);
        return _limiter.Process(samples);
    }

    /// <summary>Emits only retained input samples, preserving the exact input duration and final consonants.</summary>
    public int Flush(Span<float> destination)
    {
        _flushing = true;
        return _limiter.Flush(destination);
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
