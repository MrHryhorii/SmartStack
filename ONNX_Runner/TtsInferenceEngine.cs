using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ONNX_Runner;

public class TtsInferenceEngine(TtsModelManager modelManager, TextProcessor textProcessor)
{
    private readonly TtsModelManager _modelManager = modelManager;
    private readonly TextProcessor _textProcessor = textProcessor;
    private static readonly float[] memory = [1.0f];

    public float[] GenerateSpeech(string text)
    {
        // Tokenize the input text
        int[] tokenIds = _textProcessor.Tokenize(text);
        long[] longTokens = tokenIds.Select(t => (long)t).ToArray();

        int batchSize = 1;
        int sequenceLength = longTokens.Length;

        // Prepare EmbedTokens Inputs
        var inputIdsTensor = new DenseTensor<long>(longTokens, [batchSize, sequenceLength]);

        // Position IDs are just sequential numbers: 0, 1, 2, 3...
        long[] positions = Enumerable.Range(0, sequenceLength).Select(x => (long)x).ToArray();
        var positionIdsTensor = new DenseTensor<long>(positions, [batchSize, sequenceLength]);

        // Exaggeration is a style parameter (often set to 1.0f as default)
        var exaggerationTensor = new DenseTensor<float>(memory, [batchSize]);

        var embedInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("position_ids", positionIdsTensor),
            NamedOnnxValue.CreateFromTensor("exaggeration", exaggerationTensor)
        };

        // Execute EmbedTokens Model
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> embedOutputs =
            _modelManager.EmbedTokens.Run(embedInputs);

        // Extract the inputs_embeds tensor
        var inputsEmbeds = embedOutputs.First(o => o.Name == "inputs_embeds").AsTensor<float>();

        Console.WriteLine($"Step 1 Complete: Text embedded. Shape: [{string.Join(", ", inputsEmbeds.Dimensions.ToArray())}]");

        // TODO: Next step is the LanguageModel generation loop.
        return [];
    }
}