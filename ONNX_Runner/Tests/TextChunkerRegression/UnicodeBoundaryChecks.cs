using System.Text;
using System.Text.Json;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Text;
using ONNX_Runner.Services.Text;

internal static class UnicodeBoundaryChecks
{
    internal static IEnumerable<(string Category, string Name, Action Check)> Cases()
    {
        var abbreviations = new (string Name, string First)[]
        {
            ("Polish saint", "św. Jan przyszedł."),
            ("Polish engineer", "inż. Kowalski przyszedł."),
            ("Turkish title", "Doç. Kaya geldi."),
            ("Hungarian widowhood prefix", "özv. Kiss arrived.")
        };
        foreach (var (Name, First) in abbreviations)
            foreach (var form in new[] { NormalizationForm.FormC, NormalizationForm.FormD })
                foreach (bool early in new[] { false, true })
                {
                    string first = First.Normalize(form);
                    yield return ("Canonical abbreviation forms", $"{Name}; {form}; early={early}",
                        () => Check(first + " Next.", [first, "Next."], [true, true], early));
                }

        foreach (bool prefix in new[] { false, true })
            foreach (var configuredForm in new[] { NormalizationForm.FormC, NormalizationForm.FormD })
            {
                string spelling = (prefix ? "Žžq" : "žžq").Normalize(configuredForm);
                string json = prefix
                    ? JsonSerializer.Serialize(new { AdditionalPrefixAbbreviations = new[] { spelling } })
                    : JsonSerializer.Serialize(new { AdditionalNameBindingAbbreviations = new[] { spelling } });
                var rules = LoadRules(json);
                foreach (var inputForm in new[] { NormalizationForm.FormC, NormalizationForm.FormD })
                    foreach (bool early in new[] { false, true })
                    {
                        string first = (prefix ? "Žžq. Morgan arrived." : "žžq. Morgan arrived.").Normalize(inputForm);
                        yield return ("Canonical abbreviation forms", $"Custom prefix={prefix}; configured={configuredForm}; input={inputForm}; early={early}",
                            () => Check(first + " Next.", [first, "Next."], [true, true], early, rules));
                    }
            }

        var casing = new (string Name, string Input, string[] Expected)[]
        {
            ("Titlecase after ellipsis", "Wait… ǅuro arrived.", ["Wait…", "ǅuro arrived."]),
            ("Supplementary uppercase after ellipsis", "Wait… \U00010400 arrived.", ["Wait…", "\U00010400 arrived."]),
            ("Supplementary lowercase after ellipsis", "Wait… \U00010428 continued.", ["Wait… \U00010428 continued."]),
            ("Supplementary lowercase after a period", "We used qzx. \U00010428 continued.", ["We used qzx. \U00010428 continued."]),
            ("Supplementary name after a title", "Dr. \U00010400 Smith spoke. Next.", ["Dr. \U00010400 Smith spoke.", "Next."]),
            ("Supplementary name after an initial", "A. \U00010400 Smith spoke. Next.", ["A. \U00010400 Smith spoke.", "Next."]),
            ("Supplementary uncased name after a title", "Dr. \U00020000 arrived. Next.", ["Dr. \U00020000 arrived.", "Next."]),
            ("Supplementary number after an abbreviation", "No. \U0001D7D8 is ready. Next.", ["No. \U0001D7D8 is ready.", "Next."]),
            ("Supplementary dotted initials", "The \U00010400.\U00010401. Group met. Next.", ["The \U00010400.\U00010401. Group met.", "Next."]),
            ("Decomposed dotted initials", "The A\u0301.B\u0308. Group met. Next.", ["The A\u0301.B\u0308. Group met.", "Next."]),
            ("Supplementary letter after an embedded period", "Use \U00010400.\U00010401 inside. Next.", ["Use \U00010400.\U00010401 inside.", "Next."]),
            ("Uncased Japanese after ellipsis remains continuous", "待って… 次です。", ["待って… 次です。"]),
            ("Georgian casing policy remains unchanged", "მოვიცადოთ… შემდეგ გავაგრძელოთ.", ["მოვიცადოთ… შემდეგ გავაგრძელოთ."])
        };
        foreach (var (Name, Input, Expected) in casing)
            foreach (bool early in new[] { false, true })
            {
                yield return ("Unicode boundary casing", $"{Name}; early={early}",
                    () => Check(Input, Expected, Enumerable.Repeat(true, Expected.Length).ToArray(), early));
            }

        foreach (char opening in new[] { '\u202A', '\u202B', '\u202D', '\u202E', '\u2066', '\u2067', '\u2068' })
            foreach (bool early in new[] { false, true })
            {
                char closing = opening < '\u2060' ? '\u202C' : '\u2069';
                string arabic = $"«{opening}هل يعمل؟{closing}»";
                yield return ("Directional boundary controls", $"Arabic quoted suffix; U+{(int)opening:X4}; early={early}",
                    () => Check(arabic + " التالي.", [arabic, "التالي."], [true, true], early));

                string quoted = $"“{opening}Really?!{closing}”";
                yield return ("Directional boundary controls", $"Quoted reporting; U+{(int)opening:X4}; early={early}",
                    () => Check(quoted + " he replied. Next.",
                        early ? [quoted, "he replied.", "Next."] : [quoted + " he replied.", "Next."],
                        early ? [false, true, true] : [true, true], early));

                string plain = $"{opening}Ready!{closing}";
                yield return ("Directional boundary controls", $"Closing control stays with its scope; U+{(int)opening:X4}; early={early}",
                    () => Check(plain + " Next.", [plain, "Next."], [true, true], early));
                yield return ("Directional boundary controls", $"A formatting scope is not a reporting quote; U+{(int)opening:X4}; early={early}",
                    () => Check(plain + " he replied.", [plain, "he replied."], [true, true], early));
            }

        foreach (bool early in new[] { false, true })
        {
            yield return ("Directional boundary controls", $"The next scope remains with the next sentence; early={early}",
                () => Check("First. \u202BSecond.\u202C", ["First.", "\u202BSecond.\u202C"], [true, true], early));
            yield return ("Directional boundary controls", $"Whitespace before a closing scope; early={early}",
                () => Check("\u202AReady! \u202C Next.", ["\u202AReady! \u202C", "Next."], [true, true], early));
            yield return ("Directional boundary controls", $"Nested closing scopes; early={early}",
                () => Check("\u202A\u2067Ready!\u2069\u202C Next.", ["\u202A\u2067Ready!\u2069\u202C", "Next."], [true, true], early));
        }

        // U+037E is already a hard question mark. These fixtures do not ask the ASCII
        // semicolon heuristic to infer a question after digits or another script.
        foreach (string question in new[] { "Πόσο κάνει 2\u037E", "Είσαι ο John\u037E", "Είναι σωστό (ναι)\u037E" })
            foreach (bool early in new[] { false, true })
            {
                yield return ("Explicit Greek question marks", $"{question}; early={early}",
                    () => Check(question + " Ναι.", [question, "Ναι."], [true, true], early));
            }
    }

    private static void Check(string input, string[] expected, bool[] finished, bool early, TextChunkerRules? rules = null)
    {
        var chunks = new TextChunker(new ChunkerSettings { MaxChunkLength = 1000 }, rules).Split(input, early);
        if (chunks.Select(c => c.Text).SequenceEqual(expected) &&
            chunks.Select(c => c.IsSentenceFinished).SequenceEqual(finished)) return;

        throw new InvalidOperationException($"Actual: {JsonSerializer.Serialize(chunks)}");
    }

    private static TextChunkerRules LoadRules(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tsubaki-unicode-rules-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            return TextChunkerRules.LoadOrDefault(path);
        }
        finally { File.Delete(path); }
    }
}
