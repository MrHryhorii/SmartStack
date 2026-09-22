using SoundTouch;

namespace ONNX_Runner.Services;

/// <summary>
/// Stateful DSP module for real-time pitch shifting without changing playback tempo.
/// One instance is intended to process one continuous mono PCM stream.
/// </summary>
public sealed class PitchShifter : IDisposable
{
    private readonly SoundTouchProcessor _soundTouch;
    private bool _disposed;

    public PitchShifter(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        _soundTouch = new SoundTouchProcessor
        {
            SampleRate = sampleRate,
            Channels = 1,
            Tempo = 1.0f
        };

        // SoundTouch's reference speech profile from the SoundStretch example.
        // Full seek + anti-aliasing favor voice quality over the faster quick-seek path.
        _soundTouch.SetSetting(SettingId.UseQuickSeek, 0);
        _soundTouch.SetSetting(SettingId.UseAntiAliasFilter, 1);
        _soundTouch.SetSetting(SettingId.SequenceDurationMs, 40);
        _soundTouch.SetSetting(SettingId.SeekWindowDurationMs, 15);
        _soundTouch.SetSetting(SettingId.OverlapDurationMs, 8);
    }

    /// <summary>
    /// Sets the pitch multiplier.
    /// 1.0 = original pitch, >1.0 = higher pitch, <1.0 = lower pitch.
    /// </summary>
    public void SetPitch(float pitch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _soundTouch.Pitch = Math.Clamp(pitch, 0.5f, 2.0f);
    }

    /// <summary>
    /// Adds another PCM chunk to the current SoundTouch stream.
    /// SoundTouch buffers internally, so output is drained separately with Drain().
    /// </summary>
    public void ProcessChunk(float[] inputSamples, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(inputSamples);

        if ((uint)length > (uint)inputSamples.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (length == 0)
        {
            return;
        }

        _soundTouch.PutSamples(inputSamples.AsSpan(0, length), length);
    }

    /// <summary>
    /// Copies all currently available processed samples that fit into destination.
    /// Returns the number of samples written. Call repeatedly until it returns zero.
    /// </summary>
    public int Drain(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (destination.IsEmpty)
        {
            return 0;
        }

        return _soundTouch.ReceiveSamples(destination, destination.Length);
    }

    /// <summary>
    /// Pushes the remaining samples of the current logical speech segment to the output.
    /// Call only at a real segment boundary (for example before a sentence pause) or
    /// at the end of the request. Drain() must be called afterwards until it returns zero.
    /// </summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _soundTouch.Flush();
    }

    /// <summary>
    /// Clears SoundTouch's internal buffers and stream bookkeeping after a completed segment.
    /// Call only after Flush() has been fully drained.
    /// Pitch and speech-processing settings remain configured on the processor.
    /// </summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _soundTouch.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
