using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models.Inference;
using ONNX_Runner.Services.Text.Phonemization;

internal static class PhonemeArchitectureChecks
{
    internal static IEnumerable<(string Name, Action Check)> Cases()
    {
        foreach (bool useCatalog in new[] { false, true })
        foreach (var (composed, decomposed, carrier) in new[] { ("ã", "a\u0303", 10L), ("ẽ", "e\u0303", 11L) })
        foreach (var (modifier, release) in new[] { ("ʲ", 13L), ("ʷ", 14L) })
        {
            yield return ($"Canonical IPA keeps voiced aspiration: {composed}{modifier}ʰ; PHOIBLE={useCatalog}", () =>
            {
                var config = Config(new()
                {
                    ["a"] = [10], ["e"] = [11], ["n"] = [12], ["j"] = [13], ["w"] = [14],
                    ["h"] = [15], ["ɦ"] = [16]
                });
                string path = useCatalog ? Path.Combine("PHOIBLE", "phoible.csv") : "missing-architecture-test.csv";
                var fallback = new PhonemeFallbackMapper(path, config);
                var encoder = Tensor(config);
                long[] expected = [1, 0, carrier, 12, release, 16, 0, 2];
                Equal(encoder.PhonemesToIds(fallback.Adapter.Adapt(decomposed + modifier + "ʰ"), true, false), expected);
                Equal(encoder.PhonemesToIds(fallback.Adapter.Adapt(composed + modifier + "ʰ"), true, false), expected);
            });
        }

        yield return ("Equally good cascades retain the longest supported prefix", () => WithCatalog(path =>
        {
            var config = Config(new() { ["aɪ"] = [10], ["ɪə"] = [20], ["ɜ"] = [30] });
            var fallback = new PhonemeFallbackMapper(path, config);
            var sequence = fallback.Adapter.Adapt("aɪə");
            Require(sequence.Text == "aɪɜ", $"Expected 'aɪɜ', received '{sequence.Text}'.");
            Equal(Tensor(config).PhonemesToIds(sequence, true, false), [1, 0, 10, 30, 0, 2]);
        }));

        yield return ("Prepared adaptation and native lookup keep their startup inventory", () =>
        {
            var config = Config(new() { ["t"] = [10, 11], ["s"] = [12, 13] });
            var fallback = new PhonemeFallbackMapper("missing-architecture-test.csv", config);
            var encoder = Tensor(config);
            long[] native = encoder.PhonemesToIds("ts", true, false);
            long[] adapted = encoder.PhonemesToIds(fallback.Adapter.Adapt("ʦ"), true, false);

            config.PhonemeIdMap["t"][0] = 80;
            config.PhonemeIdMap["s"][1] = 81;
            config.PhonemeIdMap["t"] = [82];
            config.PhonemeIdMap["ts"] = [83];
            Equal(encoder.PhonemesToIds("ts", true, false), native);
            Equal(encoder.PhonemesToIds(fallback.Adapter.Adapt("ʦ"), true, false), adapted);
        });

        yield return ("Prepared tensors retain their startup control mappings", () =>
        {
            var config = Config(new()
            {
                ["^"] = [7, 8], ["_"] = [90, 91], ["$"] = [9, 10], [" "] = [3, 4],
                ["t"] = [20, 21], ["s"] = [22, 23]
            });
            var fallback = new PhonemeFallbackMapper("missing-architecture-test.csv", config);
            var encoder = Tensor(config);
            var sequence = fallback.Adapter.Adapt("ʦ");
            long[] expected = encoder.PhonemesToIds(sequence);
            foreach (string key in new[] { "^", "_", "$", " ", "t", "s" })
            {
                config.PhonemeIdMap[key][0] = 80;
                config.PhonemeIdMap[key] = [81];
            }
            Equal(encoder.PhonemesToIds(sequence), expected);
            Equal(encoder.PhonemesToIds(fallback.Adapter.Adapt("ʦ")), expected);
        });
    }

    private static void WithCatalog(Action<string> check)
    {
        string path = Path.Combine(Path.GetTempPath(), $"phoneme-architecture-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "Phoneme,SegmentClass,tone,high,periodicGlottalSource\n" +
                "aɪə,vowel,-,-,+\na,vowel,-,-,+\nə,vowel,-,-,+\nɜ,vowel,-,-,+\n");
            check(path);
        }
        finally { File.Delete(path); }
    }

    private static PiperConfig Config(Dictionary<string, int[]> tokens)
    {
        var config = new PiperConfig { PhonemeIdMap = new() { ["_"] = [0], ["^"] = [1], ["$"] = [2], [" "] = [3] } };
        foreach (var (text, ids) in tokens) config.PhonemeIdMap[text] = ids;
        return config;
    }

    private static PiperPhonemizer Tensor(PiperConfig config) => new(config, NullLogger<PiperPhonemizer>.Instance);
    private static void Equal(IEnumerable<long> actual, long[] expected) => Require(actual.SequenceEqual(expected),
        $"Expected [{string.Join(',', expected)}], received [{string.Join(',', actual)}].");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
