global using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

// Test structural speech parts independently of model inference and audio synthesis.
// Forced fixtures verify source layout; TechnicalLanguageChecks separately exercises real Lingua
// decisions without a forced language, including competing languages with the same script.
var detector = new MixedLanguagePhonemizer(
    new PhonemizerSettings { SupportedLanguages = ["en", "uk"] },
    "en-us",
    NullLogger<MixedLanguagePhonemizer>.Instance);

var cases = new (string Name, string Input, string[] ExpectedPhrases, string[] ExpectedPunctuation)[]
{
    (
        "Numeric references remain in one language phrase",
        "They reviewed report No. 12 and Fig. 3 before the meeting.",
        ["They reviewed report No. 12 and Fig. 3 before the meeting"],
        ["."]),
    (
        "Figures volumes and pages keep their numbers",
        "See Fig. 2 in Vol. 3 on p. 15.",
        ["See Fig. 2 in Vol. 3 on p. 15"],
        ["."]),
    (
        "Number title at sentence start",
        "No. 12 is available.",
        ["No. 12 is available"],
        ["."]),
    (
        "Name binding remains intact",
        "Dr. Morgan met Prof. Lee at St. Peter Hospital.",
        ["Dr. Morgan met Prof. Lee at St. Peter Hospital"],
        ["."]),
    (
        "Dotted initials stay in one phrase",
        "The U.S. Army met Q.R.S. Group.",
        ["The U.S. Army met Q.R.S. Group"],
        ["."]),
    (
        "Decimals stay inside words",
        "The response took 2.75 seconds.",
        ["The response took 2.75 seconds"],
        ["."]),
    (
        "Real sentence boundaries remain punctuation",
        "Dr. Morgan spoke. Next step.",
        ["Dr. Morgan spoke", " Next step"],
        [".", "."]),
    (
        "Period after numeric reference can end sentence",
        "See Fig. 2 in Vol. 3 on p. 15. Read it.",
        ["See Fig. 2 in Vol. 3 on p. 15", " Read it"],
        [".", "."]),
    (
        "Explicit explanatory abbreviation before capitalized word",
        "Use e.g. Python today.",
        ["Use e.g. Python today"],
        ["."]),
    (
        "Clause comma remains language boundary",
        "Use e.g., Python today.",
        ["Use e.g.", " Python today"],
        [",", "."]),
    (
        "Ukrainian references and numbers",
        "Поглянь на рис. 3 і табл. 2.",
        ["Поглянь на рис. 3 і табл. 2"],
        ["."]),
    (
        "Polish abbreviated street and name",
        "Na ul. Mickiewicza jest sklep.",
        ["Na ul. Mickiewicza jest sklep"],
        ["."]),
    (
        "Georgian initial does not break detection context",
        "გ. გიორგი მოვიდა.",
        ["გ. გიორგი მოვიდა"],
        ["."]),
    (
        "Dotted technical file names keep their structure",
        "The file config.json loaded.",
        ["The file ", "config.json", " loaded"],
        ["."]),
};

cases = [..cases,
    ("Lexical hyphen remains part of a word", "Use state-of-the-art design.",
        ["Use state-of-the-art design"], ["."]),
    ("Contraction apostrophe remains lexical", "They don't change.",
        ["They don't change"], ["."]),
    ("Modifier apostrophe remains lexical", "The name OʼNeill stays together.",
        ["The name OʼNeill stays together"], ["."]),
    ("Lowercase letter has the same boundary as the chunker", "The final letter is z. Continue.",
        ["The final letter is z", " Continue"], [".", "."]),
    ("Decomposed uppercase initial remains lexical", "A\u0301. Novak spoke.",
        ["A\u0301. Novak spoke"], ["."])
];

bool failuresOnly = args.Any(argument =>
    argument.Equals("--failures-only", StringComparison.OrdinalIgnoreCase));

int failures = 0;

foreach (var test in cases)
{
    string language = test.Name switch
    {
        "Ukrainian references and numbers" => "uk",
        "Polish abbreviated street and name" => "pl",
        "Georgian initial does not break detection context" => "ka",
        _ => "en"
    };

    var tokens = detector.ProcessTextToLanguageTokens(test.Input, language);
    var phrases = tokens.Where(token => !token.IsPunctuationOrSpace).Select(token => token.Text).ToArray();
    var punctuation = tokens.Where(token => token.IsPunctuationOrSpace).Select(token => token.Text).ToArray();

    bool passed = phrases.SequenceEqual(test.ExpectedPhrases) &&
                  punctuation.SequenceEqual(test.ExpectedPunctuation) &&
                  string.Concat(tokens.Select(token => token.Text)) == test.Input;

    if (passed)
    {
        if (!failuresOnly)
        {
            Console.WriteLine($"PASS {test.Name}");
        }

        continue;
    }

    failures++;
    Console.Error.WriteLine($"FAIL {test.Name}");
    Console.Error.WriteLine($"  Expected phrases: {string.Join(" | ", test.ExpectedPhrases)}");
    Console.Error.WriteLine($"  Actual phrases:   {string.Join(" | ", phrases)}");
    Console.Error.WriteLine($"  Expected marks:   {string.Join(" | ", test.ExpectedPunctuation)}");
    Console.Error.WriteLine($"  Actual marks:     {string.Join(" | ", punctuation)}");
    Console.Error.WriteLine($"  Preserved text:   {string.Concat(tokens.Select(token => token.Text)) == test.Input}");
}

// Script changes must remain valid detector boundaries even when abbreviations are protected.
const string mixedScriptInput = "Please check No. 12, потім рис. 3.";
var mixedScriptTokens = detector.ProcessTextToLanguageTokens(mixedScriptInput);
var mixedScriptPhrases = mixedScriptTokens.Where(token => !token.IsPunctuationOrSpace).ToArray();
bool mixedScriptPassed =
    string.Concat(mixedScriptTokens.Select(token => token.Text)) == mixedScriptInput &&
    mixedScriptPhrases.Any(token => token.Text.Contains("No. 12", StringComparison.Ordinal)) &&
    mixedScriptPhrases.Any(token => token.Text.Contains("рис. 3", StringComparison.Ordinal)) &&
    mixedScriptPhrases.Any(token => token.Script == "Latin") &&
    mixedScriptPhrases.Any(token => token.Script == "Cyrillic");

if (!mixedScriptPassed)
{
    failures++;
    Console.Error.WriteLine("FAIL Mixed-script numeric references");
    Console.Error.WriteLine($"  Tokens: {string.Join(" | ", mixedScriptTokens.Select(token => $"{token.Script}:{token.Text}"))}");
}
else if (!failuresOnly)
{
    Console.WriteLine("PASS Mixed-script numeric references");
}

int additionalTotal = 0;
foreach (var test in SharedRulesChecks.Cases().Concat(TechnicalSpeechChecks.Cases())
    .Concat(TechnicalLanguageChecks.Cases()).Concat(LanguageDetectionSettingsChecks.Cases())
    .Concat(ScriptOnlyLanguageChecks.Cases()))
{
    additionalTotal++;
    try
    {
        test.Check();
        if (!failuresOnly) Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}");
    }
}

int total = cases.Length + 1 + additionalTotal;
Console.WriteLine($"Result: {total - failures}/{total} passed");
return failures == 0 ? 0 : 1;
