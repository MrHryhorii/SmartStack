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
    private readonly string _format;
    private readonly bool _isMemoryStream;

    // --- OPUS MICRO-BUFFER VARIABLES ---
    // Opus encoding requires strictly sized frames (e.g., 20ms) to work correctly.
    private readonly short[]? _opusFrameBuffer;
    private readonly int _opusFrameSize;
    private int _opusBufferCount = 0;

    public AudioStreamManager(OpenAiSpeechRequest request, int sampleRate, Stream targetStream)
    {
        _format = request.ResponseFormat.ToLower();
        _baseStream = targetStream;
        _isMemoryStream = targetStream is MemoryStream;

        if (_format == "mp3")
        {
            var waveFormat = new WaveFormat(sampleRate, 16, 1);
            // 128 kbps is a standard, high-quality bitrate for voice audio
            _audioWriter = new LameMP3FileWriter(_baseStream, waveFormat, 128);
        }
        else if (_format == "wav")
        {
            var waveFormat = new WaveFormat(sampleRate, 16, 1);
            _audioWriter = new WaveFileWriter(_baseStream, waveFormat);
        }
        else if (_format == "opus")
        {
            // VoIP application profile is highly optimized for human speech encoding
            var encoder = OpusCodecFactory.CreateEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            _opusWriter = new OpusOggWriteStream(encoder, _baseStream);

            // Opus strictly requires exact 20 millisecond frames (e.g., 960 samples for 48kHz).
            // We calculate the exact frame size based on the target sample rate.
            _opusFrameSize = sampleRate / 50;
            _opusFrameBuffer = new short[_opusFrameSize];
        }
        else // "pcm" (Raw uncompressed 16-bit audio)
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
            if (_format == "opus" && _opusWriter != null && _opusFrameBuffer != null)
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
        if (_format == "opus" && _opusWriter != null && _opusFrameBuffer != null)
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
    /// Finalizes encoding and returns the complete audio file as a byte array.
    /// Only valid for non-streaming (in-memory) requests.
    /// </summary>
    public byte[] GetFinalAudioBytes()
    {
        if (!_isMemoryStream)
            throw new InvalidOperationException("Cannot get byte array in streaming mode.");

        FlushOpusLeftovers();
        if (_format != "pcm") _audioWriter?.Dispose();

        return ((MemoryStream)_baseStream).ToArray();
    }

    public static string GetMimeType(OpenAiSpeechRequest request)
    {
        return request.ResponseFormat.ToLower() switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/ogg",
            "pcm" => "audio/pcm",
            _ => "audio/wav"
        };
    }

    public static string GetFileName(OpenAiSpeechRequest request)
    {
        return request.ResponseFormat.ToLower() switch
        {
            "mp3" => "speech.mp3",
            "opus" => "speech.ogg",
            "pcm" => "speech.pcm",
            _ => "speech.wav"
        };
    }

    /// <summary>
    /// Disposes the underlying writers to ensure all file headers and footers are finalized properly.
    /// </summary>
    public void Dispose()
    {
        // Memory streams are handled by the ASP.NET Core framework, we only dispose writers for network streams
        if (_isMemoryStream) return;

        FlushOpusLeftovers();
        if (_format != "pcm") _audioWriter?.Dispose();

        GC.SuppressFinalize(this);
    }
}