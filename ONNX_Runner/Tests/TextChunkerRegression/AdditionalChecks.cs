using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class AdditionalChecks
{
    internal static IEnumerable<(string Category, string Name, Action Check)> Cases()
    {
        const string unicodeCategory = "Unicode emergency invariants";
        var units = new (string Name, string Text)[]
        {
            ("Supplementary letters", "\U00010400"),
            ("Combining accents", "a\u0301"),
            ("Multiple combining marks", "a\u0301\u0323"),
            ("Emoji modifiers", "\U0001F44D\U0001F3FD"),
            ("Regional indicator pairs", "\U0001F1F3\U0001F1F4"),
            ("Joined emoji", "\U0001F469\u200D\U0001F4BB")
        };
        foreach (var unit in units)
        foreach (int limit in new[] { 64, 65, 79, 80 })
        foreach (bool finished in new[] { false, true })
        foreach (bool early in new[] { false, true })
        {
            string input = string.Concat(Enumerable.Repeat(unit.Text, 72)) + (finished ? "。" : "");
            yield return (unicodeCategory, $"{unit.Name}; limit={limit}; finished={finished}; early={early}",
                () => CheckEmergency(input, limit, early, finished));
        }

        string oversizedGrapheme = "a" + new string('\u0301', 95);
        foreach (bool early in new[] { false, true })
        {
            yield return (unicodeCategory, $"One oversized grapheme makes progress; early={early}",
                () => CheckEmergency(oversizedGrapheme + new string('b', 90) + "。", 64, early, true));
            yield return (unicodeCategory, $"Pause followed by a combining mark; early={early}",
                () => CheckEmergency(new string('a', 63) + "—\u0301" + new string('b', 90) + "。", 64, early, true));
            yield return (unicodeCategory, $"Whitespace followed by a combining mark; early={early}",
                () => CheckEmergency(new string('a', 63) + " \u0301" + new string('b', 90) + "。", 64, early, true));
        }

        foreach (string separator in new[] { " ", "\t", "\u00A0", "\u200F" })
        {
            string input = string.Join(separator, Enumerable.Repeat("Alpha", 42)) + "。";
            yield return (unicodeCategory, $"Text and flags survive U+{(int)separator[0]:X4} near limits",
                () => CheckEmergency(input, 64, false, true));
        }

        foreach (string terminal in new[] { ".", "?", "!", "..." })
        foreach (bool early in new[] { false, true })
        {
            yield return ("Ordinary emergency endings", $"Long ordinary word ending '{terminal}'; early={early}",
                () => CheckEmergency(new string('a', 150) + terminal, 64, early, true));
        }
        foreach (string terminal in new[] { ".", "?", "!" })
        foreach (string separator in new[] { "—\u0301", " \u0301" })
        foreach (bool early in new[] { false, true })
        {
            yield return (unicodeCategory, $"Combining separator before '{terminal}'; U+{(int)separator[0]:X4}; early={early}",
                () => CheckEmergency(new string('a', 63) + separator + new string('b', 90) + terminal, 64, early, true));
        }

        const string technicalCategory = "Protected emergency invariants";
        string[] protectedTokens =
        [
            "https://example.com/" + new string('x', 110) + "?q=1&v=2",
            "first." + new string('x', 110) + "@example.com",
            "C:\\Users\\" + new string('x', 110) + "\\config.json",
            "config." + new string('x', 110) + ".prod.json"
        ];
        foreach (string token in protectedTokens)
        foreach (bool finished in new[] { false, true })
        foreach (bool early in new[] { false, true })
        {
            string suffix = finished ? "." : "";
            yield return (technicalCategory, $"Oversized token at end; {token[..8]}; finished={finished}; early={early}",
                () => CheckEmergency("Please review " + token + suffix, 64, early, finished, token));
            yield return (technicalCategory, $"Oversized token before prose; {token[..8]}; finished={finished}; early={early}",
                () => CheckEmergency(token + " before the next build" + suffix, 64, early, finished, token));
        }
        foreach (bool early in new[] { false, true })
        {
            string token = protectedTokens[0];
            yield return (technicalCategory, $"Moving a cut before a URL respects the preceding grapheme; early={early}",
                () => CheckEmergency(new string('a', 63) + " \u0301" + token + ".", 64, early, true, token));
        }

        foreach (string malformed in new[] { "\uD800", "\uDC00", "a\uD800b", "\uD800\uD800" })
        {
            string input = string.Concat(Enumerable.Repeat(malformed, 90));
            yield return (unicodeCategory, $"Malformed UTF-16 remains recoverable; {JsonSerializer.Serialize(malformed)}",
                () => CheckEmergency(input, 64, false, false, requireValidUnicode: false));
        }

        // Fixed seeds make generated combinations reproducible. These fragments have known
        // in-sentence structure; the invariants do not invent a grammatical boundary oracle.
        string[] fragments = ["Dr. Morgan", "No. 12", "Fig. 3", "at 3.14", "U.S. Army",
            "config.json", "state-of-the-art", "a\u0301\u0323", "\U00010400", "“quoted words”",
            "https://example.com?q=1&v=2", "ordinary words"];
        foreach (int seed in Enumerable.Range(1700, 32))
        {
            var random = new Random(seed);
            string input = string.Join(" ", Enumerable.Range(0, 18).Select(_ => fragments[random.Next(fragments.Length)])) + "。";
            int limit = new[] { 51, 64, 79, 120 }[seed % 4];
            foreach (bool early in new[] { false, true })
            {
                yield return ("Generated structural invariants", $"Seed={seed}; limit={limit}; early={early}",
                    () => CheckEmergency(input, limit, early, true));
            }
        }

        foreach (bool early in new[] { false, true })
        {
            yield return ("Output allocation contracts", $"Whole input string is reused; early={early}", () =>
            {
                const string input = "Dr. Morgan spoke.";
                var chunks = new TextChunker(new ChunkerSettings()).Split(input, early);
                Require(chunks.Count == 1 && ReferenceEquals(chunks[0].Text, input));
                Require(chunks[0].IsSentenceFinished);
            });
        }
        yield return ("Output allocation contracts", "Whitespace trimming still creates the correct slice", () =>
        {
            const string input = "  Dr. Morgan spoke.  ";
            var chunks = new TextChunker(new ChunkerSettings()).Split(input);
            Require(chunks.Count == 1 && chunks[0].Text == "Dr. Morgan spoke.");
            Require(!ReferenceEquals(chunks[0].Text, input));
        });
        yield return ("Output allocation contracts", "Indivisible oversized input is reused and remains completed", () =>
        {
            string input = "https://example.com/" + new string('x', 100) + ".";
            var chunks = new TextChunker(new ChunkerSettings { MaxChunkLength = 64 }).Split(input);
            Require(chunks.Count == 1 && ReferenceEquals(chunks[0].Text, input));
            Require(chunks[0].IsSentenceFinished);
        });

        const string configCategory = "Extended rules configuration";
        yield return (configCategory, "Comments are accepted", () =>
            Require(Load("{ /* note */ \"AdditionalAbbreviations\": [\"zzq\"] }").KnownAbbreviations.Contains("zzq")));
        yield return (configCategory, "Trailing commas are accepted", () =>
            Require(Load("{\"AdditionalAbbreviations\":[\"zzq\",],}").KnownAbbreviations.Contains("zzq")));
        yield return (configCategory, "Property names are case insensitive", () =>
            Require(Load("{\"additionalprefixabbreviations\":[\"Archon\"]}").PrefixAbbreviations.Contains("archon")));
        yield return (configCategory, "All null lists preserve the catalog", () =>
        {
            var additions = new Dictionary<string, object?>
            {
                ["AdditionalSentenceTerminators"] = null, ["AdditionalPeriodLikeMarks"] = null,
                ["AdditionalEllipsisMarks"] = null, ["AdditionalQuestionMarks"] = null,
                ["AdditionalExclamationMarks"] = null, ["AdditionalClausePunctuation"] = null,
                ["AdditionalPauseMarks"] = null, ["AdditionalClosingPunctuation"] = null,
                ["AdditionalAbbreviations"] = null, ["AdditionalPrefixAbbreviations"] = null,
                ["AdditionalNameBindingAbbreviations"] = null, ["AdditionalNumberBindingAbbreviations"] = null,
                ["AdditionalIntroductoryAbbreviations"] = null
            };
            var actual = Load(JsonSerializer.Serialize(additions));
            Require(actual.KnownAbbreviations.SetEquals(TextChunkerRules.Default.KnownAbbreviations));
            Require(actual.SemanticKinds.Count == TextChunkerRules.Default.SemanticKinds.Count);
            Require(TextChunkerRules.Default.SemanticKinds.All(pair => actual.SemanticKinds[pair.Key] == pair.Value));
        });
        yield return (configCategory, "Null and blank abbreviations are ignored", () =>
            Require(Load("{\"AdditionalAbbreviations\":[null,\"\",\"   \",\"  zzq．  \" ]}")
                .KnownAbbreviations.Contains("zzq")));
        yield return (configCategory, "Maximum abbreviation length is accepted", () =>
            Require(Load(JsonSerializer.Serialize(new { AdditionalAbbreviations = new[] { new string('z', 128) } }))
                .KnownAbbreviations.Contains(new string('z', 128))));
        yield return (configCategory, "Custom period marks normalize abbreviation suffixes", () =>
            Require(Load("{\"AdditionalPeriodLikeMarks\":[\"⁖\"],\"AdditionalAbbreviations\":[\"zzq⁖⁖\"]}")
                .KnownAbbreviations.Contains("zzq")));
        yield return (configCategory, "Configured marks do not enable early splitting by themselves", () =>
        {
            var custom = new TextChunker(new ChunkerSettings(), Load("{\"AdditionalPauseMarks\":[\"※\"]}"));
            var chunks = custom.Split("Alpha※ beta.", earlySplit: true);
            Require(chunks.Select(c => c.Text).SequenceEqual(["Alpha※ beta."]));
            Require(chunks[0].IsSentenceFinished);
        });
        yield return (configCategory, "Configured pause participates in emergency splitting", () =>
        {
            var custom = new TextChunker(new ChunkerSettings { MaxChunkLength = 64 },
                Load("{\"AdditionalPauseMarks\":[\"※\"]}"));
            string input = new string('a', 40) + "※ " + new string('b', 40) + ".";
            var chunks = custom.Split(input, earlySplit: true);
            Require(chunks.Select(c => c.Text).SequenceEqual([new string('a', 40) + "※", new string('b', 40) + "."]));
            Require(chunks.Select(c => c.IsSentenceFinished).SequenceEqual([false, true]));
        });

        var invalidJson = new (string Name, string Json)[]
        {
            ("Zero-byte file", ""), ("Whitespace-only file", " \r\n"), ("Null root", "null"),
            ("Array root", "[]"), ("Wrong list type", "{\"AdditionalAbbreviations\":true}"),
            ("Null punctuation entry", "{\"AdditionalSentenceTerminators\":[null]}"),
            ("Empty punctuation entry", "{\"AdditionalSentenceTerminators\":[\"\"]}"),
            ("Supplementary punctuation entry", "{\"AdditionalSentenceTerminators\":[\"\uD800\uDD00\"]}"),
            ("Isolated high surrogate", "{\"AdditionalSentenceTerminators\":[\"\\uD800\"]}"),
            ("Isolated low surrogate", "{\"AdditionalSentenceTerminators\":[\"\\uDC00\"]}"),
            ("Letter as punctuation", "{\"AdditionalPauseMarks\":[\"z\"]}"),
            ("Whitespace as punctuation", "{\"AdditionalPauseMarks\":[\" \"]}"),
            ("Period-only abbreviation", "{\"AdditionalAbbreviations\":[\"...\"]}"),
            ("Internal tab in abbreviation", "{\"AdditionalAbbreviations\":[\"a\\tb\"]}"),
            ("Overlong abbreviation", JsonSerializer.Serialize(new { AdditionalAbbreviations = new[] { new string('z', 129) } }))
        };
        foreach (var test in invalidJson)
        {
            yield return (configCategory, $"Rejects {test.Name}", () =>
            {
                try { _ = Load(test.Json); }
                catch (InvalidDataException error)
                {
                    Require(error.Message.Contains("rules", StringComparison.OrdinalIgnoreCase));
                    return;
                }
                throw new InvalidOperationException("Expected InvalidDataException.");
            });
        }
    }

    private static void CheckEmergency(string input, int limit, bool early, bool finished,
        string? protectedToken = null, bool requireValidUnicode = true)
    {
        var chunker = new TextChunker(new ChunkerSettings { MaxChunkLength = limit });
        var chunks = chunker.Split(input, earlySplit: early);
        Require(chunks.Count > 0, "No chunks were produced.");
        Require(chunks.Count <= input.Length, "Emergency splitting did not make progress.");
        Require(chunks.Count(c => c.IsSentenceFinished) == (finished ? 1 : 0), "Wrong number of sentence endings.");
        Require(chunks[^1].IsSentenceFinished == finished, "The final chunk lost its completion flag.");

        // Match each emitted chunk against the original source. Only boundary whitespace and
        // an inserted emergency hyphen may differ; internal characters must retain their order.
        var boundaries = StringInfo.ParseCombiningCharacters(input).Append(input.Length).ToHashSet();
        int cursor = 0;
        foreach (var chunk in chunks)
        {
            Require(chunk.Text.Length > 0, "An empty chunk was emitted.");
            ReadOnlySpan<char> core = chunk.Text.AsSpan();
            int afterWhitespace = cursor;
            while (afterWhitespace < input.Length && char.IsWhiteSpace(input[afterWhitespace])) afterWhitespace++;
            bool originalPrefix = input.AsSpan(cursor).StartsWith(core, StringComparison.Ordinal) ||
                input.AsSpan(afterWhitespace).StartsWith(core, StringComparison.Ordinal);
            if (!originalPrefix && core[^1] == '-') core = core[..^1];
            if (!input.AsSpan(cursor).StartsWith(core, StringComparison.Ordinal)) cursor = afterWhitespace;
            Require(input.AsSpan(cursor).StartsWith(core, StringComparison.Ordinal), "Text was changed or lost.");
            Require(boundaries.Contains(cursor) && boundaries.Contains(cursor + core.Length), "An extended grapheme was split.");
            if (requireValidUnicode) Require(IsValidUtf16(chunk.Text), "A valid input produced malformed UTF-16.");

            bool allowedOversize = protectedToken is not null && chunk.Text.Contains(protectedToken, StringComparison.Ordinal) ||
                StringInfo.ParseCombiningCharacters(core.ToString()).Length == 1;
            Require(chunk.Text.Length <= limit + 1 || allowedOversize, "An ordinary chunk exceeded its size limit.");
            cursor += core.Length;
        }
        while (cursor < input.Length && char.IsWhiteSpace(input[cursor])) cursor++;
        Require(cursor == input.Length, "The end of the source text was lost.");
        if (protectedToken is null) return;

        Require(chunks.Count(c => c.Text.Contains(protectedToken, StringComparison.Ordinal)) == 1,
            "A protected token was split or duplicated.");
        Require(!chunks.Any(c => c.Text.Contains(protectedToken + "-", StringComparison.Ordinal)),
            "A complete technical token gained an emergency hyphen.");
    }

    private static bool IsValidUtf16(string text)
    {
        ReadOnlySpan<char> remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out int consumed) != OperationStatus.Done) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    private static TextChunkerRules Load(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tsubaki-extra-rules-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            return TextChunkerRules.LoadOrDefault(path);
        }
        finally { File.Delete(path); }
    }

    private static void Require(bool condition, string message = "The configured behavior did not match the fixture.")
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
