using ONNX_Runner.Services.Audio.Levels;
using Signalsmith;

namespace ONNX_Runner.Services.Audio.Effects;

/// <summary>Request-local fixed-interval harmony with aligned dry audio and preserved formants.</summary>
internal sealed class HarmonizerProcessor : IDisposable
{
    private const int ProcessingBlock = 1024;
    private static readonly double VoiceWeight = 1 / Math.Sqrt(3);
    private readonly Stretch _third, _fifth;
    private readonly HarmonizerLevelMatcher _level;
    private readonly int _inputLatency, _outputLatency;
    private readonly float[] _thirdOutput, _fifthOutput, _history, _silence, _tail;
    private int _historyPosition, _priming, _pending, _tailPosition, _tailLength;
    private float _preparedAmount = float.NaN;
    private double _dryWeight = VoiceWeight, _harmonyWeight = VoiceWeight;
    private bool _finishing, _disposed;

    internal int LatencySamples { get; }
    internal int PendingSamples => _pending;

    internal HarmonizerProcessor(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 192000);
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
            _silence = new float[capacity];
            _history = new float[LatencySamples];
            _tail = new float[LatencySamples];
            _level = new HarmonizerLevelMatcher(sampleRate);
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
        if (_preparedAmount != amount)
        {
            _preparedAmount = amount;
            // Interpolate voice contributions before the request-local scalar level correction.
            _harmonyWeight = VoiceWeight * amount;
            _dryWeight = 1d - amount + _harmonyWeight;
            _level.Prepare(_dryWeight, _harmonyWeight);
        }
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

            // Retain profile B's voice accumulation; correct its local power after summing.
            float wet = (float)(_dryWeight * reference);
            wet += (float)(_harmonyWeight * _thirdOutput[i]);
            wet += (float)(_harmonyWeight * _fifthOutput[i]);
            destination[written++] = _level.Process(reference, wet);
        }
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
