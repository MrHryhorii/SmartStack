global using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

// Measure synchronous managed allocations after initialization and JIT warmup.
// Delegates, input fixtures, catalogs, and models are constructed outside measured loops.
int iterations = 5_000;
foreach (string argument in args)
{
    if (!argument.StartsWith("--iterations=", StringComparison.OrdinalIgnoreCase) ||
        !int.TryParse(argument.AsSpan("--iterations=".Length), out iterations) || iterations < 1)
    {
        Console.Error.WriteLine("Usage: --iterations=<positive integer>");
        return 2;
    }
}

var chunker = new TextChunker(new ChunkerSettings { MaxChunkLength = 200 });
var boundedChunker = new TextChunker(new ChunkerSettings { MaxChunkLength = 64 });
var detector = new MixedLanguagePhonemizer(new PhonemizerSettings { SupportedLanguages = ["en", "uk"] },
    "en-us", NullLogger<MixedLanguagePhonemizer>.Instance, chunker);
var scriptOnly = new MixedLanguagePhonemizer(new PhonemizerSettings(),
    "en-us", NullLogger<MixedLanguagePhonemizer>.Instance, chunker);
var mapper = new DynamicPunctuationMapper(new PiperConfig
{
    PhonemeIdMap = new Dictionary<string, int[]>
    {
        ["."] = [1], ["?"] = [2], ["!"] = [3], [","] = [4], [" "] = [5]
    }
});

const string plain = "This is a complete sentence.";
const string abbreviations = "Dr. Morgan met Prof. Lee at St. Peter Hospital.";
const string nameBinding = "id. Kovács spoke.";
const string reference = "Read Fig. 2 at 3.14, then continue. Next sentence.";
const string technical = "Please open config.prod.json before the next build.";
const string technicalSequence = "The variable user_name contains build_output.";
const string rewritten = "Hello๚ Next… word\"word.";
string unicode = string.Concat(Enumerable.Repeat("a\u0301", 96)) + "。";
string[] modelParts = ["en", "us"];

var scenarios = new (string Name, Func<int> Run, long? ByteBudget)[]
{
    ("technical/classification", () => TechnicalTextRecognizer.TryGetSpanLength("user_name".AsSpan(), 0, TextChunkerRules.Default, out int n) ? n : 0, 0),
    ("technical/ordinary", () => TechnicalTextRecognizer.TryGetSpanLength("well-known".AsSpan(), 0, TextChunkerRules.Default, out int n) ? n : 0, 0),
    ("technical/number-exception", () => TechnicalTextRecognizer.ShouldSpeak("v1.0.9".AsSpan(), TextChunkerRules.Default) ? 1 : 0, 0),
    ("boundary/abbreviation", () => chunker.IsIntraSentencePeriod(abbreviations.AsSpan(), 2) ? 1 : 0, 0),
    ("boundary/name-binding", () => chunker.IsIntraSentencePeriod(nameBinding.AsSpan(), 2) ? 1 : 0, 0),
    ("boundary/lowercase-letter", () => chunker.IsIntraSentencePeriod("Letter z. Continue.".AsSpan(), 8) ? 1 : 0, 0),
    ("language/inherited-code", () => MixedLanguagePhonemizer.ResolveForcedLanguageCode("en", "en-us", modelParts).Length, 0),
    ("punctuation/unchanged", () => mapper.Normalize(plain).Length, 0),
    ("punctuation/rewrite", () => mapper.Normalize(rewritten).Length, null),
    // The result list and its one-element array are necessary output allocations.
    // A 96-byte budget on .NET 10 permits those, but rejects copying either fixture string
    // or reserving the default four-element array. It is not a zero-output-allocation claim.
    ("chunker/single", () => chunker.Split(plain).Count, 96),
    ("chunker/abbreviations", () => chunker.Split(abbreviations).Count, 96),
    ("chunker/early", () => chunker.Split(reference, earlySplit: true).Count, null),
    ("chunker/unicode-emergency", () => boundedChunker.Split(unicode).Count, null),
    ("tokenizer/forced-single", () => detector.ProcessTextToLanguageTokens(plain, "en").Count, null),
    ("tokenizer/forced-technical", () => detector.ProcessTextToLanguageTokens(technical, "en").Count, null),
    ("tokenizer/forced-technical-sequence", () => detector.ProcessTextToLanguageTokens(technicalSequence, "en").Count, null),
    ("tokenizer/automatic-single", () => detector.ProcessTextToLanguageTokens(plain).Count, null),
    ("tokenizer/automatic-technical-sequence", () => detector.ProcessTextToLanguageTokens(technicalSequence).Count, null),
    ("tokenizer/script-only-single", () => scriptOnly.ProcessTextToLanguageTokens(plain).Count, null),
    ("tokenizer/script-only-technical-sequence", () => scriptOnly.ProcessTextToLanguageTokens(technicalSequence).Count, null)
};

Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}; {RuntimeInformation.OSDescription}");
Console.WriteLine($"Configuration: {BuildConfiguration()}; iterations: {iterations}; warmup: 2048 per scenario");
Console.WriteLine("Scenario\tB/op\tns/op\tAllocation budget");
long checksum = 0;
int failures = 0;
foreach (var scenario in scenarios)
{
    for (int index = 0; index < 2048; index++) checksum += scenario.Run();

    long before = GC.GetAllocatedBytesForCurrentThread();
    long start = Stopwatch.GetTimestamp();
    for (int index = 0; index < iterations; index++) checksum += scenario.Run();
    long elapsed = Stopwatch.GetTimestamp() - start;
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    bool passed = !scenario.ByteBudget.HasValue || allocated <= scenario.ByteBudget.Value * iterations;
    if (!passed) failures++;
    string budget = scenario.ByteBudget.HasValue ? $"{(passed ? "PASS" : "FAIL")} <= {scenario.ByteBudget.Value} B/op" : "reported";
    Console.WriteLine($"{scenario.Name}\t{(double)allocated / iterations:0.##}\t{(double)elapsed / Stopwatch.Frequency * 1_000_000_000 / iterations:0.##}\t{budget}");
}

GC.KeepAlive(checksum);
int contracts = scenarios.Count(s => s.ByteBudget.HasValue);
Console.WriteLine($"Result: {contracts - failures}/{contracts} allocation budgets passed; checksum: {checksum}");
return failures == 0 ? 0 : 1;

static string BuildConfiguration()
{
#if DEBUG
    return "Debug (use Release for comparisons)";
#else
    return "Release";
#endif
}
