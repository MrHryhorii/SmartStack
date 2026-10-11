using System.Buffers;
using NAudio.Dsp;

namespace ONNX_Runner.Services.Synthesis;

/// <summary>Request-local output conversion shared by speech, pauses, and spatial tails.</summary>
internal sealed class AudioOutputResampler : IDisposable
{
    private const int BlocksPerSecond = 100;
    private readonly AudioResampler _resampler;
    private readonly int _blockLength;
    private float[]? _output;

    /// <summary>Creates request-local resampling state and rents a fixed-size output block.</summary>
    internal AudioOutputResampler(int sourceRate, int targetRate)
    {
        _resampler = new AudioResampler(sourceRate, targetRate);
        _blockLength = Math.Max(1, targetRate / BlocksPerSecond);
        _output = ArrayPool<float>.Shared.Rent(_blockLength);
    }

    /// <summary>Filters input in place when needed, then writes converted blocks while retaining resampler history.</summary>
    internal void Write(Span<float> samples, AudioStreamManager manager, BiQuadFilter? filter)
    {
        ObjectDisposedException.ThrowIf(_output == null, this);
        // Cloning cleanup is defined at the DSP rate, before the output rate changes.
        if (filter != null)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = filter.Transform(samples[i]);
            }
        }
        while (!samples.IsEmpty)
        {
            var result = _resampler.Process(samples, _output!, _blockLength);
            manager.WriteChunk(_output.AsSpan(0, result.Written));
            samples = samples[result.Consumed..];
        }
    }

    /// <summary>Drains the final partial output before the audio encoder is finalized.</summary>
    internal void Complete(AudioStreamManager manager)
    {
        ObjectDisposedException.ThrowIf(_output == null, this);
        int written;
        do
        {
            written = _resampler.Complete(_output!, _blockLength);
            manager.WriteChunk(_output.AsSpan(0, written));
        } while (written == _blockLength);
    }

    /// <summary>Returns the rented output block once; the audio stream manager remains caller-owned.</summary>
    public void Dispose()
    {
        if (_output == null)
        {
            return;
        }
        ArrayPool<float>.Shared.Return(_output);
        _output = null;
    }
}
