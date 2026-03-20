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
    private readonly Stream? _audioWriter;
    private readonly OpusOggWriteStream? _opusWriter;
    private readonly MemoryStream? _memoryStream;
    private readonly string _format;

    public AudioStreamManager(OpenAiSpeechRequest request, int sampleRate)
    {
        _format = request.ResponseFormat.ToLower();
        _memoryStream = new MemoryStream();

        if (_format == "mp3")
        {
            var waveFormat = new WaveFormat(sampleRate, 16, 1);
            _audioWriter = new LameMP3FileWriter(_memoryStream, waveFormat, LAMEPreset.VBR_90);
        }
        else if (_format == "wav")
        {
            var waveFormat = new WaveFormat(sampleRate, 16, 1);
            _audioWriter = new WaveFileWriter(_memoryStream, waveFormat);
        }
        else if (_format == "opus")
        {
            var encoder = OpusCodecFactory.CreateEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            // Ogg контейнер для Opus
            _opusWriter = new OpusOggWriteStream(encoder, _memoryStream);
        }
        else // "pcm"
        {
            _audioWriter = _memoryStream;
        }
    }

    public void WriteChunk(float[] samples, NAudio.Dsp.BiQuadFilter? filter = null)
    {
        if (filter != null)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] = filter.Transform(samples[i]);
        }

        short[] shortSamples = ArrayPool<short>.Shared.Rent(samples.Length);
        try
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float sample = Math.Clamp(samples[i], -1f, 1f) * 32767f;
                shortSamples[i] = (short)sample;
            }

            if (_format == "opus" && _opusWriter != null)
            {
                _opusWriter.WriteSamples(shortSamples, 0, samples.Length);
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

    public byte[] GetFinalAudioBytes()
    {
        if (_memoryStream == null)
            throw new InvalidOperationException("Cannot get byte array in streaming mode.");

        if (_format == "opus")
            _opusWriter?.Finish();
        else if (_format != "pcm")
            _audioWriter?.Dispose();

        return _memoryStream.ToArray();
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
        _audioWriter?.Dispose();
        _memoryStream?.Dispose();
        GC.SuppressFinalize(this);
    }
}