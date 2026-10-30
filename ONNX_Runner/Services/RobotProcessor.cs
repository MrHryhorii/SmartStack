namespace ONNX_Runner.Services;

/// <summary>Request-local robotization with aligned dry PCM, shared level matching and exact completion.</summary>
internal sealed class RobotProcessor : IDisposable
{
    private const int ProcessingBlock = 1024;
    private readonly int _sampleRate;
    private readonly RobotSpectralCore _core;
    private readonly WindowedPowerMatcher _level;
    private readonly float[] _history, _dry = new float[ProcessingBlock], _silence = new float[ProcessingBlock];
    private StreamingPowerMatchedMixer? _mixer;
    private int _historyPosition, _priming, _pending;
    private float _amount = 1;
    private bool _finishing, _disposed;

    internal int LatencySamples => _core.LatencySamples;
    internal int PendingSamples => _pending;

    internal RobotProcessor(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 192000);
        _sampleRate = sampleRate;
        // Preserve the accepted 512/120 preset at 22050 Hz and its approximate audio-time scale elsewhere.
        int order = (int)Math.Round(Math.Log2(512.0 * sampleRate / 22050));
        int size = 1 << order;
        int hop = Math.Max(1, (int)Math.Round(120.0 * sampleRate / 22050));
        _core = new RobotSpectralCore(hop, size);
        _history = new float[LatencySamples];
        _priming = LatencySamples;
        _level = new WindowedPowerMatcher(sampleRate);
    }

    /// <summary>Compacts ready output into the input prefix; the fixed wet algorithm ignores intensity.</summary>
    internal int Process(Span<float> samples, float amount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finishing) throw new InvalidOperationException("The robot processor is already being completed.");
        if (!float.IsFinite(amount) || amount < 0 || amount > 1)
            throw new ArgumentOutOfRangeException(nameof(amount));
        _amount = amount;
        if (amount < 1) _mixer ??= new StreamingPowerMatchedMixer(_sampleRate);
        _mixer?.Prepare(amount);
        int written = 0;
        for (int offset = 0; offset < samples.Length; offset += ProcessingBlock)
        {
            int count = Math.Min(ProcessingBlock, samples.Length - offset);
            written += Render(samples.Slice(offset, count), samples[written..]);
        }
        _pending += samples.Length - written;
        return written;
    }

    /// <summary>Returns exactly the retained input duration, without adding silence or dropping the final phonemes.</summary>
    internal int Complete(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.IsEmpty || _pending == 0) return 0;
        _finishing = true;
        int written = 0;
        while (written < destination.Length && _pending > 0)
        {
            int count = Math.Min(ProcessingBlock, _priming + Math.Min(_pending, destination.Length - written));
            int ready = Render(_silence.AsSpan(0, count), destination[written..]);
            written += ready;
            _pending -= ready;
        }
        return written;
    }

    private int Render(ReadOnlySpan<float> input, Span<float> destination)
    {
        int written = 0;
        for (int i = 0; i < input.Length; i++)
        {
            float sample = input[i];
            float reference = _history[_historyPosition];
            _history[_historyPosition] = sample;
            if (++_historyPosition == _history.Length) _historyPosition = 0;
            float wet = _core.Process(sample);
            if (_priming > 0) { _priming--; continue; }
            _dry[written] = reference;
            destination[written++] = _level.Process(reference, wet);
        }
        if (_amount < 1 && written > 0)
            _mixer!.Mix(_dry.AsSpan(0, written), destination[..written], _amount);
        return written;
    }

    public void Dispose()
    {
        _disposed = true;
        _pending = 0;
    }
}
