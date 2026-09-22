using System.Buffers;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Reusable mono WDL resampler for pooled PCM buffers.
/// Each Resample call starts from a clean DSP state so separate audio chunks remain independent,
/// while the configured native resampler object itself is reused across calls.
/// </summary>
public sealed class AudioResampler
{
    private const int OutputSafetyFloor = 256;

    private readonly WdlResampler _resampler = new();
    private readonly int _sourceRate;
    private readonly int _targetRate;
    private readonly bool _passthrough;

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
