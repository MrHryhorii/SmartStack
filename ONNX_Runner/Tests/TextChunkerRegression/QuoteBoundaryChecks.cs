using System.Globalization;
using System.Text;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Services.Text;

internal static class QuoteBoundaryChecks
{
    private static readonly (string Open, string Close)[] QuotePairs =
    [
        ("\"", "\""), ("'", "'"), ("“", "”"), ("‘", "’"), ("„", "“"), ("‚", "‘"),
        ("«", "»"), ("»", "«"), ("‹", "›"), ("›", "‹"), ("「", "」"), ("『", "』"),
        ("〝", "〞"), ("❝", "❞"), ("❛", "❜")
    ];

    /// <summary>Exercises punctuation structure independently of a selected speech language.</summary>
    internal static IEnumerable<(string Category, string Name, Action Check)> Cases()
    {
        foreach (var pair in QuotePairs)
        foreach (string terminal in new[] { ".", "?", "!", "?!", "...?!", "…?!", "。", "！" })
        foreach (bool early in new[] { false, true })
        {
            string sentence = pair.Open + "Hello" + terminal + pair.Close;
            yield return ("Quoted sentence suffixes", $"{pair.Open}{pair.Close}; {terminal}; early={early}",
                () => Check(sentence + " Next.", [sentence, "Next."], [true, true], early));
            yield return ("Quoted sentence suffixes", $"EOF; {pair.Open}{pair.Close}; {terminal}; early={early}",
                () => Check(sentence, [sentence], [true], early));
        }

        foreach (var pair in QuotePairs)
        foreach (string spacing in new[] { " ", "\t", "\u00A0", "\u202F", "\u200F" })
        foreach (bool early in new[] { false, true })
        {
            string sentence = pair.Open + "Hello." + spacing + pair.Close;
            yield return ("Separated closing quotes", $"{pair.Open}{pair.Close}; U+{(int)spacing[0]:X4}; early={early}",
                () => Check(sentence + " Next.", [sentence, "Next."], [true, true], early));
            yield return ("Separated closing quotes", $"EOF; {pair.Open}{pair.Close}; U+{(int)spacing[0]:X4}; early={early}",
                () => Check(sentence, [sentence], [true], early));
        }

        foreach (var pair in QuotePairs)
        foreach (bool early in new[] { false, true })
        {
            string first = pair.Open + "你好！" + pair.Close;
            string second = pair.Open + "再见！" + pair.Close;
            yield return ("Adjacent quoted sentences", $"{pair.Open}{pair.Close}; early={early}",
                () => Check(first + second, [first, second], [true, true], early));
        }

        var reportingCases = new (string Name, string Quote, string Report)[]
        {
            ("Latin", "\"Really...?!\"", "he replied."),
            ("Curly Latin", "“Really…?!”", "she replied."),
            ("Cyrillic", "«Справді…?!»", "відповів він."),
            ("Greek", "«Αλήθεια…;»", "απάντησε εκείνος."),
            ("Armenian", "“Իսկապե՞ս?!”", "ասաց նա."),
            ("Supplementary lowercase", "“Ready?!”", "\U00010428 replied."),
            ("Plain quoted question", "\"Really?!\"", "she asked."),
            ("Quoted ellipsis", "\"Wait...\"", "he continued."),
            ("Reporting dash", "«Справді…?!»", "— відповів він.")
        };
        foreach (var fixture in reportingCases)
        foreach (bool early in new[] { false, true })
        {
            string input = fixture.Quote + " " + fixture.Report + " Next.";
            string[] expected = early ? [fixture.Quote, fixture.Report, "Next."] : [fixture.Quote + " " + fixture.Report, "Next."];
            bool[] finished = early ? [false, true, true] : [true, true];
            yield return ("Quoted early continuations", $"{fixture.Name}; early={early}",
                () => Check(input, expected, finished, early));
        }

        foreach (bool early in new[] { false, true })
        {
            yield return ("Quoted early continuations", $"Uppercase starts a new sentence; early={early}",
                () => Check("\"Really...?!\" Then he left.", ["\"Really...?!\"", "Then he left."], [true, true], early));
            yield return ("Quoted early continuations", $"Newline overrides lowercase; early={early}",
                () => Check("\"Really...?!\"\nhe replied.", ["\"Really...?!\"", "he replied."], [true, true], early));
            yield return ("Quoted early continuations", $"Caseless Japanese retains hard boundaries; early={early}",
                () => Check("「本当？！」彼は答えた。", ["「本当？！」", "彼は答えた。"], [true, true], early));
            yield return ("Quoted early continuations", $"Caseless Hebrew retains hard boundaries; early={early}",
                () => Check("«באמת?!» הוא ענה.", ["«באמת?!»", "הוא ענה."], [true, true], early));
            yield return ("Quoted early continuations", $"Georgian does not use initial capitalization; early={early}",
                () => Check("“მართლა?!” შემდეგ წავიდა.", ["“მართლა?!”", "შემდეგ წავიდა."], [true, true], early));
            yield return ("Quoted early continuations", $"Unfinished reporting clause; early={early}",
                () => Check("\"Really...?!\" he replied", early ? ["\"Really...?!\"", "he replied"] : ["\"Really...?!\" he replied"],
                    early ? [false, false] : [false], early));
        }

        yield return ("Quoted early continuations", "Only the first quoted sentence gets an early cut", () =>
            Check("\"Really?!\" he replied. \"Again?!\" she asked.",
                ["\"Really?!\"", "he replied.", "\"Again?!\" she asked."], [false, true, true], true));
        yield return ("Quoted early continuations", "An earlier comma consumes the one allowed early cut", () =>
            Check("First, \"Really?!\" he replied.", ["First,", "\"Really?!\" he replied."], [false, true], true));

        foreach (var pair in QuotePairs)
        foreach (string terminal in new[] { ".", "?!", "...?!", "。" })
        foreach (int limit in new[] { 8, 64, 200 })
        foreach (int offset in new[] { -1, 0, 1 })
        foreach (bool early in new[] { false, true })
        {
            string input = pair.Open + new string('a', limit - 2 + offset) + terminal + pair.Close;
            yield return ("Quoted emergency boundaries", $"{pair.Open}{pair.Close}; {terminal}; cap={limit}; offset={offset}; early={early}",
                () => CheckEmergency(input, limit, early));
        }

        foreach (bool early in new[] { false, true })
        {
            string longQuote = "\"Please wait,\" she said while the workers carefully inspected every part of the engine " +
                "and reviewed the detailed report before proceeding with the next stage of the lengthy maintenance procedure for the entire system.";
            yield return ("Quoted emergency boundaries", $"Closing quote after an emergency comma; early={early}", () =>
            {
                var chunks = Split("First, we check the engine. " + longQuote + " Next.", early, 200);
                Require(!chunks.Skip(1).Any(c => c.Text.StartsWith("\" she", StringComparison.Ordinal)), "A closing quote leaked into the next chunk.");
                Require(chunks.Any(c => c.Text.EndsWith("wait,\"", StringComparison.Ordinal)), "The comma lost its attached closing quote.");
            });
            yield return ("Quoted emergency boundaries", $"Nested suffix at the default cap; early={early}",
                () => CheckEmergency("(\"" + new string('a', 197) + "?!\").", 200, early));
            yield return ("Quoted emergency boundaries", $"Combining mark after a closing quote; early={early}",
                () => CheckEmergency("\"" + new string('a', 62) + ".\"\u0301", 64, early));
            yield return ("Quoted emergency boundaries", $"Unspaced CJK text at the default cap; early={early}",
                () => CheckEmergency("「" + new string('字', 198) + "。」", 200, early));
            yield return ("Quoted emergency boundaries", $"Spacing inside the closing suffix near the cap; early={early}",
                () => CheckEmergency("\"" + new string('a', 62) + ". \"", 64, early));
            yield return ("Quoted emergency boundaries", $"Oversized minimal punctuation atom; early={early}",
                () => CheckEmergency("\"A?!\"", 3, early, allowOversize: true));
        }

        foreach (bool early in new[] { false, true })
        {
            yield return ("Quote boundary counterexamples", $"Opening quote after a space; early={early}",
                () => Check("First. \"Second.\"", ["First.", "\"Second.\""], [true, true], early));
            yield return ("Quote boundary counterexamples", $"Opening quote after a newline; early={early}",
                () => Check("First.\n\"Second.\"", ["First.", "\"Second.\""], [true, true], early));
            yield return ("Quote boundary counterexamples", $"Balanced quotes before a new opening quote; early={early}",
                () => Check("\"First.\" Done. \"Second.\"", ["\"First.\"", "Done.", "\"Second.\""], [true, true, true], early));
            yield return ("Quote boundary counterexamples", $"Contractions do not open a quotation; early={early}",
                () => Check("Don't stop. 'Next.'", ["Don't stop.", "'Next.'"], [true, true], early));
            yield return ("Quote boundary counterexamples", $"Possessive apostrophe does not open a quotation; early={early}",
                () => Check("James' car is ready. 'Next.'", ["James' car is ready.", "'Next.'"], [true, true], early));
            yield return ("Quote boundary counterexamples", $"Several sentences inside one quote; early={early}",
                () => Check("\"First. Second.\" Next.", ["\"First.", "Second.\"", "Next."], [true, true, true], early));
            yield return ("Quote boundary counterexamples", $"Technical question is not an early boundary; early={early}",
                () => Check("Use \"https://example.org/path?q=1\" before the next step. Next.",
                    ["Use \"https://example.org/path?q=1\" before the next step.", "Next."], [true, true], early));
            yield return ("Quote boundary counterexamples", $"Nested closing suffix; early={early}",
                () => Check("He said (\"Stop!\"). Next.", ["He said (\"Stop!\").", "Next."], [true, true], early));
            yield return ("Quote boundary counterexamples", $"Mixed low and high nested quotes; early={early}",
                () => Check("„He said ‚Stop!‘“ Next.", ["„He said ‚Stop!‘“", "Next."], [true, true], early));
            yield return ("Quote boundary counterexamples", $"Mathematical closing bracket; early={early}",
                () => Check("⟨Hello.⟩ Next.", ["⟨Hello.⟩", "Next."], [true, true], early));
            yield return ("Quote boundary counterexamples", $"Standalone punctuation remains valid; early={early}",
                () => Check("?!", ["?!"], [true], early));
        }
    }

