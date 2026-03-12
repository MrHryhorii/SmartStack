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
        private readonly PhonemeChunker _chunker;

        public PiperRunner(string modelPath, PiperConfig config, IPhonemizer phonemizer, PhonemeChunker chunker)
        {
            _config = config;
            _phonemizer = phonemizer;
            _chunker = chunker;

            _session = InitializeSession(modelPath);
        }

        private InferenceSession InitializeSession(string modelPath)
        {
            int maxGpusToTry = 4; // Максимальна кількість відеокарт у системі для перевірки

            // СПРОБА ЗАВАНТАЖИТИ НА ВІДЕОКАРТУ (GPU)
            for (int deviceId = 0; deviceId < maxGpusToTry; deviceId++)
            {
                try
                {
                    var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
                    options.AppendExecutionProvider_DML(deviceId);

                    var session = new InferenceSession(modelPath, options);

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"[HARDWARE] Piper Model loaded successfully on GPU (DirectML, Device ID: {deviceId})");
                    Console.ResetColor();

                    return session; // Успіх! Миттєво повертаємо готову сесію
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DEBUG] Failed to load on GPU {deviceId}: {ex.Message}. Trying next...");
                }
            }

            // ФОЛБЕК НА ПРОЦЕСОР (CPU)
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[WARNING] All GPU attempts failed or no GPU found. Falling back to CPU...");
            Console.ResetColor();

            var cpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
            var fallbackSession = new InferenceSession(modelPath, cpuOptions);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("[HARDWARE] Piper Model loaded successfully on CPU.");
            Console.ResetColor();

            return fallbackSession; // Повертаємо сесію на процесорі
        }

        public byte[] SynthesizeAudio(string phonemes, float speed = 1.0f, float? requestNoiseScale = null, float? requestNoiseW = null)
        {
            // РІЖЕМО ГІГАНТСЬКИЙ РЯДОК НА БЕЗПЕЧНІ ЧАНКИ
            var chunks = _chunker.SplitIntoChunks(phonemes);

            // Тут ми будемо накопичувати СИРІ ЗВУКОВІ ХВИЛІ зі всіх речень
            var allAudioSamples = new List<float>();

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

            // ПРОГАНЯЄМО КОЖЕН ЧАНК ЧЕРЕЗ НЕЙРОМЕРЕЖУ
            foreach (var chunk in chunks)
            {
                // Викликаємо метод конвертації фонем у цифри для конкретного шматка
                long[] phonemeIds = _phonemizer.PhonemesToIds(chunk);

                var inputTensor = new DenseTensor<long>(phonemeIds, [1, phonemeIds.Length]);
                var inputLengthsTensor = new DenseTensor<long>(new[] { (long)phonemeIds.Length }, [1]);

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input", inputTensor),
                    NamedOnnxValue.CreateFromTensor("input_lengths", inputLengthsTensor),
                    NamedOnnxValue.CreateFromTensor("scales", scalesTensor)
                };

                using var results = _session.Run(inputs);
                var audioOutput = results.First(r => r.Name == "output").AsEnumerable<float>().ToArray();

                // Додаємо згенеровані хвилі цього речення до загальної "бобіни"
                allAudioSamples.AddRange(audioOutput);
            }

            // КОНВЕРТУЄМО ВСЕ РАЗОМ В ОДИН WAV ФАЙЛ
            // Використовуємо [.. allAudioSamples] - це синтаксичний цукор C# 12 для .ToArray()
            return ConvertToWav([.. allAudioSamples]);
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