using System.Buffers;
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

    public int GetTargetSamplingRate() => _config.Data.SamplingRate;

    public OpenVoiceRunner(string extractPath, string colorPath, ToneConfig config, OnnxSettings onnxSettings)
    {
        _config = config;
        (_extractSession, _colorSession) = InitializeSessions(extractPath, colorPath, onnxSettings);
        PrintModelMetadata();
    }

    private static (InferenceSession, InferenceSession) InitializeSessions(string extractPath, string colorPath, OnnxSettings onnxSettings)
    {
        int maxGpusToTry = 4;

        for (int deviceId = 0; deviceId < maxGpusToTry; deviceId++)
        {
            try
            {
                var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
                onnxSettings.ApplyTo(options); // ЗАСТОСУВАННЯ ТЮНІНГУ
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

        // Фолбек на CPU
        var cpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
        onnxSettings.ApplyTo(cpuOptions); // ЗАСТОСУВАННЯ ТЮНІНГУ НА CPU

        var cpuExtract = new InferenceSession(extractPath, cpuOptions);
        var cpuColor = new InferenceSession(colorPath, cpuOptions);
        return (cpuExtract, cpuColor);
    }

    // --- МЕТОДИ ДЛЯ ЕКСТРАКЦІЇ ТА РОБОТИ ЗІ ЗЛІПКАМИ ---

    public float[] ExtractToneColor(float[,] spectrogram)
    {
        if (_extractSession == null)
            throw new InvalidOperationException("Tone Extractor has been unloaded from memory.");

        int frames = spectrogram.GetLength(0);
        int bins = spectrogram.GetLength(1); // Має бути 513
        int tensorSize = frames * bins;

        // ОРЕНДУЄМО МАСИВ ДЛЯ ВХІДНОГО ТЕНЗОРА
        float[] rentedInput = ArrayPool<float>.Shared.Rent(tensorSize);
        try
        {
            // Змушуємо тензор працювати поверх орендованої пам'яті (Zero Allocation)
            var memory = new Memory<float>(rentedInput, 0, tensorSize);
            var inputTensor = new DenseTensor<float>(memory, [1, frames, bins]);

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
            // Зліпок - це лише 256 чисел (1 КБ).
            return [.. results.First(r => r.Name == "tone_embedding").AsEnumerable<float>()];
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedInput);
        }
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

    public (float[] Buffer, int Length) ApplyToneColor(float[,] spectrogram, float[] srcFingerprint, float[] destFingerprint, float tau = 1.0f)
    {
        int frames = spectrogram.GetLength(0);
        int bins = spectrogram.GetLength(1);
        int channels = _config.Model.GinChannels;
        int tensorSize = frames * bins;

        // ОРЕНДУЄМО МАСИВ ДЛЯ ВХІДНОГО ТЕНЗОРА
        float[] rentedInput = ArrayPool<float>.Shared.Rent(tensorSize);
        try
        {
            var memory = new Memory<float>(rentedInput, 0, tensorSize);
            var audioTensor = new DenseTensor<float>(memory, [1, bins, frames]);

            for (int i = 0; i < frames; i++)
            {
                for (int j = 0; j < bins; j++)
                {
                    audioTensor[0, j, i] = spectrogram[i, j];
                }
            }

            var srcTensor = new DenseTensor<float>(srcFingerprint, [1, channels, 1]);
            var destTensor = new DenseTensor<float>(destFingerprint, [1, channels, 1]);
            var lengthTensor = new DenseTensor<long>(new[] { (long)frames }, [1]);
            var tauTensor = new DenseTensor<float>(new[] { tau }, [1]);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("audio", audioTensor),
                NamedOnnxValue.CreateFromTensor("audio_length", lengthTensor),
                NamedOnnxValue.CreateFromTensor("src_tone", srcTensor),
                NamedOnnxValue.CreateFromTensor("dest_tone", destTensor),
                NamedOnnxValue.CreateFromTensor("tau", tauTensor)
            };

            using var results = _colorSession.Run(inputs);

            // ВИТЯГУЄМО РЕЗУЛЬТАТ В ОРЕНДОВАНИЙ МАСИВ
            var outputNode = results.First(r => r.Name == "converted_audio");
            var outputTensor = outputNode.AsTensor<float>();

            int outLength = (int)outputTensor.Length;
            float[] outBuffer = ArrayPool<float>.Shared.Rent(outLength);

            if (outputTensor is DenseTensor<float> denseTensor)
            {
                denseTensor.Buffer.Span.CopyTo(outBuffer);
            }
            else
            {
                int index = 0;
                foreach (var val in outputTensor) outBuffer[index++] = val;
            }

            return (outBuffer, outLength);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedInput);
        }
    }

    public void Dispose()
    {
        _extractSession?.Dispose();
        _colorSession?.Dispose();
        GC.SuppressFinalize(this);
    }
}