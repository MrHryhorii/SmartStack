using Microsoft.ML.OnnxRuntime;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

public class OpenVoiceRunner : IDisposable
{
    private readonly InferenceSession _extractSession;
    private readonly InferenceSession _colorSession;
    private readonly ToneConfig _config;

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

        // СПРОБА 1: Шукаємо GPU (DirectML)
        for (int deviceId = 0; deviceId < maxGpusToTry; deviceId++)
        {
            try
            {
                var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
                options.AppendExecutionProvider_DML(deviceId);

                // Намагаємося створити ОБИДВІ сесії на цьому пристрої
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

        // СПРОБА 2: Фолбек на CPU
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("[WARNING] OpenVoice falling back to CPU...");
        Console.ResetColor();

        var cpuOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
        var cpuExtract = new InferenceSession(extractPath, cpuOptions);
        var cpuColor = new InferenceSession(colorPath, cpuOptions);

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("[HARDWARE] OpenVoice Models loaded on CPU.");
        Console.ResetColor();

        return (cpuExtract, cpuColor);
    }

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

        InspectSession("TONE EXTRACTOR", _extractSession);
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

    public void Dispose()
    {
        _extractSession?.Dispose();
        _colorSession?.Dispose();
        GC.SuppressFinalize(this);
    }
}