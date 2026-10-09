using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class SharedRulesChecks
{
    internal static IEnumerable<(string Name, Action Check)> Cases()
    {
        var rules = LoadRules();
        var chunker = new TextChunker(new ChunkerSettings(), rules);
        var detector = Detector(chunker);
        var mapper = new DynamicPunctuationMapper(Config(), rules);

        var fixtures = new (string Name, string Input, string[] Chunks, bool[] Finished,
            string[] Cores, string[] Rendered, bool Early)[]
        {
            ("Custom general abbreviation", "Use zzq. before the next step. Next.",
                ["Use zzq. before the next step.", "Next."], [true, true],
                ["Use zzq. before the next step", "Next"], ["Use zzq. before the next step.", "Next."], false),
            ("Custom prefix binds a name", "Archon. Morgan arrived. Next.",
                ["Archon. Morgan arrived.", "Next."], [true, true],
                ["Archon. Morgan arrived", "Next"], ["Archon. Morgan arrived.", "Next."], false),
            ("Custom lowercase name binding", "sv. Peter arrived. Next.",
                ["sv. Peter arrived.", "Next."], [true, true],
                ["sv. Peter arrived", "Next"], ["sv. Peter arrived.", "Next."], false),
            ("Custom number binding", "Read eqn. 2. Next.",
                ["Read eqn. 2.", "Next."], [true, true],
                ["Read eqn. 2", "Next"], ["Read eqn. 2.", "Next."], false),
            ("Custom introductory abbreviation", "Use vizx. Python here. Next.",
                ["Use vizx. Python here.", "Next."], [true, true],
                ["Use vizx. Python here", "Next"], ["Use vizx. Python here.", "Next."], false),
            ("Configured lowercase single letter", "Read z. Morgan spoke. Next.",
                ["Read z. Morgan spoke.", "Next."], [true, true],
                ["Read z. Morgan spoke", "Next"], ["Read z. Morgan spoke.", "Next."], false),
            ("Sentence-final abbreviation period survives", "Read zzq. Next.",
                ["Read zzq.", "Next."], [true, true],
                ["Read zzq", "Next"], ["Read zzq.", "Next."], false),
            ("Custom period-like mark retains title context", "Dr⁖ Morgan arrived. Next.",
                ["Dr⁖ Morgan arrived.", "Next."], [true, true],
                ["Dr. Morgan arrived", "Next"], ["Dr. Morgan arrived.", "Next."], false),
            ("Custom hard terminator reaches the mapper", "First⸼ Next.",
                ["First⸼", "Next."], [true, true],
                ["First", "Next"], ["First.", "Next."], false),
            ("Custom question and exclamation retain their types", "Ready⸘ Yes⸰",
                ["Ready⸘", "Yes⸰"], [true, true],
                ["Ready", "Yes"], ["Ready?", "Yes!"], false),
            ("Pause-only addition does not enable an early cut", "Alpha※ beta.",
                ["Alpha※ beta."], [true], ["Alpha", "beta"], ["Alpha, beta."], true),
            ("Clause addition does enable an early cut", "Alpha⁏ beta. Next.",
                ["Alpha⁏", "beta.", "Next."], [false, true, true],
                ["Alpha", "beta", "Next"], ["Alpha,", "beta.", "Next."], true),
            ("Custom ellipsis keeps a lowercase continuation", "Wait⸬ maybe. Next.",
                ["Wait⸬ maybe.", "Next."], [true, true],
                ["Wait", "maybe", "Next"], ["Wait... maybe.", "Next."], false),
            ("Closing mark stays with its terminal cluster", "Ready?⟫ Next.",
                ["Ready?⟫", "Next."], [true, true],
                ["Ready", "Next"], ["Ready?)", "Next."], false),
            ("Default lowercase Hungarian older prefix", "id. Kovács arrived. Next.",
                ["id. Kovács arrived.", "Next."], [true, true],
                ["id. Kovács arrived", "Next"], ["id. Kovács arrived.", "Next."], false),
            ("Default uppercase Hungarian older prefix", "ID. Kovács arrived. Next.",
                ["ID. Kovács arrived.", "Next."], [true, true],
                ["ID. Kovács arrived", "Next"], ["ID. Kovács arrived.", "Next."], false),
            ("Hungarian younger prefix with a compatibility period", "ifj． Szathmári arrived. Next.",
                ["ifj． Szathmári arrived.", "Next."], [true, true],
                ["ifj. Szathmári arrived", "Next"], ["ifj. Szathmári arrived.", "Next."], false),
            ("Default Hungarian widowhood prefix", "özv. Kiss arrived. Next.",
                ["özv. Kiss arrived.", "Next."], [true, true],
                ["özv. Kiss arrived", "Next"], ["özv. Kiss arrived.", "Next."], false),
            ("Unknown two-letter token before a name", "zx. Kovács arrived. Next.",
                ["zx.", "Kovács arrived.", "Next."], [true, true, true],
                ["zx", "Kovács arrived", "Next"], ["zx.", "Kovács arrived.", "Next."], false),
            ("Unknown lowercase letter before lowercase prose", "x. next sentence.",
                ["x.", "next sentence."], [true, true],
                ["x", "next sentence"], ["x.", "next sentence."], false),
            ("Unknown two-letter token before lowercase prose", "zx. next sentence.",
                ["zx. next sentence."], [true],
                ["zx. next sentence"], ["zx. next sentence."], false),
            ("Explicit newline overrides Hungarian name binding", "id.\nKovács arrived.",
                ["id.", "Kovács arrived."], [true, true],
                ["id", "Kovács arrived"], ["id.", "Kovács arrived."], false),
            ("Default titles remain available", "St. Olav spoke. Next.",
                ["St. Olav spoke.", "Next."], [true, true],
                ["St. Olav spoke", "Next"], ["St. Olav spoke.", "Next."], false)
        };

        foreach (var fixture in fixtures)
        {
            yield return ($"Shared rules pipeline: {fixture.Name}", () =>
            {
                Require(ReferenceEquals(chunker.Rules, mapper.Rules), "Consumers use different catalogs.");
                var chunks = chunker.Split(fixture.Input, fixture.Early);
                Require(chunks.Select(c => c.Text).SequenceEqual(fixture.Chunks), "Sentence chunks differ from the fixture.");
                Require(chunks.Select(c => c.IsSentenceFinished).SequenceEqual(fixture.Finished), "Completion flags differ.");

                var bridge = new EspeakWrapper();
                var phonemizer = new UnifiedPhonemizer(bridge, mapper, Config(), detector);
                var rendered = new List<string>();
                foreach (var chunk in chunks)
                {
                    var tokens = detector.ProcessTextToLanguageTokens(chunk.Text, "en");
                    Require(string.Concat(tokens.Select(t => t.Text)) == chunk.Text, "Tokenizer lost source text.");
                    rendered.Add(phonemizer.GetPhonemes(chunk.Text, "en"));
                }

                Require(rendered.SequenceEqual(fixture.Rendered), $"Punctuation result: {string.Join(" | ", rendered)}");
                Require(bridge.Calls.Select(c => c.Text).SequenceEqual(fixture.Cores),
                    $"Native bridge inputs: {string.Join(" | ", bridge.Calls.Select(c => c.Text))}");
                Require(bridge.Calls.All(c => c.Voice == "en-us"), "Forced language inheritance was lost.");
            });
        }

        yield return ("Shared rules pipeline: defaults remain independent", () =>
        {
            var defaults = new TextChunker(new ChunkerSettings());
            Require(defaults.Split("Read z. Morgan spoke. Next.").Select(c => c.Text)
                .SequenceEqual(["Read z.", "Morgan spoke.", "Next."]), "Custom abbreviation leaked into defaults.");
            Require(!TextChunkerRules.Default.CommonAbbreviations.Contains("zzq"), "Default catalog was mutated.");
        });

        yield return ("Shared rules pipeline: concurrent consumers share immutable rules", () =>
        {
            bool passed = Enumerable.Range(0, 40).AsParallel().All(_ =>
            {
                var bridge = new EspeakWrapper();
                var phonemizer = new UnifiedPhonemizer(bridge, mapper, Config(), detector);
                return phonemizer.GetPhonemes("Archon. Morgan arrived⸼", "en") == "Archon. Morgan arrived." &&
                    bridge.Calls.Select(c => c.Text).SequenceEqual(["Archon. Morgan arrived"]);
            });
            Require(passed, "Concurrent requests changed the pipeline result.");
        });
    }

    private static MixedLanguagePhonemizer Detector(TextChunker chunker) => new(
        new PhonemizerSettings { SupportedLanguages = ["en", "uk"] }, "en-us",
        NullLogger<MixedLanguagePhonemizer>.Instance, chunker);

    private static PiperConfig Config() => new()
    {
        Espeak = new EspeakConfig { Voice = "en-us" },
        PhonemeIdMap = new Dictionary<string, int[]>
        {
            ["."] = [1], ["?"] = [2], ["!"] = [3], [","] = [4], [" "] = [5], [")"] = [6]
        }
    };

    private static TextChunkerRules LoadRules()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tsubaki-pipeline-rules-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "AdditionalSentenceTerminators": ["⸼"],
                  "AdditionalPeriodLikeMarks": ["⁖"],
                  "AdditionalEllipsisMarks": ["⸬"],
                  "AdditionalQuestionMarks": ["⸘"],
                  "AdditionalExclamationMarks": ["⸰"],
                  "AdditionalClausePunctuation": ["⁏"],
                  "AdditionalPauseMarks": ["※"],
                  "AdditionalClosingPunctuation": ["⟫"],
                  "AdditionalAbbreviations": ["zzq.", "z."],
                  "AdditionalPrefixAbbreviations": ["Archon."],
                  "AdditionalNameBindingAbbreviations": ["sv."],
                  "AdditionalNumberBindingAbbreviations": ["eqn."],
                  "AdditionalIntroductoryAbbreviations": ["vizx."]
                }
                """);
            return TextChunkerRules.LoadOrDefault(path);
        }
        finally { File.Delete(path); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
