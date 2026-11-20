using System.Text;
using System.Text.Json;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Text;
using ONNX_Runner.Services.Text;

internal static class ExternalRulesChecks
{
    internal static IEnumerable<(string Category, string Name, Action Check)> Cases()
    {
        const string category = "External rules and example";
        string examplePath = Path.Combine(AppContext.BaseDirectory, "TextChunkerRules.example.json");

        yield return (category, "Example is valid JSON with entries in every supported category", () =>
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(examplePath));
            Require(document.RootElement.EnumerateObject().Count() == 14, "Example categories are incomplete.");
            foreach (var property in document.RootElement.EnumerateObject())
                Require(property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0,
                    $"Missing examples for {property.Name}.");
            var rules = TextChunkerRules.LoadOrDefault(examplePath);
            Require(rules.KnownAbbreviations.Contains("dept") && rules.PrefixAbbreviations.Contains("supt") &&
                rules.NameBindingAbbreviations.Contains("revd") && rules.NumberBindingAbbreviations.Contains("eqn") &&
                rules.IntroductoryAbbreviations.Contains("scil"), "Example abbreviations were not merged.");
            Require(rules.KnownAbbreviations.Contains("dr") && !TextChunkerRules.Default.KnownAbbreviations.Contains("supt"),
                "Example replaced or mutated the built-in rules.");
        });

        var sentences = new (string Name, string Input, string[] Expected, bool[] Finished, bool Early)[]
        {
            ("Custom terminator and closing mark", "Ready⁖⸩ Next.", ["Ready⁖⸩", "Next."], [true, true], false),
            ("Custom period retains a known title", "Dr⸼ Morgan arrived. Next.", ["Dr⸼ Morgan arrived.", "Next."], [true, true], false),
            ("Custom ellipsis retains a lowercase continuation", "Wait︙ maybe. Next.", ["Wait︙ maybe.", "Next."], [true, true], false),
            ("Custom question mark", "Ready⸮ Next.", ["Ready⸮", "Next."], [true, true], false),
            ("Exclamation mark preserves built-in support", "Ready﹗ Next.", ["Ready﹗", "Next."], [true, true], false),
            ("Custom clause enables an early cut", "Alpha⁏ beta.", ["Alpha⁏", "beta."], [false, true], true),
            ("Custom clause remains together without early split", "Alpha⁏ beta.", ["Alpha⁏ beta."], [true], false),
            ("Pause-only addition does not enable an early cut", "Alpha‒ beta.", ["Alpha‒ beta."], [true], true),
            ("Prefix binds a name", "Supt. Morgan arrived. Next.", ["Supt. Morgan arrived.", "Next."], [true, true], false),
            ("Name-binding abbreviation accepts lowercase spelling", "revd. Morgan arrived. Next.", ["revd. Morgan arrived.", "Next."], [true, true], false),
            ("Number-binding abbreviation", "Read Eqn. 2. Next.", ["Read Eqn. 2.", "Next."], [true, true], false),
            ("Introductory abbreviation", "Use scil. Morgan here. Next.", ["Use scil. Morgan here.", "Next."], [true, true], false),
            ("Quoted terminal cluster retains lowercase reporting without early split", "\"Ready⸮!\" he replied. Next.",
                ["\"Ready⸮!\" he replied.", "Next."], [true, true], false),
            ("Quoted terminal cluster allows an early continuation", "\"Ready⸮!\" he replied. Next.",
                ["\"Ready⸮!\"", "he replied.", "Next."], [false, true, true], true),
            ("Raw phoneme blocks retain custom punctuation literally", "Read [[k︙a⁖t]]. Next.",
                ["Read [[k︙a⁖t]].", "Next."], [true, true], false)
        };
        foreach (var test in sentences)
            yield return (category, test.Name, () =>
            {
                var chunker = new TextChunker(new ChunkerSettings(), TextChunkerRules.LoadOrDefault(examplePath));
                var chunks = chunker.Split(test.Input, test.Early);
                Require(chunks.Select(chunk => chunk.Text).SequenceEqual(test.Expected), "Unexpected example boundaries.");
                Require(chunks.Select(chunk => chunk.IsSentenceFinished).SequenceEqual(test.Finished), "Unexpected completion flags.");
            });

        yield return (category, "Example technical marker is shared with protected-token recognition", () =>
        {
            const string token = "record§section";
            var rules = TextChunkerRules.LoadOrDefault(examplePath);
            Require(TechnicalTextRecognizer.TryGetSpanLength(token, 0, rules, out int length) && length == token.Length,
                "Example technical marker did not protect the complete token.");
            Require(!TechnicalTextRecognizer.TryGetSpanLength(token, 0, TextChunkerRules.Default, out _),
                "Fixture does not distinguish the example addition from built-in rules.");
        });
        foreach (bool early in new[] { false, true })
            yield return (category, $"Example rules preserve protected tokens near chunk limits; early={early}", () =>
            {
                string token = "record§" + new string('a', 100);
                string input = string.Join(' ', Enumerable.Repeat("Read this section", 8)) + " " + token + " before ending⁖ Next.";
                var rules = TextChunkerRules.LoadOrDefault(examplePath);
                var chunks = new TextChunker(new ChunkerSettings { MaxChunkLength = 64 }, rules).Split(input, early);
                Require(chunks.Count > 2 && chunks.Count(chunk => chunk.Text.Contains(token, StringComparison.Ordinal)) == 1,
                    "External rules split or duplicated a protected technical token.");
                // Emergency continuation glue is intentional and is not part of the source text.
                string restored = string.Concat(chunks.Select(chunk => chunk.Text.EndsWith('-') ? chunk.Text[..^1] : chunk.Text));
                Require(restored.Replace(" ", "") == input.Replace(" ", ""),
                    "Chunking with external rules lost source text.");
                Require(chunks[^1].Text == "Next." && chunks[^1].IsSentenceFinished,
                    "Custom terminal behavior was lost during emergency chunking.");
            });

        foreach (bool bom in new[] { false, true })
        foreach (string newline in new[] { "\n", "\r\n" })
            yield return (category, $"UTF-8 example loads with BOM={bom}, CRLF={newline.Length == 2}", () => WithDirectories((content, app) =>
            {
                string json = File.ReadAllText(examplePath).ReplaceLineEndings(newline);
                File.WriteAllText(Path.Combine(app, "TextChunkerRules.json"), json, new UTF8Encoding(bom));
                Require(TextChunkerRules.LoadForApplication(content, app).KnownAbbreviations.Contains("supt"),
                    "Encoding or line endings prevented external rule loading.");
            }));

        yield return (category, "Content root rules take precedence over application rules", () => WithDirectories((content, app) =>
        {
            Write(content, "{\"AdditionalPrefixAbbreviations\":[\"ContentTitle.\"]}");
            Write(app, "{\"AdditionalPrefixAbbreviations\":[\"AppTitle.\"]}");
            var rules = TextChunkerRules.LoadForApplication(content, app);
            Require(rules.PrefixAbbreviations.Contains("contenttitle") && !rules.PrefixAbbreviations.Contains("apptitle"),
                "Content root priority changed.");
        }));
        yield return (category, "Application rules load when content root has no active file", () => WithDirectories((content, app) =>
        {
            Write(app, "{\"AdditionalPrefixAbbreviations\":[\"AppTitle.\"]}");
            var chunker = new TextChunker(new ChunkerSettings(), TextChunkerRules.LoadForApplication(content, app));
            Require(chunker.Split("AppTitle. Morgan arrived. Next.").Select(chunk => chunk.Text)
                .SequenceEqual(["AppTitle. Morgan arrived.", "Next."]), "Application fallback did not reach the chunker.");
        }));
        yield return (category, "Missing active files retain defaults without creating files", () => WithDirectories((content, app) =>
        {
            Require(ReferenceEquals(TextChunkerRules.LoadForApplication(content, app), TextChunkerRules.Default),
                "Missing files did not select the built-in catalog.");
            Require(!Directory.EnumerateFiles(content).Any() && !Directory.EnumerateFiles(app).Any(), "Loader created an active file.");
        }));
        yield return (category, "Example filename never activates rules implicitly", () => WithDirectories((content, app) =>
        {
            File.Copy(examplePath, Path.Combine(app, "TextChunkerRules.example.json"));
            Require(ReferenceEquals(TextChunkerRules.LoadForApplication(content, app), TextChunkerRules.Default),
                "Example was activated without being renamed.");
        }));
        yield return (category, "Invalid content root rules cannot be hidden by valid application rules", () => WithDirectories((content, app) =>
        {
            Write(content, "{\"AdditionalQuestionMarks\":[\";\"]}");
            Write(app, "{}");
            try
            {
                TextChunkerRules.LoadForApplication(content, app);
                throw new InvalidOperationException("Invalid selected rules were silently ignored.");
            }
            catch (InvalidDataException) { }
        }));
        yield return (category, "Empty content root rules intentionally retain defaults", () => WithDirectories((content, app) =>
        {
            Write(content, "{}");
            Write(app, "{\"AdditionalPrefixAbbreviations\":[\"AppTitle.\"]}");
            Require(!TextChunkerRules.LoadForApplication(content, app).KnownAbbreviations.Contains("apptitle"),
                "An explicitly empty content root file fell through to application additions.");
        }));
    }

    private static void WithDirectories(Action<string, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "Tsubaki rules тест " + Guid.NewGuid().ToString("N"));
        string content = Path.Combine(root, "content root");
        string app = Path.Combine(root, "application files");
        Directory.CreateDirectory(content);
        Directory.CreateDirectory(app);
        try { check(content, app); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Write(string directory, string json) =>
        File.WriteAllText(Path.Combine(directory, "TextChunkerRules.json"), json);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
