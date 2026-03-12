using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services
{
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

        public byte[] SynthesizeAudio(string phonemes, float speed = 1.0f, float? requestNoiseScale = null, float? requestNoiseW = null)
        {
            // Викликаємо метод конвертації фонем у цифри
            long[] phonemeIds = _phonemizer.PhonemesToIds(phonemes);

            var inputTensor = new DenseTensor<long>(phonemeIds, [1, phonemeIds.Length]);
            var inputLengthsTensor = new DenseTensor<long>(new[] { (long)phonemeIds.Length }, [1]);

            // --- ЗАХИСТ ВІД ДУРНЯ ТА ДЕФОЛТНІ ЗНАЧЕННЯ ---
            float safeSpeed = Math.Clamp(speed, 0.1f, 10.0f);
            float targetLengthScale = _config.Inference.LengthScale / safeSpeed;

            float safeNoiseScale = requestNoiseScale.HasValue
                ? Math.Clamp(requestNoiseScale.Value, 0.0f, 5.0f)
                : _config.Inference.NoiseScale;

            float safeNoiseW = requestNoiseW.HasValue
                ? Math.Clamp(requestNoiseW.Value, 0.0f, 5.0f)
                : _config.Inference.NoiseW;

            var scalesTensor = new DenseTensor<float>(new[] {
                safeNoiseScale,
                targetLengthScale,
                safeNoiseW
            }, [3]);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor),
                NamedOnnxValue.CreateFromTensor("input_lengths", inputLengthsTensor),
                NamedOnnxValue.CreateFromTensor("scales", scalesTensor)
            };

            using var results = _session.Run(inputs);
            var audioOutput = results.First(r => r.Name == "output").AsEnumerable<float>().ToArray();

            return ConvertToWav(audioOutput);
        }

        private byte[] ConvertToWav(float[] audioSamples)
        {
            using var memoryStream = new MemoryStream();
            var waveFormat = new WaveFormat(_config.Audio.SampleRate, 16, 1);

            using (var writer = new WaveFileWriter(memoryStream, waveFormat))
            {
                // Рахуємо точну кількість потрібних байтів
                int requiredBytes = audioSamples.Length * 2;

                // ОРЕНДУЄМО МАСИВ З ПУЛУ ЗАМІСТЬ СТВОРЕННЯ НОВОГО!
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(requiredBytes);

                try
                {
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

                    // ВАЖЛИВО: Використовуємо requiredBytes, а не buffer.Length!
                    // Тому що ArrayPool може видати масив БІЛЬШОГО розміру, ніж ми просили.
                    writer.Write(buffer, 0, requiredBytes);
                }
                finally
                {
                    // ОБОВ'ЯЗКОВО ПОВЕРТАЄМО МАСИВ У ПУЛ, НАВІТЬ ЯКЩО СТАЛАСЯ ПОМИЛКА
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            return memoryStream.ToArray();
        }

        public void Dispose()
        {
            _session?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}