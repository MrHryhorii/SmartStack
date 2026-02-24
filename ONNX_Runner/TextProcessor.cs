using System.Text.Json;
using System.Text.Json.Serialization;
using Tokenizers.DotNet;

namespace ONNX_Runner;

/// <summary>
/// Represents the structure of the tokenizer_config.json file.
/// </summary>
public class TokenizerConfig
{
    [JsonPropertyName("add_bos_token")]
    public bool AddBosToken { get; set; }

    [JsonPropertyName("add_eos_token")]
    public bool AddEosToken { get; set; }

    [JsonPropertyName("model_input_names")]
    public string[] ModelInputNames { get; set; } = [];

    [JsonPropertyName("model_max_length")]
    public int ModelMaxLength { get; set; }
}

/// <summary>
/// Handles the conversion of raw text into an array of token IDs,
/// and manages tokenizer configuration parameters.
/// </summary>
public class TextProcessor
{
    private readonly Tokenizer _tokenizer;

    // Expose the configuration so the Inference Engine can read it
    public TokenizerConfig Config { get; private set; }

    public TextProcessor(string modelsDirectory)
    {
        string tokenizerPath = Path.Combine(modelsDirectory, "tokenizer.json");
        string configPath = Path.Combine(modelsDirectory, "tokenizer_config.json");

        if (!File.Exists(tokenizerPath))
        {
            throw new FileNotFoundException($"Tokenizer file not found at: {tokenizerPath}");
        }

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Tokenizer config file not found at: {configPath}");
        }

        // Initialize the Hugging Face tokenizer
        _tokenizer = new Tokenizer(vocabPath: tokenizerPath);

        // Read and deserialize the configuration JSON
        string configJson = File.ReadAllText(configPath);
        Config = JsonSerializer.Deserialize<TokenizerConfig>(configJson)
                 ?? new TokenizerConfig();

        Console.WriteLine("Tokenizer and configuration loaded successfully.");
    }

    /// <summary>
    /// Processes the input text and returns an array of integer token IDs.
    /// </summary>
    public int[] Tokenize(string text)
    {
        var tokens = _tokenizer.Encode(text);

        // Note: The Tokenizers.DotNet library usually applies BOS/EOS automatically 
        // if they are defined in the tokenizer.json post-processor rules.
        return [.. tokens.Select(t => (int)t)];
    }
}