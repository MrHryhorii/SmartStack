using NAudio.Wave;
using NAudio.Lame;
using ONNX_Runner.Models;
using System.Buffers;

namespace ONNX_Runner.Services;

public class AudioStreamManager : IDisposable
{
    private readonly Stream _audioWriter;
    private readonly MemoryStream? _memoryStream; // Використовується тільки якщо НЕ стрімінг
    private readonly bool _isMp3;

    public AudioStreamManager(OpenAiSpeechRequest request, int sampleRate)
    {
        _isMp3 = request.ResponseFormat.Equals("mp3", StringComparison.OrdinalIgnoreCase);

        // TODO: Пізніше тут буде перевірка на request.Stream для прямої передачі в HttpContext.Response.Body
        // Наразі (Batch mode) ми завжди пишемо в MemoryStream
        _memoryStream = new MemoryStream();

        var waveFormat = new WaveFormat(sampleRate, 16, 1);

        if (_isMp3)
        {
            _audioWriter = new LameMP3FileWriter(_memoryStream, waveFormat, LAMEPreset.VBR_90);
        }
        else
        {
            _audioWriter = new WaveFileWriter(_memoryStream, waveFormat);
        }
    }

    /// <summary>
    /// Конвертує сирі float-семпли в 16-bit PCM байти і пише їх у вибраний формат.
    /// </summary>
    public void WriteChunk(float[] samples, NAudio.Dsp.BiQuadFilter? filter = null)
    {
        // Якщо є фільтр, застосовуємо його до масиву
        if (filter != null)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = filter.Transform(samples[i]);
            }
        }

        int requiredBytes = samples.Length * 2;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(requiredBytes);
        try
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float sample = Math.Clamp(samples[i], -1f, 1f) * 32767f;
                short shortSample = (short)sample;
                buffer[i * 2] = (byte)(shortSample & 0xFF);
                buffer[i * 2 + 1] = (byte)((shortSample >> 8) & 0xFF);
            }

            _audioWriter.Write(buffer, 0, requiredBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Повертає зібрані байти (для Batch режиму).
    /// </summary>
    public byte[] GetFinalAudioBytes()
    {
        if (_memoryStream == null)
            throw new InvalidOperationException("Cannot get byte array in streaming mode.");

        // Спочатку закриваємо Writer, щоб він дописав необхідні теги/розмір у потік
        _audioWriter.Dispose();

        return _memoryStream.ToArray();
    }

    // Статичні методи, які не вимагають створення (new) самого класу
    public static string GetMimeType(OpenAiSpeechRequest request)
    {
        bool isMp3 = request.ResponseFormat.Equals("mp3", StringComparison.OrdinalIgnoreCase);
        return isMp3 ? "audio/mpeg" : "audio/wav";
    }

    public static string GetFileName(OpenAiSpeechRequest request)
    {
        bool isMp3 = request.ResponseFormat.Equals("mp3", StringComparison.OrdinalIgnoreCase);
        return isMp3 ? "speech.mp3" : "speech.wav";
    }

    public void Dispose()
    {
        _audioWriter?.Dispose();
        _memoryStream?.Dispose();

        GC.SuppressFinalize(this);
    }
}