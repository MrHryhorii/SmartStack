using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Services.Text;

internal static class LongPhonemeChecks
{
    internal static IEnumerable<(string Category, string Name, Action Check)> Cases(Dictionary<string, int[]>? phonemeIdMap = null)
    {
        TextChunker CreateChunker(int limit) => new(new ChunkerSettings { MaxChunkLength = limit }, phonemeIdMap: phonemeIdMap);

        string[] bodies =
        [
            string.Join(" ", Enumerable.Repeat("həˈloʊ", 50)),
            "ɪ" + new string('a', 300),
            string.Concat(Enumerable.Repeat("t\u0361sa˥˩n\u0329ˈkaʰ", 40)),
            string.Join(", ", Enumerable.Repeat("ɪ?! ə", 40)),
            string.Join("\n", Enumerable.Repeat("həˈloʊ", 40))
        ];
        foreach (var (Open, Close) in new[] { (Open: "[", Close: "]"), (Open: "[[", Close: "]]"), (Open: "/", Close: "/") })
        foreach (string body in bodies)
        foreach (int limit in new[] { 8, 16, 64, 200 })
        foreach (bool early in new[] { false, true })
        {
            yield return ("Long phoneme blocks", $"{Open}; payload={body.Length}; cap={limit}; early={early}", () =>
            {
                string input = Open + body + Close;
                var chunks = CreateChunker(limit).Split(input, early);
                var payloads = chunks.Select(chunk => ExplicitPayload(chunk.Text)).ToArray();
                Require(chunks.Count > 1 && string.Concat(payloads) == body, "Phonemes were lost, duplicated or left unwrapped.");
                Require(chunks.All(c => c.Text.Length <= limit), "An ordinary IPA element bypassed the configured cap.");
                Require(chunks.All(c => !c.IsSentenceFinished), "An internal syllable/line boundary completed the thought.");
                Require(!payloads.Any(p => p.EndsWith('ˈ') || p.EndsWith('ˌ') || p.EndsWith('\u0361') || p.EndsWith('\u035C')),
                    "An attached phonetic mark was stranded at a cut.");
            });
        }

        string[] atoms = ["t\u0361s", "t\u035Cs", "a˥˩", "n\u0329", "ˈk", "aʰ", "e\u0303ː", " ", "ⁿd", "kʼ", "a‿ɪ"];
        string bodyWithAtoms = string.Concat(Enumerable.Range(0, 20).SelectMany(_ => atoms));
        var boundaries = new HashSet<int> { 0 };
        int offset = 0;
        foreach (string atom in Enumerable.Range(0, 20).SelectMany(_ => atoms)) { offset += atom.Length; boundaries.Add(offset); }
        foreach (int limit in new[] { 1, 2, 5, 7, 9, 16, 63 })
        {
            yield return ("Long phoneme blocks", $"Phonetic atoms stay attached; cap={limit}", () =>
            {
                var chunks = CreateChunker(limit).Split("[[" + bodyWithAtoms + "]]");
                int cursor = 0;
                foreach (var chunk in chunks)
                {
                    string payload = ExplicitPayload(chunk.Text);
                    Require(boundaries.Contains(cursor) && boundaries.Contains(cursor + payload.Length), "A tied/stressed/modified phoneme was split.");
                    Require(chunk.Text.Length <= limit || atoms.Contains(payload, StringComparer.Ordinal),
                        "More than one indivisible phonetic atom exceeded a tiny cap.");
                    cursor += payload.Length;
                }
                Require(cursor == bodyWithAtoms.Length && chunks.Count > 1, "Atom slicing failed to consume the source.");
            });
        }

        foreach (string suffix in new[] { ".", "?!", "...?!", "\".", ")." })
        foreach (int limit in new[] { 8, 16, 64 })
        foreach (bool early in new[] { false, true })
        {
            yield return ("Long phoneme blocks", $"Final suffix retained: {suffix}; cap={limit}; early={early}", () =>
            {
                string body = string.Concat(Enumerable.Repeat("ɪa", 100));
                string prefix = suffix.StartsWith(')') ? "(" : "";
                var chunks = CreateChunker(limit).Split(prefix + "[" + body + "]" + suffix + " Next.", early);
                Require(chunks.Count(c => c.IsSentenceFinished) == 2 && chunks[^1].IsSentenceFinished, "Completion was lost or duplicated.");
                Require(chunks.All(c => c.Text.Any(char.IsLetterOrDigit)), "A punctuation-only fragment was emitted.");
                Require(chunks.Any(c => c.Text.EndsWith("]]" + suffix, StringComparison.Ordinal)), "The final phoneme fragment lost its terminal cluster.");
                Require(chunks.All(c => c.Text.Length <= limit || c.Text == "[[a]]" + suffix),
                    "More than one final atom with its suffix exceeded a tiny cap.");
            });
        }

        foreach (string body in new[] { "ɪ", string.Concat(Enumerable.Repeat("ɪa", 100)) })
        foreach (int limit in new[] { 2, 8, 16, 64 })
        foreach (bool early in new[] { false, true })
        {
            yield return ("Long phoneme blocks", $"A decorated closing delimiter stays attached; payload={body.Length}; cap={limit}; early={early}", () =>
            {
                var chunks = CreateChunker(limit)
                    .Split("[[" + body + "]]\u0301. Next.", early);
                Require(chunks.Any(c => c.Text.Contains("]]\u0301.", StringComparison.Ordinal)), "A combining mark was detached from its closing delimiter.");
                Require(chunks.All(c => c.Text.Any(char.IsLetterOrDigit)) && chunks.Count(c => c.IsSentenceFinished) == 2,
                    "The decorated delimiter produced an empty-payload or punctuation-only fragment.");
            });
        }

        foreach (var (Open, Close) in new[] { (Open: "[", Close: "]"), (Open: "[[", Close: "]]"), (Open: "/", Close: "/") })
        foreach (int limit in new[] { 8, 16, 64, 200 })
        foreach (bool early in new[] { false, true })
        {
            yield return ("Long phoneme blocks", $"A block at the cap reserves space for its suffix; {Open}; cap={limit}; early={early}", () =>
            {
                string body = "ɪ" + new string('a', limit - Open.Length - Close.Length - 1);
                var chunks = CreateChunker(limit)
                    .Split("(" + Open + body + Close + ")?! Next.", early);
                Require(chunks.All(c => c.Text.Length <= limit), "A fitting raw block became oversized when its outside punctuation was attached.");
                Require(chunks.Any(c => c.Text.EndsWith("]])?!", StringComparison.Ordinal)) && chunks.Count(c => c.IsSentenceFinished) == 2,
                    "Making room for the suffix lost its terminal cluster or completion flag.");
            });
        }

        foreach (int limit in new[] { 8, 16, 64 })
        {
            yield return ("Long phoneme blocks", $"Several long blocks preserve intervening prose; cap={limit}", () =>
            {
                string first = string.Concat(Enumerable.Repeat("ɪa", 100));
                string second = string.Concat(Enumerable.Repeat("əu", 100));
                var chunks = CreateChunker(limit)
                    .Split("Say [" + first + "] then [[" + second + "]] now. Next.");
                Require(chunks.Count(c => c.IsSentenceFinished) == 2 && chunks[^1].Text == "Next.", "Mixed prose lost its sentence boundaries.");
                Require(chunks.Any(c => c.Text.Contains("Say", StringComparison.Ordinal)) && chunks.Any(c => c.Text.Contains("then", StringComparison.Ordinal)) &&
                    chunks.Any(c => c.Text.Contains("now", StringComparison.Ordinal)), "Intervening prose was lost.");
                Require(chunks.All(c => c.Text.Length <= limit), "A mixed fragment exceeded its cap.");
            });
        }
    }

    private static string ExplicitPayload(string text)
    {
        Require(text.StartsWith("[[", StringComparison.Ordinal) && text.EndsWith("]]", StringComparison.Ordinal), "A fragment lost its explicit phoneme wrapper.");
        return text[2..^2];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
