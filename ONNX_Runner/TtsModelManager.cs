using Microsoft.ML.OnnxRuntime;

namespace ONNX_Runner;

/// <summary>
/// Manages the lifecycle and execution of the ONNX models for the TTS pipeline.
/// </summary>
public class TtsModelManager : IDisposable
{
    public InferenceSession SpeechEncoder { get; private set; }
    public InferenceSession EmbedTokens { get; private set; }
    public InferenceSession LanguageModel { get; private set; }
    public InferenceSession ConditionalDecoder { get; private set; }

    public TtsModelManager(string modelsDirectory)
    {
        Microsoft.ML.OnnxRuntime.SessionOptions options = new();

        // Configure Fallback (Try Windows GPU via DirectML, fallback to CPU)
        try
        {
            // 0 - default GPU's index
            options.AppendExecutionProvider_DML(0);
            Console.WriteLine("GPU (DirectML) successfully enabled for ONNX!");
        }
        catch (Exception ex)
        {
            options = new Microsoft.ML.OnnxRuntime.SessionOptions();
            Console.WriteLine($"GPU not available, falling back to CPU. Reason: {ex.Message}");
        }

        // Load models into memory
        Console.WriteLine($"Loading ONNX models into memory from: {modelsDirectory}...");

        try
        {
            SpeechEncoder = new InferenceSession(Path.Combine(modelsDirectory, "speech_encoder.onnx"), options);
            EmbedTokens = new InferenceSession(Path.Combine(modelsDirectory, "embed_tokens.onnx"), options);
            LanguageModel = new InferenceSession(Path.Combine(modelsDirectory, "language_model.onnx"), options);
            ConditionalDecoder = new InferenceSession(Path.Combine(modelsDirectory, "conditional_decoder.onnx"), options);

            Console.WriteLine("All 4 models loaded successfully!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CRITICAL ERROR loading models: {ex.Message}");
            throw; // Stop program if models can't be loaded, since we can't function without them
        }
    }


    /// <summary>
    /// Prints the exact input and output names and dimensions required by the loaded ONNX models.
    /// This is crucial for building the correct tensor inputs in the Inference Engine.
    /// </summary>
    public void PrintModelSignatures()
    {
        Console.WriteLine("\n================ MODEL SIGNATURES ================");
        PrintSignature("SpeechEncoder", SpeechEncoder);
        PrintSignature("EmbedTokens", EmbedTokens);
        PrintSignature("LanguageModel", LanguageModel);
        PrintSignature("ConditionalDecoder", ConditionalDecoder);
        Console.WriteLine("==================================================\n");
    }

    private void PrintSignature(string modelName, InferenceSession session)
    {
        if (session == null) return;

        Console.WriteLine($"--- {modelName} ---");
        Console.WriteLine("  INPUTS:");
        foreach (var input in session.InputMetadata)
        {
            // Join the dimensions array into a readable string format like [-1, 1024]
            string shape = string.Join(", ", input.Value.Dimensions);
            Console.WriteLine($"    Name: '{input.Key}' | Type: {input.Value.ElementType} | Shape: [{shape}]");
        }

        Console.WriteLine("  OUTPUTS:");
        foreach (var output in session.OutputMetadata)
        {
            string shape = string.Join(", ", output.Value.Dimensions);
            Console.WriteLine($"    Name: '{output.Key}' | Type: {output.Value.ElementType} | Shape: [{shape}]");
        }
        Console.WriteLine();
    }


    /// <summary>
    /// Free unmanaged resources (memory) when the application stops.
    /// </summary>
    public void Dispose()
    {
        SpeechEncoder?.Dispose();
        EmbedTokens?.Dispose();
        LanguageModel?.Dispose();
        ConditionalDecoder?.Dispose();

        // Tells the Garbage Collector that we've already cleaned up manually
        GC.SuppressFinalize(this);
    }
}