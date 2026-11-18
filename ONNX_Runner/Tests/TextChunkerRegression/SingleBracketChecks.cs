using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Text;
using ONNX_Runner.Services.Text;

internal static class SingleBracketChecks
{
    internal static IEnumerable<(string Category, string Name, Action Check)> Cases()
    {
        string[] bodies =
        [
            "həˈloʊ", "t\u0361s", "n\u0329", "a˥˩", "həˈloʊ, wɜːld", "ə. baʊt",
            "həˈloʊ\nwɜːld", string.Join(" ", Enumerable.Repeat("həˈloʊ", 40))
        ];
        foreach (string body in bodies)
        foreach (int limit in new[] { 8, 64, 200 })
        foreach (bool early in new[] { false, true })
        {
            yield return ("Single bracket phoneme protection", $"{body[..Math.Min(body.Length, 16)]}; cap={limit}; early={early}", () =>
            {
                string block = "[" + body + "]";
                var chunks = new TextChunker(new ChunkerSettings { MaxChunkLength = limit }).Split("Say " + block + " now. Next.", early);
                Require(chunks.Count(c => c.Text.Contains(block, StringComparison.Ordinal)) == 1,
                    "A phoneme block was split or duplicated.");
                Require(chunks.Count(c => c.IsSentenceFinished) == 2 && chunks[^1].IsSentenceFinished,
                    "Phonetic punctuation or a line break became a semantic sentence ending.");
                Require(!chunks.Any(c => c.Text.EndsWith("[", StringComparison.Ordinal) || c.Text.StartsWith("]", StringComparison.Ordinal)),
                    "A phoneme delimiter was detached from its content.");
            });
        }

        foreach (string suffix in new[] { ".", "?!", "\".", ")." })
        foreach (int limit in new[] { 2, 8, 200 })
        foreach (bool early in new[] { false, true })
        {
            yield return ("Single bracket phoneme protection", $"Attached suffix {suffix}; cap={limit}; early={early}", () =>
            {
                const string block = "[həˈloʊ]";
                string prefix = suffix.StartsWith(')') ? "Say (" : "Say ";
                var chunks = new TextChunker(new ChunkerSettings { MaxChunkLength = limit }).Split(prefix + block + suffix + " Next.", early);
                Require(chunks.Count(c => c.Text.Contains(block + suffix, StringComparison.Ordinal)) == 1,
                    "A phoneme block lost its attached sentence suffix.");
                Require(chunks.All(c => c.Text.Any(char.IsLetterOrDigit)) && chunks.Count(c => c.IsSentenceFinished) == 2,
                    "The block produced a punctuation-only chunk or lost sentence completion.");
            });
        }

        foreach (bool clause in new[] { false, true })
        {
            yield return ("Single bracket phoneme protection", $"Configured opening bracket retains its ordinary role; clause={clause}", () =>
            {
                string path = Path.Combine(Path.GetTempPath(), $"tsubaki-bracket-rules-{Guid.NewGuid():N}.json");
                try
                {
                    File.WriteAllText(path, clause ? "{\"AdditionalClausePunctuation\":[\"[\"]}" : "{\"AdditionalSentenceTerminators\":[\"[\"]}");
                    var chunker = new TextChunker(new ChunkerSettings(), TextChunkerRules.LoadOrDefault(path));
                    var chunks = chunker.Split("First[ ordinary. Next.", earlySplit: clause);
                    Require(chunks.Select(c => c.Text).SequenceEqual(["First[", "ordinary.", "Next."]),
                        "A configured bracket boundary was suppressed outside phonemes.");
                    Require(chunks[0].IsSentenceFinished == !clause, "The configured boundary role changed.");
                }
                finally
                {
                    File.Delete(path);
                }
            });
        }

        foreach (bool early in new[] { false, true })
        {
            yield return ("Single bracket phoneme protection", $"Ordinary bracketed sentences remain splittable; early={early}", () =>
            {
                var chunks = new TextChunker(new ChunkerSettings()).Split("[First. Next.] End.", early);
                Require(chunks.Select(c => c.Text).SequenceEqual(["[First.", "Next.]", "End."]),
                    "Ordinary square brackets acquired raw-phoneme semantics.");
            });
            yield return ("Single bracket phoneme protection", $"Ordinary early comma remains available; early={early}", () =>
            {
                var chunks = new TextChunker(new ChunkerSettings()).Split("First, [həˈloʊ] continues. Next.", early);
                Require(chunks.Select(c => c.Text).SequenceEqual(early
                    ? ["First,", "[həˈloʊ] continues.", "Next."]
                    : ["First, [həˈloʊ] continues.", "Next."]), "An external early boundary was lost.");
                Require(chunks.Select(c => c.IsSentenceFinished).SequenceEqual(early ? [false, true, true] : [true, true]),
                    "The external early cut changed completion flags.");
            });
            yield return ("Single bracket phoneme protection", $"A phoneme block without external punctuation remains unfinished; early={early}", () =>
            {
                var chunks = new TextChunker(new ChunkerSettings()).Split("[ə. baʊt]", early);
                Require(chunks.Count == 1 && chunks[0].Text == "[ə. baʊt]" && !chunks[0].IsSentenceFinished,
                    "A syllable boundary completed the sentence.");
            });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
