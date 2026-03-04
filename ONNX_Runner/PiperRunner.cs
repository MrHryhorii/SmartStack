using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using ONNX_Runner.Models;

namespace ONNX_Runner;

public class PiperRunner : IDisposable
{
    private readonly InferenceSession _session;
    private readonly IPhonemizer _phonemizer;
    private readonly PiperConfig _config;

    public PiperRunner(string modelPath, PiperConfig config, IPhonemizer phonemizer)
    {
        _config = config;
        _phonemizer = phonemizer;

        var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
        // Вмикаємо прискорення на відеокарті через DirectML
        options.AppendExecutionProvider_DML(0);

        _session = new InferenceSession(modelPath, options);
    }

    public byte[] SynthesizeAudio(string text)
    {
        // Отримуємо ID фонем з тексту
        long[] phonemeIds = _phonemizer.TextToPhonemeIds(text);

        // Створюємо тензори для моделі
        var inputTensor = new DenseTensor<long>(phonemeIds, [1, phonemeIds.Length]);
        var inputLengthsTensor = new DenseTensor<long>(new[] { (long)phonemeIds.Length }, [1]);

        var scalesTensor = new DenseTensor<float>(new[] {
            _config.Inference.NoiseScale,
            _config.Inference.LengthScale,
            _config.Inference.NoiseW
        }, [3]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("input_lengths", inputLengthsTensor),
            NamedOnnxValue.CreateFromTensor("scales", scalesTensor)
        };

        // Запускаємо генерацію!
        using var results = _session.Run(inputs);
        var audioOutput = results.First(r => r.Name == "output").AsEnumerable<float>().ToArray();

        // Пакуємо в WAV
        return ConvertToWav(audioOutput);
    }

    private byte[] ConvertToWav(float[] audioSamples)
    {
        using var memoryStream = new MemoryStream();
        var waveFormat = new WaveFormat(_config.Audio.SampleRate, 16, 1);

        using (var writer = new WaveFileWriter(memoryStream, waveFormat))
        {
            // Створюємо буфер для сирих байтів (16-bit = 2 байти на кожен семпл)
            byte[] buffer = new byte[audioSamples.Length * 2];

            for (int i = 0; i < audioSamples.Length; i++)
            {
                // Запобігаємо перевантаженню звуку (clipping)
                float clamped = Math.Clamp(audioSamples[i], -1f, 1f);

                // Конвертуємо float (-1.0 до 1.0) в 16-bit PCM (-32768 до 32767)
                short shortSample = (short)(clamped * short.MaxValue);

                // Записуємо 2 байти (Little Endian формат)
                buffer[i * 2] = (byte)(shortSample & 0xFF);
                buffer[i * 2 + 1] = (byte)((shortSample >> 8) & 0xFF);
            }

            // Записуємо весь буфер у WAV файл одним махом
            writer.Write(buffer, 0, buffer.Length);
        }
        return memoryStream.ToArray();
    }

    public void Dispose()
    {
        _session?.Dispose();

        // Кажемо Garbage Collector не викликати фіналізатор
        GC.SuppressFinalize(this);
    }
}