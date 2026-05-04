using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Whisper.net;
using System.Text;

namespace STT_Runner.Services;

/// <summary>
/// The core engine responsible for audio inference.
/// It utilizes Silero VAD to filter out silence and Whisper.net for Speech-to-Text and Translation.
/// </summary>
public class Transcriptor(IConfiguration config, string whisperPath, string vadPath) : IDisposable
{
    private readonly string _whisperPath = whisperPath;
    private readonly string _vadPath = vadPath;
    private readonly string _defaultLanguage = config["SttSettings:Language"] ?? "auto";

    // Core Whisper Factory (Holds the heavy neural network weights in memory)
    private WhisperFactory? _whisperFactory;

    // Silero VAD component
    private InferenceSession? _vadSession;

    // Silero VAD strict requirements
    private const int SampleRate = 16000;
    private const int WindowSize = 512;
    private const float SpeechThreshold = 0.5f;

    /// <summary>
    /// Loads the heavy neural network models into RAM/VRAM. 
    /// Must be called once during application startup.
    /// </summary>
    public void Initialize()
    {
        Console.WriteLine("[SYSTEM] Initializing Transcriptor Engine...");

#if GPU_SUPPORT
        // =========================================================
        // GPU BUILD LOGIC
        // =========================================================
        var factoryOptions = new WhisperFactoryOptions { UseGpu = true };
        string? gpuIndexStr = config["SttSettings:GpuDeviceIndex"];

        if (int.TryParse(gpuIndexStr, out int targetGpu))
        {
            factoryOptions.GpuDevice = targetGpu;
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[HARDWARE] GPU Acceleration active. Forcing Device Index: {targetGpu}");
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine("[HARDWARE] GPU Acceleration active. Using default Device 0.");
        }
#else
        // =========================================================
        // CPU BUILD LOGIC
        // =========================================================
        var factoryOptions = new WhisperFactoryOptions { UseGpu = false };
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("[HARDWARE] Running in CPU-only mode (GPU support disabled by build target).");
        Console.ResetColor();
#endif

        // Load factory with appropriate options
        _whisperFactory = WhisperFactory.FromPath(_whisperPath, factoryOptions);

        // Initialize VAD session
        var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
        _vadSession = new InferenceSession(_vadPath, sessionOptions);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[SYSTEM] Transcriptor Engine initialized successfully.");
        Console.ResetColor();
    }

    /// <summary>
    /// Processes the audio array, filters silence, and returns text.
    /// Thread-safe: Creates a lightweight WhisperProcessor per request.
    /// </summary>
    /// <param name="audioSamples">16kHz Mono float array.</param>
    /// <param name="languageOverride">Optional ISO language code from the API request.</param>
    /// <param name="isTranslation">If true, translates the audio to English.</param>
    /// <returns>Transcribed or translated text.</returns>
    public async Task<string> ProcessAudioAsync(float[] audioSamples, string? languageOverride = null, bool isTranslation = false)
    {
        if (_whisperFactory == null || _vadSession == null)
            throw new InvalidOperationException("Transcriptor Engine is not initialized.");

        // Fallback logic: 1. API Parameter -> 2. AppSettings -> 3. Auto
        var targetLanguage = !string.IsNullOrWhiteSpace(languageOverride)
                             ? languageOverride
                             : (!string.IsNullOrWhiteSpace(_defaultLanguage) ? _defaultLanguage : "auto");

        var activeSpeechBuffer = new List<float>();

        // ==========================================================
        // SILERO VAD V5 INFERENCE
        // ==========================================================
        var stateTensor = new DenseTensor<float>([2, 1, 128]);
        stateTensor.Fill(0f);
        var srTensor = new DenseTensor<long>(new long[] { SampleRate }, [1]);

        for (int i = 0; i < audioSamples.Length - WindowSize; i += WindowSize)
        {
            var chunk = new float[WindowSize];
            Array.Copy(audioSamples, i, chunk, 0, WindowSize);

            var inputTensor = new DenseTensor<float>(chunk, [1, WindowSize]);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor),
                NamedOnnxValue.CreateFromTensor("sr", srTensor),
                NamedOnnxValue.CreateFromTensor("state", stateTensor)
            };

            using var results = _vadSession.Run(inputs);

            var probTensor = results.First(v => v.Name == "output").AsTensor<float>();
            float probability = probTensor.GetValue(0);

            var nextState = (DenseTensor<float>)results.First(v => v.Name == "stateN").AsTensor<float>();
            nextState.Buffer.Span.CopyTo(stateTensor.Buffer.Span);

            if (probability >= SpeechThreshold)
            {
                activeSpeechBuffer.AddRange(chunk);
            }
        }

        if (activeSpeechBuffer.Count == 0)
        {
            return string.Empty;
        }

        // ==========================================================
        // WHISPER INFERENCE (Dynamically built per request)
        // ==========================================================
        var builder = _whisperFactory.CreateBuilder()
            .WithLanguage(targetLanguage)
            .WithProbabilities(); // Optional: Allows confidence score extraction if needed later

        if (isTranslation)
        {
            builder.WithTranslate();
        }

        // The 'using' statement ensures the lightweight processor is destroyed after the request
        using var processor = builder.Build();
        var sb = new StringBuilder();

        await foreach (var segment in processor.ProcessAsync([.. activeSpeechBuffer]))
        {
            sb.Append(segment.Text);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Cleans up unmanaged resources and ONNX sessions.
    /// </summary>
    public void Dispose()
    {
        _whisperFactory?.Dispose();
        _vadSession?.Dispose();
        GC.SuppressFinalize(this);
    }
}