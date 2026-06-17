using NAudio.Wave;
using NAudio.Lame;
using ONNX_Runner.Models;
using System.Buffers;
using Concentus.Oggfile;
using Concentus;
using Concentus.Enums;

namespace ONNX_Runner.Services;

/// <summary>
/// Manages the encoding, formatting, and routing of generated audio streams.
/// Supports dynamic format switching (WAV, MP3, OPUS, PCM) and handles both 
/// in-memory buffering (for static files) and real-time chunked network streaming.
/// </summary>
public class AudioStreamManager : IDisposable
{
    private readonly Stream _baseStream;
    private readonly Stream? _audioWriter;
    private readonly OpusOggWriteStream? _opusWriter;
    private readonly AudioFormat _format;
    private readonly bool _isMemoryStream;

    // Guards Finalize() so the underlying writers are torn down exactly once,
    // regardless of whether the caller reaches it via GetFinalAudioBytes(),
    // Dispose(), or both (e.g. GetFinalAudioBytes() followed by the using-block's
    // implicit Dispose()). NAudio.Lame's and Concentus' own Dispose/Finish methods
    // are not guaranteed to be safely re-entrant, so this flag is the single
    // source of truth rather than relying on their internal state.
    private bool _finalized;

    // --- OPUS MICRO-BUFFER VARIABLES ---
    // Opus encoding requires strictly sized frames (e.g., 20ms) to work correctly.
    private readonly short[]? _opusFrameBuffer;
    private readonly int _opusFrameSize;
    private int _opusBufferCount = 0;

    public AudioStreamManager(AudioFormat format, int sampleRate, Stream targetStream)
    {
        _format = format;
        _baseStream = targetStream;
        _isMemoryStream = targetStream is MemoryStream;

        if (_format == AudioFormat.Mp3)
        {
            var waveFormat = new WaveFormat(sampleRate, 16, 1);
            // 128 kbps is a standard, high-quality bitrate for voice audio
            _audioWriter = new LameMP3FileWriter(_baseStream, waveFormat, 128);
        }
        else if (_format == AudioFormat.Wav)
        {
            var waveFormat = new WaveFormat(sampleRate, 16, 1);
            _audioWriter = new WaveFileWriter(_baseStream, waveFormat);
        }
        else if (_format == AudioFormat.Opus)
        {
            // VoIP application profile is highly optimized for human speech encoding
            var encoder = OpusCodecFactory.CreateEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            _opusWriter = new OpusOggWriteStream(encoder, _baseStream);

            // Opus strictly requires exact 20 millisecond frames (e.g., 960 samples for 48kHz).
            // We calculate the exact frame size based on the target sample rate.
            _opusFrameSize = sampleRate / 50;
            _opusFrameBuffer = new short[_opusFrameSize];
        }
        else // AudioFormat.Pcm (Raw uncompressed 16-bit audio)
        {
            _audioWriter = _baseStream;
        }
    }

