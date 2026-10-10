using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class TechnicalLanguageChecks
{
    internal static IEnumerable<(string Name, Action Check)> Cases()
    {
        var logger = new AnalysisLogger();
        var chunker = new TextChunker(new ChunkerSettings { MaxChunkLength = 1000 });
        var detector = new MixedLanguagePhonemizer(Settings(), "en-us", logger, chunker);

        // Expected languages and analysis boundaries come from the prose and explicit delimiters.
        // Technical identifiers remain separate speech parts, but never create an analysis boundary.
        var fixtures = new (string Name, string Input, string Language, string Analysis, string[] Technical)[]
        {
            ("Underscores preserve the surrounding English phrase",
                "The variable user_name contains build_output.", "en-us", "The variable contains",
                ["user_name", "build_output"]),
            ("Dotted file and identifier share English prose",
                "The file config.prod.json contains build_output.", "en-us", "The file contains",
                ["config.prod.json", "build_output"]),
            ("Operators preserve the surrounding English phrase",
                "The expression alpha+beta contains gamma+delta.", "en-us", "The expression contains",
                ["alpha+beta", "gamma+delta"]),
            ("Addresses preserve the surrounding English phrase",
                "The page https://example.com/a?q=1 contains https://example.org/b.", "en-us", "The page contains",
                ["https://example.com/a?q=1", "https://example.org/b"]),
            ("Neutral standalone symbols do not reset the script",
                "The value _ contains $.", "en-us", "The value contains", ["_", "$"]),
            ("Leading technical text inherits following prose",
                "user_name contains the requested value.", "en-us", "contains the requested value", ["user_name"]),
            ("Trailing technical text inherits preceding prose",
                "The requested value is stored in user_name.", "en-us", "The requested value is stored in", ["user_name"]),
            ("Adjacent technical parts retain their order",
                "user_name build_output contains the requested value.", "en-us", "contains the requested value",
                ["user_name", "build_output"]),
            ("Foreign-looking identifiers do not cast separate votes",
                "The variable français_utilisateur contains données_sortie.", "en-us", "The variable contains",
                ["français_utilisateur", "données_sortie"]),
            ("Tabs survive reconstruction",
                "The variable\tuser_name\tcontains\tbuild_output.", "en-us", "The variable contains",
                ["user_name", "build_output"]),
            ("Nonbreaking spaces survive reconstruction",
                "The variable\u00A0user_name\u00A0contains\u00A0build_output.", "en-us", "The variable contains",
                ["user_name", "build_output"]),
            ("French prose wins over the English model",
                "La variable user_name contient une valeur valide dans build_output.", "fr",
                "La variable contient une valeur valide dans", ["user_name", "build_output"]),
            ("Leading code inherits the following French prose",
                "user_name contient une valeur valide dans build_output.", "fr",
                "contient une valeur valide dans", ["user_name", "build_output"]),
            ("Spanish prose wins over the English model",
                "La variable user_name contiene un valor válido en build_output.", "es",
                "La variable contiene un valor válido en", ["user_name", "build_output"]),
            ("Numbers remain attached to the same language phrase",
                "The variable user_name contains 123 values in build_output.", "en-us",
                "The variable contains 123 values in", ["user_name", "build_output"]),
            ("Initials and titles keep their protected periods",
                "Dr. Morgan and J. R. R. Tolkien checked user_name before opening build_output.", "en-us",
                "Dr. Morgan and J. R. R. Tolkien checked before opening", ["user_name", "build_output"]),
            ("Numeric version keeps its pronunciation exception",
                "The version v1.0.9 contains the expected changes in build_output.", "en-us",
                "The version contains the expected changes in", ["v1.0.9", "build_output"]),
            ("Ordinary speech still uses one analysis phrase",
                "The variable contains the requested output.", "en-us",
                "The variable contains the requested output", [])
        };

        foreach (var fixture in fixtures)
        foreach (bool early in new[] { false, true })
        {
            yield return ($"Automatic technical language: {fixture.Name}; early={early}", () =>
            {
                logger.Phrases.Clear();
                var chunks = chunker.Split(fixture.Input, early);
                Require(chunks.Count == 1, "An in-sentence technical token created a sentence cut.");
                var tokens = detector.ProcessTextToLanguageTokens(chunks[0].Text);
                Require(string.Concat(tokens.Select(t => t.Text)) == fixture.Input, "Source text was lost or reordered.");
                Require(tokens.Where(t => !t.IsPunctuationOrSpace).All(t => t.DetectedLanguage == fixture.Language),
                    $"Wrong language: {Describe(tokens)}");
                Require(tokens.Where(t => t.IsTechnical).Select(t => t.Text).SequenceEqual(fixture.Technical),
                    "The technical speech parts differ from the fixture.");
                Require(logger.Phrases.Select(Words).SequenceEqual([fixture.Analysis]),
                    $"Unexpected analysis boundaries: {string.Join(" | ", logger.Phrases)}");
            });
        }

        var boundaryCases = new (string Name, string Input, string[] Analysis, (string Text, string Language)[] Anchors)[]
        {
            ("Comma permits a same-script language change",
                "The variable user_name contains the expected value, la variable autre_nom contient une valeur française.",
                ["The variable contains the expected value", "la variable contient une valeur française"],
                [("user_name", "en-us"), ("expected value", "en-us"), ("autre_nom", "fr"), ("valeur française", "fr")]),
            ("Semicolon permits a same-script language change",
                "The variable user_name contains the expected value; la variable autre_nom contient une valeur française.",
                ["The variable contains the expected value", "la variable contient une valeur française"],
                [("user_name", "en-us"), ("autre_nom", "fr")]),
            ("Quotes preserve an independent foreign phrase",
                "The result is stored in user_name \"la variable autre_nom contient une valeur française\" and the English report remains available.",
                ["The result is stored in", "la variable contient une valeur française", "and the English report remains available"],
                [("user_name", "en-us"), ("autre_nom", "fr"), ("English report", "en-us")]),
            ("Curly quotes preserve an independent foreign phrase",
                "The result is stored in user_name “la variable autre_nom contient une valeur française” and the English report remains available.",
                ["The result is stored in", "la variable contient une valeur française", "and the English report remains available"],
                [("user_name", "en-us"), ("autre_nom", "fr"), ("English report", "en-us")]),
            ("A technical script change still separates language analysis",
                "The variable user_name contains дані_значення and the English report remains available.",
                ["The variable contains", "and the English report remains available"],
                [("user_name", "en-us"), ("дані_значення", "uk"), ("English report", "en-us")]),
            ("Latin identifiers do not absorb Cyrillic prose",
                "Українська змінна дані_значення містить build_output і правильно зберігає результат.",
                ["Українська змінна містить", "і правильно зберігає результат"],
                [("дані_значення", "uk"), ("build_output", "en-us"), ("зберігає результат", "uk")])
        };

        foreach (var fixture in boundaryCases)
        foreach (bool early in new[] { false, true })
        {
            yield return ($"Automatic technical boundary: {fixture.Name}; early={early}", () =>
            {
                logger.Phrases.Clear();
                var tokens = chunker.Split(fixture.Input, early)
                    .SelectMany(chunk => detector.ProcessTextToLanguageTokens(chunk.Text)).ToArray();
                foreach (var anchor in fixture.Anchors)
                {
                    Require(tokens.Any(t => t.Text.Contains(anchor.Text, StringComparison.Ordinal) &&
                        t.DetectedLanguage == anchor.Language), $"Missing language anchor {anchor}: {Describe(tokens)}");
                }
                Require(logger.Phrases.Select(Words).SequenceEqual(fixture.Analysis),
                    $"Unexpected analysis boundaries: {string.Join(" | ", logger.Phrases)}");
            });
        }

        yield return ("Automatic technical language reaches ordinary eSpeak calls", () =>
        {
            var bridge = new EspeakWrapper();
            var config = Config();
            var phonemizer = new UnifiedPhonemizer(bridge, new DynamicPunctuationMapper(config), config, detector);
            phonemizer.GetPhonemes("The variable user_name contains build_output.");
            Require(bridge.Calls.Any(c => c.Text.Contains("contains", StringComparison.Ordinal)), "The verb disappeared.");
            Require(bridge.Calls.All(c => c.Voice == "en-us"), "An ordinary English fragment used a foreign voice.");
            Require(bridge.CharacterCalls.Count == 1 && bridge.CharacterCalls[0] == ('_', "en-us"),
                "Shared language analysis changed symbol naming or its cache.");
        });

        foreach (string text in new[] { "user_name build_output", "_ $ ^", "123 user_name 456",
            "français_utilisateur données_sortie" })
        {
            yield return ($"Technical-only parts do not trigger a lexical vote: {text}", () =>
            {
                logger.Phrases.Clear();
                var tokens = detector.ProcessTextToLanguageTokens(text);
                Require(string.Concat(tokens.Select(t => t.Text)) == text, "Neutral or numeric content was lost.");
                Require(logger.Phrases.Count == 0, "Technical syntax was sent for phrase-level language detection.");
                Require(tokens.Where(t => !t.IsPunctuationOrSpace).All(t => t.DetectedLanguage == "en-us"),
                    "A context-free technical part lost the configured model language.");
            });
        }

        foreach (string language in new[] { "en", "fr", "es", "uk" })
        {
            yield return ($"Forced language remains authoritative across technical/script boundaries: {language}", () =>
            {
                logger.Phrases.Clear();
                const string text = "The variable user_name contains дані_значення.";
                var tokens = detector.ProcessTextToLanguageTokens(text, language);
                string expected = language == "en" ? "en-us" : language;
                Require(string.Concat(tokens.Select(t => t.Text)) == text, "Forced tokenization lost source text.");
                Require(tokens.Where(t => !t.IsPunctuationOrSpace).All(t => t.DetectedLanguage == expected),
                    "Automatic context replaced the forced language.");
                Require(logger.Phrases.Count == 0, "A forced language still invoked local detection.");
            });
        }

        yield return ("Technical language analysis uses request-local pending parts", () =>
        {
            var concurrentDetector = new MixedLanguagePhonemizer(Settings(), "en-us",
                NullLogger<MixedLanguagePhonemizer>.Instance, chunker);
            bool passed = Enumerable.Range(0, 32).AsParallel().All(index =>
            {
                string text = $"The variable user_{index} contains build_{index}.";
                var tokens = concurrentDetector.ProcessTextToLanguageTokens(text);
                return string.Concat(tokens.Select(t => t.Text)) == text &&
                    tokens.Where(t => !t.IsPunctuationOrSpace).All(t => t.DetectedLanguage == "en-us");
            });
            Require(passed, "Concurrent language analysis mixed request buffers or languages.");
        });
    }

    private static PhonemizerSettings Settings() => new() { SupportedLanguages = ["en", "fr", "es", "uk"] };

    private static PiperConfig Config() => new()
    {
        Espeak = new EspeakConfig { Voice = "en-us" },
        PhonemeIdMap = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ .?!,_^$"
            .Distinct().ToDictionary(c => c.ToString(), c => new[] { (int)c })
    };

    private static string Words(string text) => string.Join(" ", text.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries));

    private static string Describe(IEnumerable<TextChunk> tokens) => string.Join(" | ",
        tokens.Select(t => $"{t.DetectedLanguage}:{t.Text}"));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class AnalysisLogger : ILogger<MixedLanguagePhonemizer>
    {
        internal List<string> Phrases { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var fields = values.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (fields.ContainsKey("LetterCount") && fields.TryGetValue("Text", out object? text) && text is string phrase)
                Phrases.Add(phrase);
        }
    }
}