    private static List<TextChunker.TextChunk> Split(string input, bool early, int limit = 1000) =>
        new TextChunker(new ChunkerSettings { MaxChunkLength = limit }).Split(input, early);

    private static void Check(string input, string[] expected, bool[] finished, bool early)
    {
        var chunks = Split(input, early);
        Require(chunks.Select(c => c.Text).SequenceEqual(expected) &&
            chunks.Select(c => c.IsSentenceFinished).SequenceEqual(finished),
            $"Actual: {string.Join(" | ", chunks.Select(c => $"{c.Text} ({c.IsSentenceFinished})"))}");
    }

    private static void CheckEmergency(string input, int limit, bool early, bool allowOversize = false)
    {
        var chunks = Split(input, early, limit);
        Require(chunks.Count > 0, "No chunks were produced.");
        Require(chunks.Count(c => c.IsSentenceFinished) == 1 && chunks[^1].IsSentenceFinished, "The sentence completion flag was lost or duplicated.");
        var boundaries = StringInfo.ParseCombiningCharacters(input).Append(input.Length).ToHashSet();
        int cursor = 0;
        foreach (var chunk in chunks)
        {
            Require(chunk.Text.EnumerateRunes().Any(Rune.IsLetterOrDigit), "An attached punctuation suffix became a separate chunk.");
            ReadOnlySpan<char> core = chunk.Text.AsSpan();
            if (!input.AsSpan(cursor).StartsWith(core, StringComparison.Ordinal) && core[^1] == '-') core = core[..^1];
            Require(input.AsSpan(cursor).StartsWith(core, StringComparison.Ordinal), "Source text changed during emergency splitting.");
            Require(boundaries.Contains(cursor) && boundaries.Contains(cursor + core.Length), "A grapheme was split.");
            Require(allowOversize || chunk.Text.Length <= limit + 1, "A normal chunk exceeded its size cap.");
            cursor += core.Length;
        }
        Require(cursor == input.Length, "Source text was lost.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
