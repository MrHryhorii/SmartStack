using System.Buffers;

namespace STT_Runner.Services;

/// <summary>
/// Silero-style hysteresis and silence timing. PCM inside an active segment is
/// retained verbatim; probability never attenuates or removes interior samples.
/// </summary>
internal sealed class VadSegmenter : IDisposable
{
    private const int SampleRate = 16_000;
    private readonly float _threshold;
    private readonly float _exitThreshold;
    private readonly int _pauseSamples;
    private readonly int _minimumSpeechSamples;
    private readonly int _prefixSamples;
    private readonly int _tailSamples;
    private readonly int _maximumSamples;
    private readonly int _bufferLimit;
    private readonly int _overlapSamples;
    private float[] _buffer;
    private readonly float[] _prefix;
    private int _prefixLength;
    private int _length;
    private long _start;
    private long _speechStart;
    private long? _silenceStart;
    private long _lastEmittedEnd;
    private bool _speaking;
    public long SamplesRead { get; private set; }

    public VadSegmenter(VadProfile profile, VadSegmentationOptions? options = null)
    {
        _threshold = options?.Threshold ?? profile.Threshold;
        _exitThreshold = Math.Min(_threshold, profile.ExitThreshold ?? Math.Max(_threshold - 0.15f, 0.01f));
        _pauseSamples = (options?.SilenceDurationMs ?? profile.PauseMs) * SampleRate / 1000;
        _minimumSpeechSamples = profile.MinSpeechMs * SampleRate / 1000;
        _prefixSamples = (options?.PrefixPaddingMs ?? profile.PrefixPaddingMs) * SampleRate / 1000;
        _tailSamples = profile.TailPaddingMs * SampleRate / 1000;
        _maximumSamples = profile.MaxSegmentSeconds * SampleRate;
        _bufferLimit = profile.MaxBufferedSegmentSeconds * SampleRate;
        _overlapSamples = profile.SplitOverlapMs * SampleRate / 1000;
        _buffer = ArrayPool<float>.Shared.Rent(Math.Min(_bufferLimit, SampleRate * 2));
        _prefix = _prefixSamples == 0 ? [] : ArrayPool<float>.Shared.Rent(_prefixSamples);
    }

    public AudioPiece? Accept(ReadOnlySpan<float> frame, float probability)
    {
        if (frame.IsEmpty || frame.Length > SileroVadStream.WindowSize)
            throw new ArgumentOutOfRangeException(nameof(frame));
        long frameStart = SamplesRead;
        SamplesRead += frame.Length;
        AudioPiece? output = null;
        if (!_speaking && probability >= _threshold)
        {
            // A normal pause never duplicates audio already emitted in the previous tail.
            int prefixLength = (int)Math.Min(_prefixLength, Math.Max(0, frameStart - _lastEmittedEnd));
            _start = frameStart - prefixLength;
            _speechStart = frameStart;
            EnsureCapacity(prefixLength + frame.Length);
            _prefix.AsSpan(_prefixLength - prefixLength, prefixLength).CopyTo(_buffer);
            _length = prefixLength;
            _speaking = true;
        }
        if (_speaking)
        {
            if (_maximumSamples > 0 && _length + frame.Length > _maximumSamples)
            {
                output = Emit(_length);
                int overlap = Math.Min(_overlapSamples, Math.Min(_length, Math.Max(0, _maximumSamples - frame.Length)));
                _buffer.AsSpan(_length - overlap, overlap).CopyTo(_buffer);
                _start += _length - overlap;
                _length = overlap;
                _speechStart = _start;
                _silenceStart = null;
            }
            EnsureCapacity(_length + frame.Length);
            frame.CopyTo(_buffer.AsSpan(_length));
            _length += frame.Length;
            if (probability >= _threshold) _silenceStart = null;
            if (probability < _exitThreshold)
            {
                _silenceStart ??= frameStart;
                // The clock follows decoded samples, never arrival time or segment length.
                if (frameStart - _silenceStart.Value >= _pauseSamples)
                {
                    long boundary = _silenceStart.Value;
                    if (boundary - _speechStart >= _minimumSpeechSamples)
                    {
                        int emittedLength = (int)Math.Min(_length, boundary - _start + _tailSamples);
                        output = Emit(emittedLength);
                    }
                    ResetSpeech();
                }
            }
        }
        RememberPrefix(frame);
        return output;
    }

    public AudioPiece? Finish()
    {
        if (!_speaking) return null;
        long speechEnd = _silenceStart ?? SamplesRead;
        AudioPiece? output = speechEnd - _speechStart >= _minimumSpeechSamples ? Emit(_length) : null;
        ResetSpeech();
        return output;
    }

    private AudioPiece Emit(int length)
    {
        IMemoryOwner<float> owner = MemoryPool<float>.Shared.Rent(length);
        _buffer.AsSpan(0, length).CopyTo(owner.Memory.Span);
        _lastEmittedEnd = _start + length;
        return new AudioPiece(owner, length, _start);
    }

    private void ResetSpeech()
    {
        _speaking = false;
        _length = 0;
        _silenceStart = null;
    }

    private void RememberPrefix(ReadOnlySpan<float> frame)
    {
        if (_prefixSamples == 0) return;
        int added = Math.Min(frame.Length, _prefixSamples);
        int kept = Math.Min(_prefixLength, _prefixSamples - added);
        _prefix.AsSpan(_prefixLength - kept, kept).CopyTo(_prefix);
        frame[^added..].CopyTo(_prefix.AsSpan(kept));
        _prefixLength = kept + added;
    }

    private void EnsureCapacity(int required)
    {
        if (required > _bufferLimit)
            throw new InvalidDataException("Speech segment exceeds MaxBufferedSegmentSeconds in the active VAD profile.");
        if (required <= _buffer.Length) return;
        int capacity = (int)Math.Min(_bufferLimit, Math.Max((long)_buffer.Length * 2, required));
        float[] larger = ArrayPool<float>.Shared.Rent(capacity);
        _buffer.AsSpan(0, _length).CopyTo(larger);
        ArrayPool<float>.Shared.Return(_buffer);
        _buffer = larger;
    }

    public void Dispose()
    {
        ArrayPool<float>.Shared.Return(_buffer);
        if (_prefixSamples > 0) ArrayPool<float>.Shared.Return(_prefix);
    }
}
