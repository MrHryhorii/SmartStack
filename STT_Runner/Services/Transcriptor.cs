using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Whisper.net;
using System.Text;
using System.Diagnostics;

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
    /// Performs a dummy inference run to pre-JIT the Whisper and VAD pipelines,
    /// eliminating cold-start latency on the first real HTTP request.
    /// Uses low-amplitude white noise to guarantee VAD passes audio through to Whisper.
    /// </summary>
    public async Task WarmUpAsync()
    {
        // Sanity check to prevent null reference exceptions during warmup if Initialize() wasn't called.
        if (_whisperFactory == null || _vadSession == null)
            throw new InvalidOperationException("Cannot warm up: Transcriptor Engine is not initialized.");
        // Log the start of the warmup process to give users feedback during startup, 
        // especially since GPU shader caching can take several seconds.
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("[SYSTEM] Warming up inference pipelines...");
        Console.ResetColor();
        // We run multiple iterations for GPU builds to ensure Vulkan shader pipelines are fully cached, 
        // while a single pass is sufficient for CPU-only builds.
#if GPU_SUPPORT
        const int WarmupIterations = 3; // Vulkan shader pipeline caching
#else
        const int WarmupIterations = 1; // JIT + ONNX kernel init is single-shot on CPU
#endif
        // We use a fixed seed for reproducibility, but the actual audio content doesn't matter as long as it's non-silent.
        var stopwatch = Stopwatch.StartNew();
        // Generate 1 second of low-amplitude white noise at 16kHz to ensure VAD detects "speech" and passes it to Whisper.
        var rng = new Random(42);
        var warmupAudio = new float[SampleRate];
        for (int i = 0; i < warmupAudio.Length; i++)
            warmupAudio[i] = (float)(rng.NextDouble() * 0.1 - 0.05);
        // Run the warmup iterations
        for (int iteration = 1; iteration <= WarmupIterations; iteration++)
        {
            Console.WriteLine($"[SYSTEM] Warmup pass {iteration}/{WarmupIterations}...");

            // --- VAD warmup ---
            var stateTensor = new DenseTensor<float>([2, 1, 128]);
            stateTensor.Fill(0f);
            var srTensor = new DenseTensor<long>(new long[] { SampleRate }, [1]);
            var chunk = new float[WindowSize];
            Array.Copy(warmupAudio, chunk, WindowSize);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(chunk, [1, WindowSize])),
                NamedOnnxValue.CreateFromTensor("sr", srTensor),
                NamedOnnxValue.CreateFromTensor("state", stateTensor)
            };
            using var _ = _vadSession.Run(inputs);

            // --- Whisper warmup ---
            using var processor = _whisperFactory
                .CreateBuilder()
                .WithLanguage(_defaultLanguage ?? "auto")
                .Build();

            await foreach (var __ in processor.ProcessAsync(warmupAudio)) { }
        }
        // Stop the timer after all iterations are complete to get an accurate measure of total warmup time.
        stopwatch.Stop();
        // Log the total warmup time and the number of iterations to give users a clear understanding of the startup process.
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[SYSTEM] All pipelines warmed up ({WarmupIterations} pass(es)) in {stopwatch.Elapsed.TotalMilliseconds:F0} ms. Server ready.");
        Console.ResetColor();
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