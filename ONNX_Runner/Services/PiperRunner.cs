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

        // ДОДАЄМО ПУБЛІЧНУ ВЛАСТИВІСТЬ
        public bool IsUsingGPU { get; private set; }

        public PiperRunner(string modelPath, PiperConfig config, IPhonemizer phonemizer, PhonemeChunker chunker)
        {
            _phonemizer = phonemizer;
            _config = config;
            _chunker = chunker;

            // Змінюємо виклик, щоб отримати і сесію, і статус GPU
            var (session, isGpu) = InitializeSession(modelPath);
            _session = session;
            IsUsingGPU = isGpu;
        }

        // Змінюємо повернений тип на Tuple (InferenceSession, bool)
        private static (InferenceSession, bool) InitializeSession(string modelPath)
        {
            int maxGpusToTry = 4;

            for (int deviceId = 0; deviceId < maxGpusToTry; deviceId++)
            {
                try
                {
                    var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
                    options.AppendExecutionProvider_DML(deviceId);

                    var session = new InferenceSession(modelPath, options);
                    Console.WriteLine($"[HARDWARE] Piper Model loaded successfully on GPU (DirectML, Device ID: {deviceId})");
                    return (session, true); // Повертаємо true (працює на GPU)
                }
                catch { /* ігноруємо і пробуємо далі */ }
            }

            var cpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
            var fallbackSession = new InferenceSession(modelPath, cpuOptions);
            Console.WriteLine("[HARDWARE] Piper Model loaded successfully on CPU.");
            return (fallbackSession, false); // Повертаємо false (працює на CPU)
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
            return ConvertToWav([.. allAudioSamples]);
        }

        public byte[] ConvertToWav(float[] audioSamples)
        {
            using var memoryStream = new MemoryStream();
            var waveFormat = new WaveFormat(_config.Audio.SampleRate, 16, 1);

            using (var writer = new WaveFileWriter(memoryStream, waveFormat))
            {
                int requiredBytes = audioSamples.Length * 2;
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(requiredBytes);

                try
                {
                    for (int i = 0; i < audioSamples.Length; i++)
                    {
                        // Звук завжди в межах [-1.0; 1.0], тому просто обмежуємо і масштабуємо
                        float sample = Math.Clamp(audioSamples[i], -1f, 1f) * 32767f;

                        short shortSample = (short)sample;
                        buffer[i * 2] = (byte)(shortSample & 0xFF);
                        buffer[i * 2 + 1] = (byte)((shortSample >> 8) & 0xFF);
                    }

                    writer.Write(buffer, 0, requiredBytes);
                }
                finally
                {
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