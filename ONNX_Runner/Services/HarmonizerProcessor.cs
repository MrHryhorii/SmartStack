using Signalsmith;

namespace ONNX_Runner.Services;

/// <summary>Request-local fixed-interval harmony with aligned dry audio and preserved formants.</summary>
internal sealed class HarmonizerProcessor : IDisposable
{
    private const int ProcessingBlock = 1024;
    private static readonly double VoiceWeight = 1 / Math.Sqrt(3);
    private readonly Stretch _third, _fifth;
    private readonly WindowedPowerMatcher _level;
    private readonly int _sampleRate, _inputLatency, _outputLatency;
    private readonly float[] _thirdOutput, _fifthOutput, _dry, _history, _silence, _tail;
    private StreamingPowerMatchedMixer? _mixer;
    private int _historyPosition, _priming, _pending, _tailPosition, _tailLength;
    private float _amount = 1;
    private bool _finishing, _disposed;

    internal int LatencySamples { get; }
    internal int PendingSamples => _pending;

    internal HarmonizerProcessor(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 192000);
        _sampleRate = sampleRate;
        _third = CreateVoice(sampleRate, 4);
        try
        {
            _fifth = CreateVoice(sampleRate, 7);
            _inputLatency = _third.InputLatency();
            _outputLatency = _third.OutputLatency();
            if (_inputLatency != _fifth.InputLatency() || _outputLatency != _fifth.OutputLatency())
                throw new InvalidOperationException("Harmony voices have different latencies.");
            LatencySamples = _inputLatency + _outputLatency;
            _priming = LatencySamples;
            int capacity = Math.Max(ProcessingBlock, _outputLatency);
            _thirdOutput = new float[capacity];
            _fifthOutput = new float[capacity];
            _dry = new float[capacity];
            _silence = new float[capacity];
            _history = new float[LatencySamples];
            _tail = new float[LatencySamples];
            _level = new WindowedPowerMatcher(sampleRate);
        }
        catch
        {
            _third.Dispose();
            _fifth?.Dispose();
            throw;
        }
    }

    /// <summary>Compacts ready output into the buffer prefix; priming does not emit padding.</summary>
    internal int Process(Span<float> samples, float amount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finishing) throw new InvalidOperationException("The harmonizer is already being completed.");
        if (!float.IsFinite(amount) || amount < 0 || amount > 1)
            throw new ArgumentOutOfRangeException(nameof(amount));
        _amount = amount;
        if (amount < 1) _mixer ??= new StreamingPowerMatchedMixer(_sampleRate);
        _mixer?.Prepare(amount);
        int written = 0;
        for (int offset = 0; offset < samples.Length; offset += ProcessingBlock)
        {
            int count = Math.Min(ProcessingBlock, samples.Length - offset);
            Span<float> input = samples.Slice(offset, count);
            _third.Process(input, _thirdOutput.AsSpan(0, count));
            _fifth.Process(input, _fifthOutput.AsSpan(0, count));
            written += Render(input, samples.Slice(written, count));
        }
        _pending += samples.Length - written;
        return written;
    }

    /// <summary>Returns exactly the retained input duration; empty calls do not finish the stream.</summary>
    internal int Complete(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.IsEmpty || _pending == 0) return 0;
        if (!_finishing) Finish();
        int count = Math.Min(destination.Length, _tailLength - _tailPosition);
        _tail.AsSpan(_tailPosition, count).CopyTo(destination);
        _tailPosition += count;
        _pending -= count;
        return count;
    }

    private int Render(ReadOnlySpan<float> input, Span<float> destination)
    {
        int written = 0;
        for (int i = 0; i < input.Length; i++)
        {
            float reference = _history[_historyPosition];
            _history[_historyPosition] = input[i];
            if (++_historyPosition == _history.Length) _historyPosition = 0;
            if (_priming > 0) { _priming--; continue; }

            // Retain profile B's accumulation order and scalar voice-power correction.
            float wet = (float)(VoiceWeight * reference);
            wet += (float)(VoiceWeight * _thirdOutput[i]);
            wet += (float)(VoiceWeight * _fifthOutput[i]);
            _dry[written] = reference;
            destination[written++] = _level.Process(reference, wet);
        }
        if (_amount < 1 && written > 0)
            _mixer!.Mix(_dry.AsSpan(0, written), destination[..written], _amount);
        return written;
    }

    private void Finish()
    {
        _finishing = true;
        for (int offset = 0; offset < _inputLatency; offset += ProcessingBlock)
        {
            int count = Math.Min(ProcessingBlock, _inputLatency - offset);
            Span<float> zeros = _silence.AsSpan(0, count);
            _third.Process(zeros, _thirdOutput.AsSpan(0, count));
            _fifth.Process(zeros, _fifthOutput.AsSpan(0, count));
            _tailLength += Render(zeros, _tail.AsSpan(_tailLength));
        }
        // Flush resets upstream spectral state. Call it once, then serve any output partitions.
        _third.Flush(_thirdOutput.AsSpan(0, _outputLatency), 1);
        _fifth.Flush(_fifthOutput.AsSpan(0, _outputLatency), 1);
        _tailLength += Render(_silence.AsSpan(0, _outputLatency), _tail.AsSpan(_tailLength));
        if (_tailLength != _pending)
            throw new InvalidOperationException("Harmony completion changed the retained duration.");
    }

    private static Stretch CreateVoice(int sampleRate, int semitones)
    {
        var voice = new Stretch(42);
        try
        {
            voice.PresetDefault(1, sampleRate, false);
            voice.SetTransposeSemitones(semitones, 1);
            voice.SetFormantSemitones(0, true);
            voice.SetFormantBase(0);
            return voice;
        }
        catch
        {
            voice.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _third.Dispose();
        _fifth.Dispose();
        _pending = 0;
    }
}
