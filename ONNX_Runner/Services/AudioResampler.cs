using System.Buffers;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Request-local mono WDL resampling with caller-owned spans or pooled output.
/// Complete chunks reset DSP state; incremental conversion retains history until Complete.
/// </summary>
public sealed class AudioResampler
{
    private const int CompleteOutputBlockLength = 512;

    private readonly WdlResampler _resampler = new();
    private readonly int _sourceRate;
    private readonly int _targetRate;
    private readonly bool _passthrough;
    private int _streamInputCount;
    private int _streamInputNeeded;
    private int _streamOutputLength;
    private bool _streamStarted;
    private bool _streamCompleted;
    private bool _streamDrained;

    /// <summary>Validates positive rates and preserves the established WDL filter profile.</summary>
    public AudioResampler(int sourceRate, int targetRate)
    {
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(targetRate));

        _sourceRate = sourceRate;
        _targetRate = targetRate;
        _passthrough = sourceRate == targetRate;
        if (_passthrough) return;

        _resampler.SetMode(interp: true, filtercnt: 2, sinc: false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(false);
        _resampler.SetRates(sourceRate, targetRate);
    }

    /// <summary>
    /// Returns the required destination capacity, including WDL's final padded block.
    /// Only the sample count returned by Resample is valid audio.
    /// </summary>
    public int GetOutputCapacity(int inputLength)
    {
        if (inputLength < 0) throw new ArgumentOutOfRangeException(nameof(inputLength));
        if (inputLength == 0 || _passthrough) return inputLength;
        int expected = checked((int)Math.Ceiling((double)inputLength * _targetRate / _sourceRate));
        return checked(expected + CompleteOutputBlockLength);
    }

    /// <summary>Resamples the valid array region; the caller returns Buffer to ArrayPool&lt;float&gt;.Shared.</summary>
    public (float[] Buffer, int Length) Resample(float[] samples, int length)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if ((uint)length > (uint)samples.Length) throw new ArgumentOutOfRangeException(nameof(length));
        return Resample(samples.AsSpan(0, length));
    }

    /// <summary>Resamples a complete chunk into pooled output without copying to an intermediate input array.</summary>
    public (float[] Buffer, int Length) Resample(ReadOnlySpan<float> samples)
    {
        EnsureCompleteChunkMode();
        float[] output = ArrayPool<float>.Shared.Rent(Math.Max(1, GetOutputCapacity(samples.Length)));
        try { return (output, Resample(samples, output)); }
        catch
        {
            ArrayPool<float>.Shared.Return(output);
            throw;
        }
    }

    /// <summary>
    /// Resamples a complete chunk into a destination sized with GetOutputCapacity.
    /// Fixed requests bound WDL's internal buffers independently of the input duration.
    /// Input and output must not overlap when rates differ.
    /// </summary>
    public int Resample(ReadOnlySpan<float> samples, Span<float> output)
    {
        EnsureCompleteChunkMode();
        if (output.Length < GetOutputCapacity(samples.Length))
            throw new ArgumentException("The resampling output span is too small.", nameof(output));
        if (samples.IsEmpty) return 0;
        if (_passthrough)
        {
            samples.CopyTo(output);
            return samples.Length;
        }
        if (samples.Overlaps(output))
            throw new ArgumentException("Resampling input and output must not overlap.", nameof(output));

        _resampler.Reset();
        int written = 0;
        while (true)
        {
            int needed = _resampler.ResamplePrepare(CompleteOutputBlockLength, 1, out Span<float> input);
            if (needed <= 0) throw new InvalidOperationException("WDL resampler did not request input samples.");
            int consumed = Math.Min(samples.Length, needed);
            samples[..consumed].CopyTo(input);
            written += _resampler.ResampleOut(output[written..], consumed, CompleteOutputBlockLength, 1);
            samples = samples[consumed..];
            // A short feed flushes WDL. An exact final feed reaches this branch on the next cycle.
            if (consumed < needed) return written;
        }
    }

    /// <summary>Compatibility overload; only outputLength samples form the fixed output block.</summary>
    public (int Consumed, int Written) Process(ReadOnlySpan<float> samples, float[] output, int outputLength)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (outputLength <= 0 || outputLength > output.Length)
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        return Process(samples, output.AsSpan(0, outputLength));
    }

    /// <summary>
    /// Consumes input into a fixed output span, retaining incomplete input and filter history.
    /// The output span length must remain constant throughout the stream.
    /// </summary>
    public (int Consumed, int Written) Process(ReadOnlySpan<float> samples, Span<float> output)
    {
        ValidateStreamingOutput(output.Length);
        if (_streamCompleted) throw new InvalidOperationException("The resampling stream is complete.");
        if (_passthrough)
        {
            int count = Math.Min(samples.Length, output.Length);
            samples[..count].CopyTo(output);
            return (count, count);
        }
        if (samples.IsEmpty) return (0, 0);

        Span<float> input = PrepareStreamingInput(output.Length);
        int consumed = Math.Min(samples.Length, _streamInputNeeded - _streamInputCount);
        samples[..consumed].CopyTo(input.Slice(_streamInputCount, consumed));
        _streamInputCount += consumed;
        if (_streamInputCount < _streamInputNeeded) return (consumed, 0);

        int written = _resampler.ResampleOut(output, _streamInputCount, output.Length, 1);
        _streamInputNeeded = 0;
        _streamInputCount = 0;
        return (consumed, written);
    }

    /// <summary>Compatibility overload; drains into the valid output array region.</summary>
    public int Complete(float[] output, int outputLength)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (outputLength <= 0 || outputLength > output.Length)
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        return Complete(output.AsSpan(0, outputLength));
    }

    /// <summary>Drains once without adding an audio tail; repeat while a full output block is returned.</summary>
    public int Complete(Span<float> output)
    {
        ValidateStreamingOutput(output.Length);
        if (_streamDrained) return 0;
        _streamCompleted = true;
        if (_passthrough)
        {
            _streamDrained = true;
            return 0;
        }

        PrepareStreamingInput(output.Length);
        int written = _resampler.ResampleOut(output, _streamInputCount, output.Length, 1);
        _streamInputNeeded = 0;
        _streamInputCount = 0;
        _streamDrained = written < output.Length;
        return written;
    }

    /// <summary>Rejects complete-chunk use after incremental conversion has started.</summary>
    private void EnsureCompleteChunkMode()
    {
        if (_streamStarted)
            throw new InvalidOperationException("Complete-chunk and incremental resampling cannot share an active instance.");
    }

    /// <summary>Validates the fixed output block and initializes streaming state on first use.</summary>
    private void ValidateStreamingOutput(int outputLength)
    {
        if (outputLength <= 0) throw new ArgumentOutOfRangeException(nameof(outputLength));
        if (_streamStarted && outputLength != _streamOutputLength)
            throw new ArgumentException("The resampling output block length cannot change mid-stream.", nameof(outputLength));
        if (_streamStarted) return;
        _resampler.Reset();
        _streamOutputLength = outputLength;
        _streamStarted = true;
    }

    /// <summary>Reacquires WDL's input span while retaining samples from a partial input feed.</summary>
    private Span<float> PrepareStreamingInput(int outputLength)
    {
        // Repeated prepares with the same request retain WDL's pending input region.
        int needed = _resampler.ResamplePrepare(outputLength, 1, out Span<float> input);
        if (needed <= 0) throw new InvalidOperationException("WDL resampler did not request input samples.");
        if (_streamInputNeeded != 0 && needed != _streamInputNeeded)
            throw new InvalidOperationException("WDL changed a partially filled input region.");
        _streamInputNeeded = needed;
        return input;
    }
}
