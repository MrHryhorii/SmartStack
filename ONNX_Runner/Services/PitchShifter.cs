using SoundTouch;

namespace ONNX_Runner.Services;

/// <summary>
/// DSP module for real-time pitch shifting.
/// Uses the WSOLA algorithm to preserve the natural sound of the human voice 
/// without altering the playback speed.
/// </summary>
public class PitchShifter : IDisposable
{
    private readonly SoundTouchProcessor _soundTouch;
    private readonly float[] _receiveBuffer;
    private bool _disposed;

    public PitchShifter(int sampleRate)
    {
        _soundTouch = new SoundTouchProcessor
        {
            SampleRate = sampleRate,
            Channels = 1, // Mono audio
            Tempo = 1.0f  // Fix tempo; Piper handles playback speed
        };

        // Buffer for retrieving processed samples (8192 is a safe size for chunks)
        _receiveBuffer = new float[8192];
    }

    /// <summary>
    /// Sets the pitch shift factor.
    /// 1.0 = original, >1.0 = higher pitch, <1.0 = lower pitch.
    /// </summary>
    public void SetPitch(float pitch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Prevent extreme values that could distort the audio or break the algorithm
        _soundTouch.Pitch = Math.Clamp(pitch, 0.5f, 2.0f);
    }

    /// <summary>
    /// Processes a chunk of the audio stream.
    /// Iterates over all available output since pitch shifting may produce
    /// more samples than the input (e.g. pitch &lt; 1.0 stretches audio internally).
    /// </summary>
    public IEnumerable<ArraySegment<float>> ProcessChunk(ReadOnlySpan<float> inputSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Push raw samples into the processor.
        // ToArray() allocation is unavoidable here — SoundTouch.Net 2.x does not
        // expose a Span-based overload for PutSamples.
        _soundTouch.PutSamples(inputSamples.ToArray(), inputSamples.Length);

        return DrainBuffer();
    }

    /// <summary>
    /// Flushes any remaining audio from the internal buffers.
    /// Must be called at the end of the generation process!
    /// </summary>
    public IEnumerable<ArraySegment<float>> Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _soundTouch.Flush();

        return DrainBuffer();
    }

    /// <summary>
    /// Drains all currently available samples from the SoundTouch internal buffer.
    /// A loop is required because pitch shifting may produce more output samples
    /// than fit into a single receive call.
    /// </summary>
    private IEnumerable<ArraySegment<float>> DrainBuffer()
    {
        int received;
        while ((received = _soundTouch.ReceiveSamples(_receiveBuffer, _receiveBuffer.Length)) > 0)
        {
            // Yield a view into the shared buffer.
            // Callers must consume or copy before the next iteration.
            yield return new ArraySegment<float>(_receiveBuffer, 0, received);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}