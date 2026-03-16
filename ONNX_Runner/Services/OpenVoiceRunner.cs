using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

public class OpenVoiceRunner : IDisposable
{
    private InferenceSession? _extractSession; // Робимо nullable, щоб можна було вивантажити
    private readonly InferenceSession _colorSession;
    private readonly ToneConfig _config;

    // Словник: Ключ - назва голосу (напр. "MorganFreeman"), Значення - зліпок (256 чисел)
    public Dictionary<string, float[]> VoiceLibrary { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly float[] memoryArray = [1.0f];
    public int GetTargetSamplingRate() => _config.Data.SamplingRate;

    public OpenVoiceRunner(string extractPath, string colorPath, ToneConfig config)
    {
        _config = config;

        // Викликаємо універсальний метод завантаження
        (_extractSession, _colorSession) = InitializeSessions(extractPath, colorPath);

        PrintModelMetadata();
    }

    private static (InferenceSession, InferenceSession) InitializeSessions(string extractPath, string colorPath)
    {
        int maxGpusToTry = 4;

        for (int deviceId = 0; deviceId < maxGpusToTry; deviceId++)
        {
            try
            {
                var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
                options.AppendExecutionProvider_DML(deviceId);

                var extract = new InferenceSession(extractPath, options);
                var color = new InferenceSession(colorPath, options);

                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[HARDWARE] OpenVoice Models loaded on GPU (DirectML, Device ID: {deviceId})");
                Console.ResetColor();

                return (extract, color);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] OpenVoice failed on GPU {deviceId}: {ex.Message}");
            }
        }

        var cpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
        var cpuExtract = new InferenceSession(extractPath, cpuOptions);
        var cpuColor = new InferenceSession(colorPath, cpuOptions);

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("[HARDWARE] OpenVoice Models loaded on CPU.");
        Console.ResetColor();

        return (cpuExtract, cpuColor);
    }

    // --- МЕТОДИ ДЛЯ ЕКСТРАКЦІЇ ТА РОБОТИ ЗІ ЗЛІПКАМИ ---

    public float[] ExtractToneColor(float[,] spectrogram)
    {
        if (_extractSession == null)
            throw new InvalidOperationException("Tone Extractor has been unloaded from memory.");

        int frames = spectrogram.GetLength(0);
        int bins = spectrogram.GetLength(1); // Має бути 513

        // Створюємо тензор [1 x Batch x 513] згідно з логами інспекції 
        var inputTensor = new DenseTensor<float>([1, frames, bins]);
        for (int i = 0; i < frames; i++)
        {
            for (int j = 0; j < bins; j++)
            {
                inputTensor[0, i, j] = spectrogram[i, j];
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor)
        };

        using var results = _extractSession.Run(inputs);
        // Вихід моделі називається "tone_embedding" 
        return results.First(r => r.Name == "tone_embedding").AsEnumerable<float>().ToArray();
    }

