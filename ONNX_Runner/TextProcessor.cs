using Tokenizers.DotNet;

namespace ONNX_Runner;

/// <summary>
/// Handles the conversion of raw text into an array of token IDs.
/// Utilizes Tokenizers.DotNet to parse the Hugging Face tokenizer.json file natively.
/// </summary>
public class TextProcessor
{
    private readonly Tokenizer _tokenizer;

    public TextProcessor(string tokenizerPath)
    {
        if (!File.Exists(tokenizerPath))
        {
            throw new FileNotFoundException($"Tokenizer file not found at: {tokenizerPath}. Ensure tokenizer.json is in the models directory.");
        }

        // Initialize the Hugging Face tokenizer directly from the tokenizer.json file
        _tokenizer = new Tokenizer(vocabPath: tokenizerPath);

        Console.WriteLine("Tokenizer loaded successfully!");
    }

    /// <summary>
    /// Processes the input text and returns an array of integer token IDs.
    /// </summary>
    /// <param name="text">The string to be tokenized.</param>
    /// <returns>An array of token IDs representing the input text.</returns>
    public int[] Tokenize(string text)
    {
        // Encode returns a collection of integer IDs representing the text
        var tokens = _tokenizer.Encode(text);

        // Convert the collection to a standard integer array required by ONNX Runtime
        return [.. tokens.Select(t => (int)t)];
    }
}