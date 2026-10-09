using ONNX_Runner.Models;
using ONNX_Runner.Services;

var config = new PiperConfig
{
    PhonemeIdMap = new Dictionary<string, int[]>
    {
        ["."] = [1],
        ["?"] = [2],
        ["!"] = [3],
        [","] = [4],
        [" "] = [5],
        ["a"] = [6]
    }
};

var mapper = new DynamicPunctuationMapper(config);
string path = Path.Combine(Path.GetTempPath(), $"tsubaki-punctuation-{Guid.NewGuid():N}.json");
File.WriteAllText(path, """
    {
      "AdditionalSentenceTerminators": ["⸼"],
      "AdditionalPauseMarks": ["⸭"],
      "AdditionalQuestionMarks": ["⸘"]
    }
    """);
TextChunkerRules customRules;
try
{
    customRules = TextChunkerRules.LoadOrDefault(path);
}
finally
{
    File.Delete(path);
}
var customMapper = new DynamicPunctuationMapper(config, customRules);

var punctuationCases = new (string Name, string Input, string Expected, DynamicPunctuationMapper Mapper)[]
{
    ("Thai angkhankhu", "๚", ".", mapper),
    ("Thai khomut", "๛", ".", mapper),
    ("Limbu exclamation", "᥄", "!", mapper),
    ("Limbu question", "᥅", "?", mapper),
    ("Vertical question", "︖", "?", mapper),
    ("Small exclamation", "﹗", "!", mapper),
    ("Khmer ending", "៚", ".", mapper),
    ("Custom terminator", "⸼", ".", customMapper),
    ("Custom pause", "⸭", ",", customMapper),
    ("Thai sentences", "สวัสดี๚ ทดสอบ๛", "สวัสดี. ทดสอบ.", mapper),
    ("Ordinary question", "Hello?", "Hello?", mapper),
    ("Custom typed question", "⸘", "?", customMapper),
    ("Compatibility full stop", "﹒", ".", mapper),
    ("One dot leader", "․", ".", mapper),
    ("Vertical full stop", "︒", ".", mapper),
    ("Double exclamation", "‼", "!", mapper),
    ("Double question", "⁇", "??", mapper),
    ("Question exclamation order", "⁈", "?!", mapper),
    ("Exclamation question order", "⁉", "!?", mapper),
    ("Quoted prose keeps words separated", "word\"word\"word", "word word word", mapper),
    ("Lexical hyphen survives normalization", "state-of-the-art", "state-of-the-art", mapper),
    ("Model-native punctuation survives", "Hello?!", "Hello?!", mapper),
    ("Dotted technical text is retained", "config.prod.json", "config.prod.json", mapper)
};

bool failuresOnly = args.Any(value => value.Equals("--failures-only", StringComparison.OrdinalIgnoreCase));
int failed = 0;

foreach (var test in punctuationCases)
{
    string actual = test.Mapper.Normalize(test.Input);
    if (actual == test.Expected)
    {
        if (!failuresOnly) Console.WriteLine($"PASS {test.Name}");
        continue;
    }

    failed++;
    Console.Error.WriteLine($"FAIL {test.Name}: expected '{test.Expected}', actual '{actual}'");
}

// Explicit Unicode inputs also verify the punctuation inventory independently of the catalog.
var terminalCases = new (char Mark, string Expected)[]
{
    ('\u07F9', "!"), ('\u0700', "."), ('\u0701', "."), ('\u0702', "."),
    ('\u0965', "."), ('\u0DF4', "."), ('\u0E5B', "."), ('\u17D4', "."),
    ('\u17D5', "."), ('\u17DA', "."), ('\u1944', "!"), ('\u1945', "?"),
    ('\u1B5F', "."), ('\u1C7E', "."), ('\u1C7F', "."), ('\u1803', "."),
    ('\u1809', "."), ('\u166E', "."), ('\uA60E', "."), ('\uA60F', "?"),
    ('\uA6F3', "."), ('\uA6F7', "?"), ('\uA8CE', "."), ('\uA8CF', "."),
    ('\uA95F', "."), ('\uA9C9', "."), ('\uAA5D', "."), ('\uAA5E', "."),
    ('\uAA5F', "."), ('\uABEB', "."), ('\u1367', "?"), ('\u1368', "."),
    ('\uFF61', "."), ('\uFE12', "."), ('\uFE15', "!"), ('\uFE16', "?"),
    ('\uFE57', "!"), ('\uFE56', "?")
};
foreach (var test in terminalCases)
{
    string actual = mapper.Normalize(test.Mark.ToString());
    if (actual == test.Expected) continue;

    failed++;
    Console.Error.WriteLine($"FAIL U+{(int)test.Mark:X4}: expected '{test.Expected}', actual '{actual}'");
}

var nativeConfig = new PiperConfig
{
    PhonemeIdMap = new Dictionary<string, int[]> { ["๚"] = [1], [" "] = [2] }
};
var nativeMapper = new DynamicPunctuationMapper(nativeConfig);
if (nativeMapper.Normalize("Hello.") != "Hello๚" || nativeMapper.Normalize("สวัสดี๚") != "สวัสดี๚")
{
    failed++;
    Console.Error.WriteLine("FAIL Native Thai sentence marker is preserved and used as period fallback");
}

int additionalTotal = 0;
void Check(string name, bool passed)
{
    additionalTotal++;
    if (passed)
    {
        if (!failuresOnly) Console.WriteLine($"PASS {name}");
        return;
    }
    failed++;
    Console.Error.WriteLine($"FAIL {name}");
}

foreach (string input in new[] { "", " \t", "A complete sentence.", "state-of-the-art", "OʼNeill arrived." })
{
    Check($"Unchanged input is returned by reference: '{input}'", ReferenceEquals(mapper.Normalize(input), input));
}
Check("Normalization is idempotent across the explicit corpus", punctuationCases.All(test =>
{
    string first = test.Mapper.Normalize(test.Input);
    return test.Mapper.Normalize(first) == first;
}));

var sparseCases = new (string Name, string[] Symbols, string Input, string Expected)[]
{
    ("Space-only inventory degrades unsupported terminals", [" "], "Hello. Next?", "Hello  Next "),
    ("Colon-only inventory supplies the weakest available boundary", [":"], "Hello. Next?", "Hello: Next:"),
    ("Semicolon inventory supplies period fallback", [";", " "], "Hello. Next?", "Hello; Next;"),
    ("Model-native compound question wins", ["⁇", " "], "Ready?", "Ready⁇"),
    ("Model-native ellipsis wins", ["…", " "], "Wait...", "Wait…"),
    ("Missing period preserves an extended ellipsis pause", [";", " "], "Wait…", "Wait  ;")
};
foreach (var test in sparseCases)
{
    var sparseMapper = new DynamicPunctuationMapper(new PiperConfig
    {
        PhonemeIdMap = test.Symbols.ToDictionary(symbol => symbol, _ => new[] { 1 })
    });
    string actual = sparseMapper.Normalize(test.Input);
    Check(test.Name, actual == test.Expected && sparseMapper.Normalize(actual) == actual);
}

var controlMapper = new DynamicPunctuationMapper(new PiperConfig
{
    PhonemeIdMap = new Dictionary<string, int[]> { ["_"] = [1], ["^"] = [2], ["$"] = [3], [" "] = [4] }
});
Check("Model control tokens remain unavailable to input text", controlMapper.Normalize("word_^$word") == "wordword");

int total = punctuationCases.Length + terminalCases.Length + 1 + additionalTotal;
Console.WriteLine($"Result: {total - failed}/{total} passed");
return failed == 0 ? 0 : 1;
