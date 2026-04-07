using NAudio.Wave;
using NAudio.Lame;
using ONNX_Runner.Models;
using System.Buffers;
using Concentus.Oggfile;
using Concentus;
using Concentus.Enums;

namespace ONNX_Runner.Services;

public class AudioStreamManager : IDisposable
{
    private readonly Stream _baseStream;
    private readonly Stream? _audioWriter;
    private readonly OpusOggWriteStream? _opusWriter;
    private readonly string _format;
    private readonly bool _isMemoryStream;

    // --- ЗМІННІ ДЛЯ МІКРО-БУФЕРА OPUS ---
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
            _audioWriter = new LameMP3FileWriter(_baseStream, waveFormat, 128);
        }
        else if (_format == "wav")
        {
            var waveFormat = new WaveFormat(sampleRate, 16, 1);
            _audioWriter = new WaveFileWriter(_baseStream, waveFormat);
        }
        else if (_format == "opus")
        {
            var encoder = OpusCodecFactory.CreateEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            _opusWriter = new OpusOggWriteStream(encoder, _baseStream);

            // ВИПРАВЛЕННЯ 2: Opus вимагає фрейми рівно по 20 мілісекунд (напр. 960 семплів для 48kHz)
            _opusFrameSize = sampleRate / 50;
            _opusFrameBuffer = new short[_opusFrameSize];
        }
        else // "pcm"
        {
            _audioWriter = _baseStream;
        }
    }

    public void WriteChunk(Span<float> samples, NAudio.Dsp.BiQuadFilter? filter = null)
    {
        if (filter != null)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] = filter.Transform(samples[i]);
        }

        short[] shortSamples = ArrayPool<short>.Shared.Rent(samples.Length);
        try
        {
            // SIMD ВЕКТОРИЗАЦІЯ
            int vectorSize = System.Numerics.Vector<float>.Count;
            int i = 0;
            var minVec = new System.Numerics.Vector<float>(-1f);
            var maxVec = new System.Numerics.Vector<float>(1f);
            var multVec = new System.Numerics.Vector<float>(32767f);

            for (; i <= samples.Length - vectorSize; i += vectorSize)
            {
                var vSamples = new System.Numerics.Vector<float>(samples[i..]);
                var vClamped = System.Numerics.Vector.Max(minVec, System.Numerics.Vector.Min(maxVec, vSamples));
                var vScaled = vClamped * multVec;
                for (int k = 0; k < vectorSize; k++) shortSamples[i + k] = (short)vScaled[k];
            }
            for (; i < samples.Length; i++)
            {
                float sample = Math.Clamp(samples[i], -1f, 1f) * 32767f;
                shortSamples[i] = (short)sample;
            }

            // ЗАПИС У ФОРМАТ
            if (_format == "opus" && _opusWriter != null && _opusFrameBuffer != null)
            {
                // Нарізаємо сирий звук на ідеальні Opus-фрейми
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

                    if (_opusBufferCount == _opusFrameSize)
                    {
                        _opusWriter.WriteSamples(_opusFrameBuffer, 0, _opusFrameSize);
                        _opusBufferCount = 0;
                    }
                }
            }
            else if (_audioWriter != null)
            {
                int requiredBytes = samples.Length * 2;
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

    private void FlushOpusLeftovers()
    {
        if (_format == "opus" && _opusWriter != null && _opusFrameBuffer != null)
        {
            if (_opusBufferCount > 0)
            {
                // Заповнюємо залишки тишею (нулями), щоб добити фрейм до кінця
                Array.Clear(_opusFrameBuffer, _opusBufferCount, _opusFrameSize - _opusBufferCount);
                _opusWriter.WriteSamples(_opusFrameBuffer, 0, _opusFrameSize);
                _opusBufferCount = 0;
            }
            _opusWriter.Finish();
        }
    }

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

    public void Dispose()
    {
        if (_isMemoryStream) return;

        FlushOpusLeftovers();
        if (_format != "pcm") _audioWriter?.Dispose();

        GC.SuppressFinalize(this);
    }
}