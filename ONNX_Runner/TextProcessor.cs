using System.Text.Json;
using System.Text.Json.Serialization;
using Tokenizers.DotNet;

namespace ONNX_Runner;

public class TokenizerConfig
{
    [JsonPropertyName("add_bos_token")] public bool AddBosToken { get; set; }
    [JsonPropertyName("add_eos_token")] public bool AddEosToken { get; set; }
}

// Новий клас для параметрів генерації
public class GenerationConfig
{
    [JsonPropertyName("bos_token_id")] public int BosTokenId { get; set; }

    // Може бути одним числом або масивом
    [JsonPropertyName("eos_token_id")] public JsonElement EosTokenId { get; set; }

    [JsonPropertyName("repetition_penalty")] public float RepetitionPenalty { get; set; }

    public List<long> GetEosIds()
    {
        if (EosTokenId.ValueKind == JsonValueKind.Array)
            return [.. EosTokenId.EnumerateArray().Select(x => (long)x.GetInt32())];
        if (EosTokenId.ValueKind == JsonValueKind.Number)
            return [(EosTokenId.GetInt32())];
        return [2]; // Fallback
    }
}

public class TextProcessor
{
    private readonly Tokenizer _tokenizer;
    public TokenizerConfig Config { get; private set; }
    public GenerationConfig GenConfig { get; private set; } // Додано

    public TextProcessor(string modelsDirectory)
    {
        string tokenizerPath = Path.Combine(modelsDirectory, "tokenizer.json");
        string configPath = Path.Combine(modelsDirectory, "tokenizer_config.json");
        string genConfigPath = Path.Combine(modelsDirectory, "generation_config.json");

        _tokenizer = new Tokenizer(vocabPath: tokenizerPath);

        Config = JsonSerializer.Deserialize<TokenizerConfig>(File.ReadAllText(configPath)) ?? new();

        // Читаємо параметри "машини"
        GenConfig = JsonSerializer.Deserialize<GenerationConfig>(File.ReadAllText(genConfigPath)) ?? new();

        Console.WriteLine($"Generation config loaded: EOS IDs [{string.Join(", ", GenConfig.GetEosIds())}], Penalty: {GenConfig.RepetitionPenalty}");
    }

    public int[] Tokenize(string text) => [.. _tokenizer.Encode(text).Select(t => (int)t)];
}