    public void SaveVoiceFingerprint(string path, float[] embedding)
    {
        byte[] result = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, result, 0, result.Length);
        File.WriteAllBytes(path, result);
    }

    public float[] LoadVoiceFingerprint(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        float[] embedding = new float[data.Length / sizeof(float)];
        Buffer.BlockCopy(data, 0, embedding, 0, data.Length);
        return embedding;
    }

    public void UnloadExtractor()
    {
        _extractSession?.Dispose();
        _extractSession = null;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("[INFO] OpenVoice Tone Extractor has been unloaded from memory to save VRAM.");
        Console.ResetColor();
    }

    // --- ІНСПЕКЦІЯ ТА ДИСПРОВЕР ---

    private void PrintModelMetadata()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("\n=========================================");
        Console.WriteLine("       OPENVOICE MODELS INSPECTION       ");
        Console.WriteLine("=========================================");
        Console.ResetColor();

        Console.WriteLine($">>> CONFIG SETTINGS:");
        Console.WriteLine($"  Target Sample Rate: {_config.Data.SamplingRate} Hz");
        Console.WriteLine($"  Filter Length:      {_config.Data.FilterLength}");
        Console.WriteLine($"  Hop Length:         {_config.Data.HopLength}");
        Console.WriteLine($"  Gin Channels:       {_config.Model.GinChannels}");

        if (_extractSession != null) InspectSession("TONE EXTRACTOR", _extractSession);
        InspectSession("TONE COLOR CONVERTER", _colorSession);
    }

    private static void InspectSession(string name, InferenceSession session)
    {
        Console.WriteLine($"\n>>> {name}:");
        Console.WriteLine("  Inputs:");
        foreach (var input in session.InputMetadata)
        {
            var shape = string.Join(" x ", input.Value.Dimensions.Select(d => d == -1 ? "Batch" : d.ToString()));
            Console.WriteLine($"    - {input.Key}: [{shape}] ({input.Value.ElementType})");
        }

        Console.WriteLine("  Outputs:");
        foreach (var output in session.OutputMetadata)
        {
            var shape = string.Join(" x ", output.Value.Dimensions.Select(d => d == -1 ? "Batch" : d.ToString()));
            Console.WriteLine($"    - {output.Key}: [{shape}] ({output.Value.ElementType})");
        }
    }

    public float[] ApplyToneColor(float[,] spectrogram, float[] srcFingerprint, float[] destFingerprint)
    {
        // Динамічно отримуємо параметри з конфігурації та вхідних даних
        int frames = spectrogram.GetLength(0);
        int bins = spectrogram.GetLength(1);
        int channels = _config.Model.GinChannels; // Використовуємо 256 з конфігу

        // Готуємо вхідну спектрограму [1 x Bins x Frames]
        // На основі інспекції: [1 x 513 x Batch]
        var audioTensor = new DenseTensor<float>([1, bins, frames]);
        for (int i = 0; i < frames; i++)
        {
            for (int j = 0; j < bins; j++)
            {
                // Переносимо дані в формат, який очікує ONNX (Bins як друга розмірність)
                audioTensor[0, j, i] = spectrogram[i, j];
            }
        }

        // Готуємо тензори зліпків [1 x Channels x 1]
        var srcTensor = new DenseTensor<float>(srcFingerprint, [1, channels, 1]);
        var destTensor = new DenseTensor<float>(destFingerprint, [1, channels, 1]);

        // Додаткові параметри
        var lengthTensor = new DenseTensor<long>(new[] { (long)frames }, [1]);

        // Tau — це коефіцієнт інтенсивності перетворення (зазвичай 1.0)
        var tauTensor = new DenseTensor<float>(memoryArray, [1]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("audio", audioTensor),
            NamedOnnxValue.CreateFromTensor("audio_length", lengthTensor),
            NamedOnnxValue.CreateFromTensor("src_tone", srcTensor),
            NamedOnnxValue.CreateFromTensor("dest_tone", destTensor),
            NamedOnnxValue.CreateFromTensor("tau", tauTensor)
        };

        using var results = _colorSession.Run(inputs);

        // Вихід моделі: [1 x 1 x Batch]
        return [.. results.First(r => r.Name == "converted_audio").AsEnumerable<float>()];
    }

    // Розумна обробка довгих аудіо по шматочках
    public float[] ApplyToneColorInChunks(float[] originalSamples, AudioProcessor audioProc, float[] srcFingerprint, float[] destFingerprint, int chunkSeconds)
    {
        var convertedSamplesList = new List<float>(originalSamples.Length);

        // Динамічно рахуємо розмір шматка (Час * Частота з конфігу моделі)
        int maxAudioChunkSize = chunkSeconds * _config.Data.SamplingRate;

        // Межа, до якої відступаємо (3 секунди назад)
        int minSearchSize = Math.Max(1000, maxAudioChunkSize - (_config.Data.SamplingRate * 3));

        int currentIndex = 0;
        while (currentIndex < originalSamples.Length)
        {
            int remaining = originalSamples.Length - currentIndex;
            int currentChunkSize = Math.Min(maxAudioChunkSize, remaining);

            // Шукаємо "тишу" для ідеального розрізу
            if (currentChunkSize == maxAudioChunkSize)
            {
                for (int i = currentChunkSize - 1; i > minSearchSize; i--)
                {
                    if (Math.Abs(originalSamples[currentIndex + i]) < 0.005f)
                    {
                        currentChunkSize = i + 1;
                        break;
                    }
                }
            }

            // Вирізаємо шматок
            float[] audioChunk = new float[currentChunkSize];
            Array.Copy(originalSamples, currentIndex, audioChunk, 0, currentChunkSize);

            // Перетворюємо у спектрограму і клонуємо голос
            var specChunk = audioProc.GetMagnitudeSpectrogram(audioChunk);
            if (specChunk.GetLength(0) > 0)
            {
                float[] convertedChunk = ApplyToneColor(specChunk, srcFingerprint, destFingerprint);
                convertedSamplesList.AddRange(convertedChunk);
            }

            currentIndex += currentChunkSize;
        }

        return convertedSamplesList.ToArray();
    }

    public void Dispose()
    {
        _extractSession?.Dispose();
        _colorSession?.Dispose();
        GC.SuppressFinalize(this);
    }
}