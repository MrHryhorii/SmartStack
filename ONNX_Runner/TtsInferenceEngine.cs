using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ONNX_Runner;

/// <summary>
/// Coordinates the execution of the ONNX models to transform text into an audio waveform.
/// </summary>
public class TtsInferenceEngine(TtsModelManager modelManager, TextProcessor textProcessor)
{
    private readonly TtsModelManager _modelManager = modelManager;
    private readonly TextProcessor _textProcessor = textProcessor;

    /// <summary>
    /// Generates raw audio samples from the provided text.
    /// </summary>
    /// <param name="text">The text to synthesize.</param>
    /// <returns>An array of float values representing the audio waveform.</returns>
    public float[] GenerateSpeech(string text)
    {
        // Tokenize the input text
        int[] tokenIds = _textProcessor.Tokenize(text);

        // Convert integer array to an ONNX Tensor
        // ONNX expects data in specific dimensional shapes, usually [batch_size, sequence_length]
        // We cast to Int64 (long) because most ONNX language models expect 64-bit integers for tokens
        long[] longTokens = tokenIds.Select(t => (long)t).ToArray();

        var inputTensor = new DenseTensor<long>(
            longTokens,
            [1, longTokens.Length] // Batch size of 1
        );

        // 3. Create the input parameters for the first model
        // The string "input_ids" must exactly match the input name defined inside the ONNX file
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputTensor)
        };

        // TODO: Execute models sequentially (EmbedTokens -> LanguageModel -> ConditionalDecoder)

        return []; // Returning a dummy array until implementation is complete
    }
}