    /// <summary>
    /// Processes a chunk of raw 32-bit float audio, applies optional DSP filtering, 
    /// converts it to 16-bit PCM, and writes it to the selected encoder stream.
    /// </summary>
    public void WriteChunk(Span<float> samples, NAudio.Dsp.BiQuadFilter? filter = null)
    {
        if (filter != null)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] = filter.Transform(samples[i]);
        }

        // Rent memory to avoid garbage collection overhead during rapid chunk streaming
        short[] shortSamples = ArrayPool<short>.Shared.Rent(samples.Length);
        try
        {
            // --- SIMD VECTORIZATION (Hardware Accelerated Float-to-Short Conversion) ---
            // Neural networks output 32-bit floats (-1.0 to 1.0), but standard audio encoders 
            // expect 16-bit integers (-32768 to 32767). We use SIMD to process this conversion in bulk.
            int vectorSize = System.Numerics.Vector<float>.Count;
            int i = 0;
            var minVec = new System.Numerics.Vector<float>(-1f);
            var maxVec = new System.Numerics.Vector<float>(1f);
            var multVec = new System.Numerics.Vector<float>(32767f);

            for (; i <= samples.Length - vectorSize; i += vectorSize)
            {
                var vSamples = new System.Numerics.Vector<float>(samples[i..]);
                // Hard clipping: restrict values to exactly [-1.0, 1.0] to prevent integer overflow (audio wrapping/popping)
                var vClamped = System.Numerics.Vector.Max(minVec, System.Numerics.Vector.Min(maxVec, vSamples));
                var vScaled = vClamped * multVec;

                for (int k = 0; k < vectorSize; k++)
                    shortSamples[i + k] = (short)vScaled[k];
            }

            // Handle the remaining tail of the array that didn't fit into a SIMD vector
            for (; i < samples.Length; i++)
            {
                float sample = Math.Clamp(samples[i], -1f, 1f) * 32767f;
                shortSamples[i] = (short)sample;
            }

            // --- FORMAT SPECIFIC WRITING ---
            if (_format == AudioFormat.Opus && _opusWriter != null && _opusFrameBuffer != null)
            {
                // Slice the raw arbitrary-sized audio chunk into perfect Opus-sized frames
                int sourceIndex = 0;
                int remaining = samples.Length;

                while (remaining > 0)
                {
                    int spaceInFrame = _opusFrameSize - _opusBufferCount;
                    int toCopy = Math.Min(remaining, spaceInFrame);

                    Array.Copy(shortSamples, sourceIndex, _opusFrameBuffer, _opusBufferCount, toCopy);

                    _opusBufferCount += toCopy;
                    sourceIndex += toCopy;
                    remaining -= toCopy;

                    // Once the micro-buffer is full (exactly 20ms), push it to the Ogg stream
                    if (_opusBufferCount == _opusFrameSize)
                    {
                        _opusWriter.WriteSamples(_opusFrameBuffer, 0, _opusFrameSize);
                        _opusBufferCount = 0;
                    }
                }
            }
            else if (_audioWriter != null)
            {
                // For MP3, WAV, and PCM, we write bytes directly to the underlying stream
                int requiredBytes = samples.Length * 2; // 1 short = 2 bytes
                byte[] buffer = ArrayPool<byte>.Shared.Rent(requiredBytes);
                try
                {
                    Buffer.BlockCopy(shortSamples, 0, buffer, 0, requiredBytes);
                    _audioWriter.Write(buffer, 0, requiredBytes);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        finally
        {
            ArrayPool<short>.Shared.Return(shortSamples);
        }
    }

    /// <summary>
    /// Flushes any remaining audio data in the Opus micro-buffer.
    /// Since Opus requires exact frame sizes, incomplete frames are padded with silence.
    /// </summary>
    private void FlushOpusLeftovers()
    {
        if (_format == AudioFormat.Opus && _opusWriter != null && _opusFrameBuffer != null)
        {
            if (_opusBufferCount > 0)
            {
                // Pad the remaining buffer space with silence (zeros) to complete the final frame
                Array.Clear(_opusFrameBuffer, _opusBufferCount, _opusFrameSize - _opusBufferCount);
                _opusWriter.WriteSamples(_opusFrameBuffer, 0, _opusFrameSize);
                _opusBufferCount = 0;
            }
            _opusWriter.Finish();
        }
    }

    /// <summary>
    /// Finalizes the underlying writer/encoder exactly once: flushes any pending Opus
    /// frame, disposes the format-specific writer (MP3/WAV — required to finalize
    /// compression headers and footers; skipped for PCM, which has no header to close).
    ///
    /// This is the single finalization path shared by both GetFinalAudioBytes() and
    /// Dispose(). Previously each had its own copy of this logic, and Dispose() skipped
    /// it entirely for memory-stream requests — meaning a memory-stream caller who hit
    /// an exception before explicitly calling GetFinalAudioBytes() would leave the MP3/WAV
    /// writer never finalized. Routing both call sites through one idempotent method closes
    /// that gap without changing behavior for any caller that already finalizes correctly.
    /// </summary>
    private void EnsureFinalized()
    {
        if (_finalized) return;
        _finalized = true;

        FlushOpusLeftovers();
        if (_format != AudioFormat.Pcm) _audioWriter?.Dispose();
    }

    /// <summary>
    /// Finalizes encoding and returns the complete audio file as a byte array.
    /// Only valid for non-streaming (in-memory) requests.
    /// Safe to call at most meaningfully once; a second call returns the same bytes
    /// without re-finalizing (EnsureFinalized is idempotent).
    /// </summary>
    public byte[] GetFinalAudioBytes()
    {
        if (!_isMemoryStream)
            throw new InvalidOperationException("Cannot get byte array in streaming mode.");

        EnsureFinalized();

        return ((MemoryStream)_baseStream).ToArray();
    }

    public static string GetMimeType(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.Mp3 => "audio/mpeg",
            AudioFormat.Opus => "audio/ogg",
            AudioFormat.Pcm => "audio/pcm",
            _ => "audio/wav" // Fallback (AudioFormat.Wav)
        };
    }

    public static string GetFileName(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.Mp3 => "speech.mp3",
            AudioFormat.Opus => "speech.ogg",
            AudioFormat.Pcm => "speech.pcm",
            _ => "speech.wav" // Fallback (AudioFormat.Wav)
        };
    }

    /// <summary>
    /// Disposes the underlying writers to ensure all file headers and footers are finalized properly.
    ///
    /// Unconditionally routes through EnsureFinalized() for BOTH streaming and in-memory requests.
    /// Previously this returned immediately for memory streams (relying entirely on an
    /// explicit GetFinalAudioBytes() call elsewhere) — if that call was skipped due to an
    /// exception thrown earlier in the request, the MP3/WAV writer was never finalized.
    /// EnsureFinalized()'s _finalized guard makes this safe to call after GetFinalAudioBytes()
    /// has already run: it becomes a no-op rather than a double-dispose.
    /// </summary>
    public void Dispose()
    {
        EnsureFinalized();
        GC.SuppressFinalize(this);
    }
}