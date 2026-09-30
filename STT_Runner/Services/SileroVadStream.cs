using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace STT_Runner.Services;

/// <summary>Per-stream Silero state and the separate 64-sample waveform context.</summary>
internal sealed class SileroVadStream
{
    internal const int WindowSize = 512;
    private const int ContextSize = 64;
    private readonly InferenceSession _session;
    private readonly DenseTensor<float> _input = new([1, ContextSize + WindowSize]);
    private readonly DenseTensor<float> _state = new([2, 1, 128]);
    private readonly List<NamedOnnxValue> _inputs;

    public SileroVadStream(InferenceSession session)
    {
        _session = session;
        _inputs = [
            NamedOnnxValue.CreateFromTensor("input", _input),
            NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new long[] { 16_000 }, [1])),
            NamedOnnxValue.CreateFromTensor("state", _state)
        ];
    }

    public float Predict(ReadOnlySpan<float> frame)
    {
        if (frame.Length is < 1 or > WindowSize)
            throw new ArgumentOutOfRangeException(nameof(frame));

        Span<float> samples = _input.Buffer.Span;
        // The first 64 samples already hold the previous window's tail.
        samples[ContextSize..].Clear();
        frame.CopyTo(samples[ContextSize..]);
        using var results = _session.Run(_inputs);
        float probability = results.First(result => result.Name == "output")
            .AsTensor<float>().GetValue(0);
        var nextState = (DenseTensor<float>)results.First(result => result.Name == "stateN").Value;
        nextState.Buffer.Span.CopyTo(_state.Buffer.Span);
        samples[^ContextSize..].CopyTo(samples[..ContextSize]);
        return probability;
    }
}
