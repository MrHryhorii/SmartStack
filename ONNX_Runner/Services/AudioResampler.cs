using System.Buffers;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Reusable mono WDL resampler for pooled PCM buffers.
/// Complete-chunk resampling resets DSP state; incremental output resampling preserves
/// filter history and fractional position until Complete is called.
/// </summary>
public sealed class AudioResampler
{
    private const int OutputSafetyFloor = 256;

    private readonly WdlResampler _resampler = new();
    private readonly int _sourceRate;
    private readonly int _targetRate;
    private readonly bool _passthrough;
    private float[]? _streamInput;
    private int _streamInputOffset;
    private int _streamInputCount;
    private int _streamInputNeeded;
    private int _streamOutputLength;
    private bool _streamStarted;
    private bool _streamCompleted;
    private bool _streamDrained;

    public AudioResampler(int sourceRate, int targetRate)
    {
        if (sourceRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRate));
        }

        if (targetRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetRate));
        }

        _sourceRate = sourceRate;
        _targetRate = targetRate;
        _passthrough = sourceRate == targetRate;

        if (_passthrough)
        {
            return;
        }

        // Match NAudio's WdlResamplingSampleProvider quality profile.
        _resampler.SetMode(interp: true, filtercnt: 2, sinc: false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(false);
        _resampler.SetRates(sourceRate, targetRate);
    }

    /// <summary>
    /// Resamples one complete mono PCM chunk into an ArrayPool-rented output buffer.
    /// If source and target rates match, the valid input samples are copied into a rented buffer
    /// so callers always receive the same ownership contract.
    /// The caller owns the returned buffer and must return it to ArrayPool&lt;float&gt;.Shared.
    /// </summary>
    public (float[] Buffer, int Length) Resample(float[] samples, int length)
    {
        if (_streamStarted)
        {
            throw new InvalidOperationException("Complete-chunk and incremental resampling cannot share an active instance.");
        }
        ArgumentNullException.ThrowIfNull(samples);
        if ((uint)length > (uint)samples.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (length == 0)
        {
            return (ArrayPool<float>.Shared.Rent(1), 0);
        }

        if (_passthrough)
        {
            float[] clone = ArrayPool<float>.Shared.Rent(length);
            samples.AsSpan(0, length).CopyTo(clone);
            return (clone, length);
        }

        _resampler.Reset();

        int rawExpectedLength = checked((int)Math.Ceiling((double)length * _targetRate / _sourceRate));
        int safetyMargin = Math.Max(OutputSafetyFloor, rawExpectedLength / 20);
        int expectedLength = checked(rawExpectedLength + safetyMargin);
        float[] output = ArrayPool<float>.Shared.Rent(expectedLength);

        try
        {
            int inputOffset = 0;
            int outputLength = 0;

            while (inputOffset < length)
            {
                int remainingInput = length - inputOffset;
                int remainingExpected = checked((int)Math.Ceiling(
                    (double)remainingInput * _targetRate / _sourceRate));
                int outputRequest = checked(remainingExpected + Math.Max(OutputSafetyFloor, remainingExpected / 20));

                EnsureOutputSpace(ref output, outputLength, outputRequest);

                int inputNeeded = _resampler.ResamplePrepare(
                    outputRequest,
                    1,
                    out float[] inputBuffer,
                    out int inputBufferOffset);

                if (inputNeeded <= 0)
                {
                    throw new InvalidOperationException("WDL resampler did not request input samples.");
                }

                int inputAvailable = Math.Min(inputNeeded, remainingInput);
                samples.AsSpan(inputOffset, inputAvailable)
                    .CopyTo(inputBuffer.AsSpan(inputBufferOffset, inputAvailable));

                int produced = _resampler.ResampleOut(
                    output,
                    outputLength,
                    inputAvailable,
                    outputRequest,
                    1);

                inputOffset += inputAvailable;
                outputLength += produced;

                // Under-feeding ResampleOut is WDL's end-of-stream flush path.
                if (inputAvailable < inputNeeded)
                {
                    return (output, outputLength);
                }
            }

            // The final input block can exactly match WDL's request. Explicitly under-feed one
            // final prepare cycle so any fractional/interpolation tail is emitted before return.
            EnsureOutputSpace(ref output, outputLength, OutputSafetyFloor);
            int flushRequest = _resampler.ResamplePrepare(
                OutputSafetyFloor,
                1,
                out _,
                out _);

            if (flushRequest > 0)
            {
                outputLength += _resampler.ResampleOut(
                    output,
                    outputLength,
                    0,
                    OutputSafetyFloor,
                    1);
            }

            return (output, outputLength);
        }
        catch
        {
            ArrayPool<float>.Shared.Return(output);
            throw;
        }
    }

    /// <summary>
    /// Consumes input into one fixed-size output block without flushing or resetting DSP state.
    /// The output block length must remain constant throughout this stream. A partial block
    /// returns zero Written samples and is retained until more input arrives or Complete runs.
    /// </summary>
    public (int Consumed, int Written) Process(ReadOnlySpan<float> samples, float[] output, int outputLength)
    {
        ValidateStreamingOutput(output, outputLength);
        if (_streamCompleted)
        {
            throw new InvalidOperationException("The resampling stream is complete.");
        }
        if (_passthrough)
        {
            int count = Math.Min(samples.Length, outputLength);
            samples[..count].CopyTo(output);
            return (count, count);
        }
        if (samples.IsEmpty)
        {
            return (0, 0);
        }

        PrepareStreamingInput(outputLength);
        int consumed = Math.Min(samples.Length, _streamInputNeeded - _streamInputCount);
        samples[..consumed].CopyTo(_streamInput!.AsSpan(_streamInputOffset + _streamInputCount, consumed));
        _streamInputCount += consumed;
        if (_streamInputCount < _streamInputNeeded)
        {
            return (consumed, 0);
        }

        int written = _resampler.ResampleOut(output, 0, _streamInputCount, outputLength, 1);
        _streamInputNeeded = 0;
        _streamInputCount = 0;
        return (consumed, written);
    }

    /// <summary>
    /// Drains the final output without adding a silence tail. Repeat while a full block is
    /// returned; subsequent calls return zero after the final partial block has been drained.
    /// </summary>
    public int Complete(float[] output, int outputLength)
    {
        ValidateStreamingOutput(output, outputLength);
        if (_streamDrained)
        {
            return 0;
        }
        _streamCompleted = true;
        if (_passthrough)
        {
            _streamDrained = true;
            return 0;
        }

        PrepareStreamingInput(outputLength);
        int written = _resampler.ResampleOut(output, 0, _streamInputCount, outputLength, 1);
        _streamInputNeeded = 0;
        _streamInputCount = 0;
        _streamDrained = written < outputLength;
        return written;
    }

    private void ValidateStreamingOutput(float[] output, int outputLength)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (outputLength <= 0 || outputLength > output.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        }
        if (_streamStarted && outputLength != _streamOutputLength)
        {
            throw new ArgumentException("The resampling output block length cannot change mid-stream.", nameof(outputLength));
        }
        if (!_streamStarted)
        {
            _resampler.Reset();
            _streamOutputLength = outputLength;
            _streamStarted = true;
        }
    }

    private void PrepareStreamingInput(int outputLength)
    {
        if (_streamInputNeeded != 0)
        {
            return;
        }
        _streamInputNeeded = _resampler.ResamplePrepare(outputLength, 1, out _streamInput, out _streamInputOffset);
        if (_streamInputNeeded <= 0)
        {
            throw new InvalidOperationException("WDL resampler did not request input samples.");
        }
    }

    private static void EnsureOutputSpace(ref float[] buffer, int used, int additional)
    {
        int required = checked(used + Math.Max(additional, 1));
        if (required <= buffer.Length)
        {
            return;
        }

        int newSize = Math.Max(required, checked(buffer.Length * 2));
        float[] grown = ArrayPool<float>.Shared.Rent(newSize);
        buffer.AsSpan(0, used).CopyTo(grown);
        ArrayPool<float>.Shared.Return(buffer);
        buffer = grown;
    }
}
