using Microsoft.ML.OnnxRuntime;

namespace ONNX_Runner;

/// <summary>
/// Manages model lifecycle with specialized split-execution and automatic hardware fallback.
/// </summary>
public class TtsModelManager : IDisposable
{
    public InferenceSession SpeechEncoder { get; private set; }
    public InferenceSession EmbedTokens { get; private set; }
    public InferenceSession LanguageModel { get; private set; }
    public InferenceSession ConditionalDecoder { get; private set; }

    public TtsModelManager(string modelsDirectory)
    {
        // 1. Prepare GPU Options (DirectML)
        Microsoft.ML.OnnxRuntime.SessionOptions gpuOptions = new();
        bool isGpuAvailable = false;
        try
        {
            gpuOptions.AppendExecutionProvider_DML(0);
            isGpuAvailable = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GPU initialization failed, using CPU fallback for all models. Error: {ex.Message}");
        }

        // 2. Prepare CPU Options (Optimized for text and KV-cache)
        Microsoft.ML.OnnxRuntime.SessionOptions cpuOptions = new()
        {
            EnableMemoryPattern = false // Prevents static memory planning issues with dynamic shapes
        };

        Console.WriteLine($"Initializing Hybrid Inference Engine from: {modelsDirectory}");

        // 3. Load models with individual fallback logic
        // Text models are forced to CPU to avoid the DML GroupQueryAttention bug and PCIe overhead
        EmbedTokens = new InferenceSession(Path.Combine(modelsDirectory, "embed_tokens.onnx"), cpuOptions);
        LanguageModel = new InferenceSession(Path.Combine(modelsDirectory, "language_model.onnx"), cpuOptions);

        // Audio models attempt GPU first, then fallback to CPU if VRAM or compatibility fails
        SpeechEncoder = CreateSessionWithFallback(
            Path.Combine(modelsDirectory, "speech_encoder.onnx"),
            isGpuAvailable ? gpuOptions : cpuOptions,
            cpuOptions,
            "SpeechEncoder");

        ConditionalDecoder = CreateSessionWithFallback(
            Path.Combine(modelsDirectory, "conditional_decoder.onnx"),
            isGpuAvailable ? gpuOptions : cpuOptions,
            cpuOptions,
            "ConditionalDecoder");

        Console.WriteLine("Hybrid Model Manager initialized successfully.");
    }

    /// <summary>
    /// Attempts to create a session with preferred options, falling back to CPU on failure.
    /// </summary>
    private static InferenceSession CreateSessionWithFallback(string path, Microsoft.ML.OnnxRuntime.SessionOptions preferred, Microsoft.ML.OnnxRuntime.SessionOptions fallback, string modelName)
    {
        try
        {
            var session = new InferenceSession(path, preferred);
            Console.WriteLine($"[{modelName}] loaded with preferred hardware (GPU/DML).");
            return session;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{modelName}] failed to load on preferred hardware. Falling back to CPU. Error: {ex.Message}");
            return new InferenceSession(path, fallback);
        }
    }

    public void PrintModelSignatures()
    {
        Console.WriteLine("\n================ MODEL SIGNATURES ================");
        PrintSignature("SpeechEncoder", SpeechEncoder);
        PrintSignature("EmbedTokens", EmbedTokens);
        PrintSignature("LanguageModel", LanguageModel);
        PrintSignature("ConditionalDecoder", ConditionalDecoder);
        Console.WriteLine("==================================================\n");
    }

    private static void PrintSignature(string modelName, InferenceSession session)
    {
        if (session == null) return;
        Console.WriteLine($"--- {modelName} ---");
        foreach (var input in session.InputMetadata)
        {
            Console.WriteLine($"    Input: '{input.Key}' | Shape: [{string.Join(", ", input.Value.Dimensions)}]");
        }
    }

    public void Dispose()
    {
        SpeechEncoder?.Dispose();
        EmbedTokens?.Dispose();
        LanguageModel?.Dispose();
        ConditionalDecoder?.Dispose();
        GC.SuppressFinalize(this);
    }
}