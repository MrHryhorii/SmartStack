using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ONNX_Runner.Models;

/// <summary>
/// Immutable shared catalog for sentence boundaries, abbreviations, and punctuation semantics.
/// Optional JSON adds entries to the built-in categories before requests are accepted.
/// </summary>
public sealed class TextChunkerRules
{
    public enum PunctuationKind : byte
    {
        Period, Question, Exclamation, Interrobang, DoubleQuestion,
        QuestionExclamation, ExclamationQuestion, Comma, Semicolon, Colon,
        Ellipsis, Dash, OpenBracket, CloseBracket, WordSeparator,
        OpeningQuestion, OpeningExclamation
    }

    [Flags]
    private enum PunctuationRole
    {
        None = 0, Sentence = 1, PeriodLike = 2, Ellipsis = 4, Clause = 8,
        Pause = 16, Closing = 32, Line = 64, SeparatedTerminal = 128,
        ReportingDash = 256, LexicalHyphen = 512, VisualQuote = 1024,
        FormatControl = 2048, ContextualQuestion = 4096, TechnicalMarker = 8192,
        LexicalApostrophe = 16384, TechnicalSuffix = 32768
    }

    private readonly record struct PunctuationGroup(
        PunctuationKind? Kind, PunctuationRole Roles, string Marks);

    private readonly record struct PunctuationEntry(
        PunctuationKind? Kind, PunctuationRole Roles);

    // Each group declares both spelling behavior and boundary roles in one place.
    private static readonly PunctuationGroup[] BuiltInPunctuation =
    [
        new(PunctuationKind.Period, PunctuationRole.PeriodLike | PunctuationRole.Sentence | PunctuationRole.TechnicalMarker, "."),
        new(PunctuationKind.Period, PunctuationRole.PeriodLike | PunctuationRole.Sentence, "․"),
        new(PunctuationKind.Period, PunctuationRole.Sentence, "。"),
        new(PunctuationKind.Period, PunctuationRole.PeriodLike | PunctuationRole.Sentence, "．"),
        new(PunctuationKind.Period, PunctuationRole.Sentence, "｡۔։।॥"),
        new(PunctuationKind.Period, PunctuationRole.Pause, "།༎༏༐༑༒"),
        new(PunctuationKind.Period, PunctuationRole.Sentence, "។៕៚။።፨᙮᠃᠉"),
        new(PunctuationKind.Period, PunctuationRole.None, "꓿"),
        new(PunctuationKind.Period, PunctuationRole.Sentence, "꘎෴׃܀܁܂๚๛᭟᱾᱿꛳꣎꣏꥟꧉꩝꩞꩟꯫"),
        new(PunctuationKind.Period, PunctuationRole.PeriodLike | PunctuationRole.Sentence, "﹒"),
        new(PunctuationKind.Period, PunctuationRole.Sentence, "︒"),
        new(PunctuationKind.Question, PunctuationRole.Sentence | PunctuationRole.SeparatedTerminal | PunctuationRole.TechnicalMarker | PunctuationRole.TechnicalSuffix, "?"),
        new(PunctuationKind.Question, PunctuationRole.Sentence, "？؟;"),
        new(PunctuationKind.Question, PunctuationRole.None, "՞"),
        new(PunctuationKind.Question, PunctuationRole.Sentence, "፧꘏"),
        new(PunctuationKind.Question, PunctuationRole.None, "⸮"),
        new(PunctuationKind.Question, PunctuationRole.Sentence, "᥅꛷﹖︖"),
        new(PunctuationKind.Exclamation, PunctuationRole.Sentence | PunctuationRole.SeparatedTerminal | PunctuationRole.TechnicalSuffix, "!"),
        new(PunctuationKind.Exclamation, PunctuationRole.Sentence, "！"),
        new(PunctuationKind.Exclamation, PunctuationRole.None, "՜"),
        new(PunctuationKind.Exclamation, PunctuationRole.Sentence, "߹"),
        new(PunctuationKind.Exclamation, PunctuationRole.Sentence | PunctuationRole.SeparatedTerminal, "‼"),
        new(PunctuationKind.Exclamation, PunctuationRole.Sentence, "᥄﹗︕"),
        new(PunctuationKind.Interrobang, PunctuationRole.Sentence | PunctuationRole.SeparatedTerminal, "‽"),
        new(PunctuationKind.DoubleQuestion, PunctuationRole.Sentence | PunctuationRole.SeparatedTerminal, "⁇"),
        new(PunctuationKind.QuestionExclamation, PunctuationRole.Sentence | PunctuationRole.SeparatedTerminal, "⁈"),
        new(PunctuationKind.ExclamationQuestion, PunctuationRole.Sentence | PunctuationRole.SeparatedTerminal, "⁉"),
        new(PunctuationKind.Comma, PunctuationRole.Clause | PunctuationRole.Pause | PunctuationRole.TechnicalSuffix, ","),
        new(PunctuationKind.Comma, PunctuationRole.Clause | PunctuationRole.Pause, "，、"),
        new(PunctuationKind.Comma, PunctuationRole.None, "､"),
        new(PunctuationKind.Comma, PunctuationRole.Clause | PunctuationRole.Pause, "،՝፣၊᠂᠈߸"),
        new(PunctuationKind.Comma, PunctuationRole.None, "꓾꘍"),
        new(PunctuationKind.Semicolon, PunctuationRole.Clause | PunctuationRole.ContextualQuestion | PunctuationRole.Pause | PunctuationRole.TechnicalSuffix, ";"),
        new(PunctuationKind.Semicolon, PunctuationRole.Clause | PunctuationRole.Pause, "；؛፤"),
        new(PunctuationKind.Colon, PunctuationRole.Clause | PunctuationRole.Pause | PunctuationRole.TechnicalMarker | PunctuationRole.TechnicalSuffix, ":"),
        new(PunctuationKind.Colon, PunctuationRole.Clause | PunctuationRole.Pause, "：፥፦៖᠄܃܄܅܆܇܈܉"),
        new(PunctuationKind.Ellipsis, PunctuationRole.Ellipsis | PunctuationRole.Sentence, "…‥⋯᠅"),
        new(PunctuationKind.Dash, PunctuationRole.Pause | PunctuationRole.ReportingDash, "—–"),
        new(PunctuationKind.Dash, PunctuationRole.ReportingDash, "―"),
        new(PunctuationKind.Dash, PunctuationRole.None, "‒⸺⸻〜～"),
        new(PunctuationKind.OpenBracket, PunctuationRole.None, "([{（［｛〈《【〔〖〘〚⟨⟦⟪〈"),
        new(PunctuationKind.CloseBracket, PunctuationRole.Closing, ")]}）］｝〉》】〕〗〙〛"),
        new(PunctuationKind.CloseBracket, PunctuationRole.None, "⟩⟧⟫〉"),
        new(PunctuationKind.WordSeparator, PunctuationRole.None, "·・･፡"),
        new(PunctuationKind.OpeningQuestion, PunctuationRole.None, "¿𞥟"),
        new(PunctuationKind.OpeningExclamation, PunctuationRole.None, "¡𞥞"),
        new(null, PunctuationRole.Line | PunctuationRole.Sentence, "\n\r"),
        new(null, PunctuationRole.Closing | PunctuationRole.VisualQuote, "\""),
        new(null, PunctuationRole.TechnicalMarker, "#&"),
        new(null, PunctuationRole.Closing | PunctuationRole.VisualQuote | PunctuationRole.LexicalApostrophe, "'"),
        new(null, PunctuationRole.LexicalHyphen | PunctuationRole.Pause | PunctuationRole.ReportingDash, "-"),
        new(null, PunctuationRole.TechnicalMarker, "/=@\\"),
        new(null, PunctuationRole.Line | PunctuationRole.Sentence, "\u0085"),
        new(null, PunctuationRole.Closing | PunctuationRole.VisualQuote, "«»"),
        new(null, PunctuationRole.Clause | PunctuationRole.Pause, "·"),
        new(null, PunctuationRole.LexicalHyphen, "֊־"),
        new(null, PunctuationRole.FormatControl, "\u061C"),
        new(null, PunctuationRole.Pause, "༔᛫᛬᛭᪨᪩᪪᪫᭞"),
        new(null, PunctuationRole.FormatControl, "\u200E\u200F"),
        new(null, PunctuationRole.LexicalHyphen, "‐‑"),
        new(null, PunctuationRole.Closing | PunctuationRole.VisualQuote, "‘"),
        new(null, PunctuationRole.Closing | PunctuationRole.VisualQuote | PunctuationRole.LexicalApostrophe, "’"),
        new(null, PunctuationRole.Closing | PunctuationRole.VisualQuote, "‚‛“”„‟"),
        new(null, PunctuationRole.Line | PunctuationRole.Sentence, "\u2028\u2029"),
        new(null, PunctuationRole.Closing | PunctuationRole.VisualQuote, "‹›"),
        new(null, PunctuationRole.FormatControl, "\u2066\u2067\u2068\u2069"),
        new(null, PunctuationRole.VisualQuote, "❛❜❝❞「"),
        new(null, PunctuationRole.Closing | PunctuationRole.VisualQuote, "」"),
        new(null, PunctuationRole.VisualQuote, "『"),
        new(null, PunctuationRole.Closing | PunctuationRole.VisualQuote, "』"),
        new(null, PunctuationRole.VisualQuote, "〝"),
        new(null, PunctuationRole.Closing | PunctuationRole.VisualQuote, "〞〟"),
        new(null, PunctuationRole.LexicalHyphen, "゠"),
        new(null, PunctuationRole.Pause, "꧈"),
        new(null, PunctuationRole.Closing, "﴾﴿"),
        new(null, PunctuationRole.Clause | PunctuationRole.Pause, "︐︑︓︔"),
        new(null, PunctuationRole.Closing, "︶︸︺︼︾﹀﹂﹄"),
        new(null, PunctuationRole.Clause | PunctuationRole.Pause, "﹐﹑﹔﹕"),
        new(null, PunctuationRole.Closing, "﹚﹜﹞"),
        new(null, PunctuationRole.FormatControl, "\uFEFF"),
        new(null, PunctuationRole.LexicalApostrophe, "ʼ"),
    ];

    private static readonly string[] BuiltInCommonAbbreviations =
    [
        // ================= SHARED / CROSS-LANGUAGE =================
        "dr", "prof", "fr", "mgr", "mag",
        "gen", "cap", "st", "ste", "av",

        // ================= ENGLISH =================
        "mr", "mrs", "ms", "mx", "messrs", "mmes", "msgr", "esq", "hon", "rev", "sr", "jr",
        "rep", "sen", "gov", "pres", "amb", "sec", "min", "cmdr", "cllr", "ald", "jud",
        "col", "maj", "capt", "lieut", "lt", "sgt", "cpl", "pvt", "adm", "brig", "comm",
        "ceo", "cfo", "cto", "vp", "dir", "asst", "assoc",
        "mt", "ft", "ave", "blvd", "rd", "hwy", "bldg", "apt", "vs", "etc", "approx",

        // ================= SPANISH / PORTUGUESE =================
        "srta", "sra", "don", "doña", "dra", "profa",
        "ldo", "lda", "arq", "gral", "sto", "sta", "pza",

        // ================= FRENCH =================
        "mme", "mlle", "pr", "me", "vve", "bd",

        // ================= ITALIAN =================
        "sig", "sigra", "dott", "dottssa", "avv",
        "arch", "geom", "rag", "profssa", "mons", "ten",

        // ================= GERMAN / DUTCH =================
        "herr", "frau", "ing", "frl", "dipl", "med",
        "dhr", "mevr", "mej", "ir", "drs", "ds", "univ", "bakk",

        // ================= NORDIC =================
        "hr", "fru", "frk", "kapt",

        // ================= POLISH / CZECH / SLOVAK =================
        "doc", "inż", "mec", "dyr", "św", "bł", "bc",
        "mudr", "mvdr", "judr", "phdr", "rndr", "inž", "pan", "pani",

        // ================= SHARED UKRAINIAN / RUSSIAN =================
        "проф", "доц", "акад", "гр", "тов", "пом", "д-р",
        "бул", "обл", "пл", "кв", "р-н", "рис", "табл", "напр",

        // ================= UKRAINIAN =================
        "пан", "пані", "дир", "інж", "зав", "заст", "ст", "мол",
        "вул", "пров", "просп", "ім", "буд", "мкр", "пт",
        "сел", "смт", "див", "пор",

        // ================= RUSSIAN =================
        "г", "ул", "пр", "пер", "наб", "ш", "пос", "дер",
        "стр", "корп", "см", "ср", "т", "д", "п", "тп", "св",

        // ================= TURKISH =================
        "doç", "yrd", "uzm", "öğr", "mh", "sk", "cd", "bul", "sok",

        // ================= HEBREW =================
        "דר", "פרופ", "עו",

        // ================= THAI =================
        // Academic / professional
        "ดร", "ผศ", "รศ",

        // Medical
        "นพ", "พญ", "ทพ", "ทพญ", "ภก", "ภญ",

        // ================= VIETNAMESE =================
        "ts", "ths", "gs", "pgs",

        // ================= ROMANIAN =================
        "dl", "dna", "dv", "dvs", "intr", "șos", "nr",

        // ================= HUNGARIAN =================
        "id", "ifj", "özv", "gr", "hg", "ig", "igh", "mb", "okl",

        // ================= INDONESIAN =================
        "hj", "kh",
    ];

    private static readonly string[] BuiltInPrefixAbbreviations =
    [
        // Shared / English
        "dr", "prof", "fr", "mgr", "mag", "st", "ste",
        "mr", "mrs", "ms", "mx", "messrs", "mmes", "msgr", "hon", "rev",
        "gen", "cap", "cmdr", "col", "maj", "capt", "lieut", "lt", "sgt", "cpl",
        "pvt", "adm", "brig", "comm", "rep", "sen", "gov", "pres", "amb", "mt",

        // Spanish / Portuguese
        "sr", "srta", "sra", "don", "doña", "dra", "profa", "ldo", "lda", "arq", "gral",
        "sto", "sta",

        // French / Italian / German / Dutch / Nordic
        "mme", "mlle", "pr", "me", "vve",
        "sig", "sigra", "dott", "dottssa", "avv", "arch", "profssa", "mons", "geom", "rag", "ten",
        "herr", "frau", "frl", "dhr", "mevr", "mej", "ing", "ir", "drs", "ds",
        "hr", "fru", "frk", "kapt",

        // Central / Eastern European
        "doc", "inż", "mec", "mudr", "mvdr", "judr", "phdr", "rndr", "św", "bł",
        "проф", "доц", "акад", "тов", "пан", "пані", "д-р",

        // Turkish / Hebrew / Thai
        "doç", "yrd", "uzm",
        "דר", "פרופ",
        "ดร", "ผศ", "รศ", "นพ", "พญ", "ทพ", "ทพญ", "ภก", "ภญ",

        // Vietnamese academic titles (normally capitalized before a name)
        "ts", "ths", "gs", "pgs",
    ];

    private static readonly string[] BuiltInNameBindingAbbreviations =
    [
        // Lowercase honorifics and academic titles are conventional in several languages.
        "dr", "prof", "mgr", "doc", "inż", "mec", "hab",
        "sr", "sra", "srta", "dra", "dott", "dottssa", "sig", "sigra", "avv",

        // Polish: saint/blessed, street/avenue and named locations.
        "św", "bł", "ul", "al",

        // Hungarian: age and widowhood prefixes before a personal name.
        "id", "ifj", "özv",

        // Ukrainian and Russian: streets, named institutions, and saint.
        "вул", "пров", "просп", "бул", "пл", "ім", "проф", "доц", "акад", "д-р",
        "ул", "пер", "наб", "св", "г-н", "г-жа",

        // Georgian: saint before a personal name (წმ. გიორგი).
        "წმ",
    ];

    private static readonly string[] BuiltInNumberBindingAbbreviations =
    [
        "no", "num", "nr", "fig", "vol", "ch", "chap", "sec", "art", "pp", "p",
        "kl", "ca", "circa", "approx", "ed", "eds",
    ];

    private static readonly string[] BuiltInIntroductoryAbbreviations =
    [
        "e.g", "i.e", "cf", "vs", "f.eks", "bl.a", "t.ex", "z.b", "d.h", "u.a",
    ];

    public static TextChunkerRules Default { get; } = new(new Extensions());

    public SearchValues<char> SentenceTerminators { get; }
    public SearchValues<char> SentenceBoundaryCandidates { get; }
    public SearchValues<char> PeriodLikeMarks { get; }
    public SearchValues<char> EllipsisMarks { get; }
    public SearchValues<char> ClausePunctuation { get; }
    public SearchValues<char> PauseMarks { get; }
    public SearchValues<char> ClosingPunctuation { get; }
    public SearchValues<char> LineBoundaries { get; }
    public SearchValues<char> SeparatedTerminals { get; }
    public SearchValues<char> ReportingDashes { get; }
    public SearchValues<char> LexicalHyphens { get; }
    public SearchValues<char> VisualQuotes { get; }
    public SearchValues<char> BoundaryFormatControls { get; }
    public SearchValues<char> TechnicalMarkers { get; }
    public SearchValues<char> LexicalApostrophes { get; }
    public SearchValues<char> TechnicalSuffixMarks { get; }
    public FrozenDictionary<Rune, PunctuationKind> SemanticKinds { get; }
    public FrozenSet<string> CommonAbbreviations { get; }
    public FrozenSet<string> PrefixAbbreviations { get; }
    public FrozenSet<string> NameBindingAbbreviations { get; }
    public FrozenSet<string> NumberBindingAbbreviations { get; }
    public FrozenSet<string> IntroductoryAbbreviations { get; }
    public FrozenSet<string> KnownAbbreviations { get; }
    private readonly FrozenDictionary<PunctuationKind, ImmutableArray<Rune>> _semanticMarks;

    internal ImmutableArray<Rune> GetSemanticMarks(PunctuationKind kind) => _semanticMarks[kind];

    private TextChunkerRules(Extensions additions)
    {
        var punctuation = new Dictionary<Rune, PunctuationEntry>();
        foreach (var group in BuiltInPunctuation)
        {
            foreach (Rune mark in group.Marks.EnumerateRunes())
            {
                punctuation.Add(mark, new PunctuationEntry(group.Kind, group.Roles));
            }
        }

        AddMarks(punctuation, additions.AdditionalSentenceTerminators, PunctuationRole.Sentence, PunctuationKind.Period);
        AddMarks(punctuation, additions.AdditionalPeriodLikeMarks, PunctuationRole.Sentence | PunctuationRole.PeriodLike, PunctuationKind.Period, strictKind: true);
        AddMarks(punctuation, additions.AdditionalEllipsisMarks, PunctuationRole.Sentence | PunctuationRole.Ellipsis, PunctuationKind.Ellipsis, strictKind: true);
        AddMarks(punctuation, additions.AdditionalQuestionMarks, PunctuationRole.Sentence, PunctuationKind.Question, strictKind: true);
        AddMarks(punctuation, additions.AdditionalExclamationMarks, PunctuationRole.Sentence, PunctuationKind.Exclamation, strictKind: true);
        AddMarks(punctuation, additions.AdditionalClausePunctuation, PunctuationRole.Clause | PunctuationRole.Pause, PunctuationKind.Semicolon);
        AddMarks(punctuation, additions.AdditionalPauseMarks, PunctuationRole.Pause, PunctuationKind.Comma);
        AddMarks(punctuation, additions.AdditionalClosingPunctuation, PunctuationRole.Closing, PunctuationKind.CloseBracket);

        foreach (var (mark, entry) in punctuation)
        {
            if ((entry.Roles & PunctuationRole.Sentence) != 0 &&
                ((entry.Roles & (PunctuationRole.Clause | PunctuationRole.Closing | PunctuationRole.ContextualQuestion)) != 0))
            {
                throw new ArgumentException($"Conflicting sentence and clause/closing roles for punctuation '{mark}'.");
            }
        }

        SentenceTerminators = SelectMarks(punctuation, PunctuationRole.Sentence);
        SentenceBoundaryCandidates = SelectMarks(punctuation, PunctuationRole.Sentence | PunctuationRole.ContextualQuestion);
        PeriodLikeMarks = SelectMarks(punctuation, PunctuationRole.PeriodLike);
        EllipsisMarks = SelectMarks(punctuation, PunctuationRole.Ellipsis);
        ClausePunctuation = SelectMarks(punctuation, PunctuationRole.Clause);
        PauseMarks = SelectMarks(punctuation, PunctuationRole.Pause);
        ClosingPunctuation = SelectMarks(punctuation, PunctuationRole.Closing);
        LineBoundaries = SelectMarks(punctuation, PunctuationRole.Line);
        SeparatedTerminals = SelectMarks(punctuation, PunctuationRole.SeparatedTerminal);
        ReportingDashes = SelectMarks(punctuation, PunctuationRole.ReportingDash);
        LexicalHyphens = SelectMarks(punctuation, PunctuationRole.LexicalHyphen);
        VisualQuotes = SelectMarks(punctuation, PunctuationRole.VisualQuote);
        BoundaryFormatControls = SelectMarks(punctuation, PunctuationRole.FormatControl);
        TechnicalMarkers = SelectMarks(punctuation, PunctuationRole.TechnicalMarker);
        LexicalApostrophes = SelectMarks(punctuation, PunctuationRole.LexicalApostrophe);
        TechnicalSuffixMarks = SelectMarks(punctuation, PunctuationRole.TechnicalSuffix);
        SemanticKinds = punctuation.Where(pair => pair.Value.Kind.HasValue)
            .ToFrozenDictionary(pair => pair.Key, pair => pair.Value.Kind!.Value);
        _semanticMarks = punctuation.Where(pair => pair.Value.Kind.HasValue)
            .GroupBy(pair => pair.Value.Kind!.Value)
            .ToFrozenDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToImmutableArray());

        PrefixAbbreviations = MergeAbbreviations(BuiltInPrefixAbbreviations, additions.AdditionalPrefixAbbreviations);
        NameBindingAbbreviations = MergeAbbreviations(BuiltInNameBindingAbbreviations, additions.AdditionalNameBindingAbbreviations);
        NumberBindingAbbreviations = MergeAbbreviations(BuiltInNumberBindingAbbreviations, additions.AdditionalNumberBindingAbbreviations);
        IntroductoryAbbreviations = MergeAbbreviations(BuiltInIntroductoryAbbreviations, additions.AdditionalIntroductoryAbbreviations);

        // Keep the established phonemizer table; specialized sentence hints do not silently
        // change built-in pronunciation. Explicit user additions are shared by every consumer.
        CommonAbbreviations = MergeAbbreviations(BuiltInCommonAbbreviations,
            (additions.AdditionalAbbreviations ?? []).Concat(additions.AdditionalPrefixAbbreviations ?? [])
            .Concat(additions.AdditionalNameBindingAbbreviations ?? [])
            .Concat(additions.AdditionalNumberBindingAbbreviations ?? [])
            .Concat(additions.AdditionalIntroductoryAbbreviations ?? []));
        KnownAbbreviations = CommonAbbreviations.Concat(PrefixAbbreviations)
            .Concat(NameBindingAbbreviations).Concat(NumberBindingAbbreviations)
            .Concat(IntroductoryAbbreviations).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Missing JSON selects the complete built-in catalog without creating a file.</summary>
    public static TextChunkerRules LoadOrDefault(string path)
    {
        if (!File.Exists(path)) return Default;

        try
        {
            using var stream = File.OpenRead(path);
            var additions = JsonSerializer.Deserialize<Extensions>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new InvalidDataException($"TextChunker rules file is empty: {path}");
            return new TextChunkerRules(additions);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Invalid TextChunker rules file: {path}", error);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException($"Invalid TextChunker rules entries: {path}. {error.Message}", error);
        }
    }

    private static SearchValues<char> SelectMarks(
        Dictionary<Rune, PunctuationEntry> punctuation, PunctuationRole roles)
    {
        return SearchValues.Create(punctuation
            .Where(pair => pair.Key.IsBmp && (pair.Value.Roles & roles) != 0)
            .Select(pair => (char)pair.Key.Value).ToArray());
    }

    private static void AddMarks(Dictionary<Rune, PunctuationEntry> punctuation,
        IEnumerable<string>? additions, PunctuationRole roles, PunctuationKind kind, bool strictKind = false)
    {
        if (additions is null) return;

        foreach (string? value in additions)
        {
            if (value is null || value.Length != 1 || char.IsSurrogate(value[0]) ||
                !(char.IsPunctuation(value[0]) || DefaultLineBoundary(value[0])))
            {
                throw new ArgumentException($"Punctuation entries must be individual BMP punctuation marks or line boundaries: '{value}'.");
            }

            var mark = new Rune(value[0]);
            punctuation.TryGetValue(mark, out var existing);
            if (strictKind && existing.Kind.HasValue && existing.Kind.Value != kind)
            {
                throw new ArgumentException($"Punctuation '{mark}' already belongs to {existing.Kind}; it cannot also belong to {kind}.");
            }

            punctuation[mark] = new PunctuationEntry(existing.Kind ?? kind, existing.Roles | roles);
        }
    }

    private static bool DefaultLineBoundary(char mark)
    {
        return BuiltInPunctuation.Any(group =>
            (group.Roles & PunctuationRole.Line) != 0 && group.Marks.Contains(mark));
    }

    private FrozenSet<string> MergeAbbreviations(IEnumerable<string> builtIn, IEnumerable<string>? additions)
    {
        var merged = new HashSet<string>(builtIn, StringComparer.OrdinalIgnoreCase);
        if (additions is null) return merged.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        foreach (string? value in additions)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            ReadOnlySpan<char> normalized = value.AsSpan().Trim();
            while (!normalized.IsEmpty && PeriodLikeMarks.Contains(normalized[^1]))
            {
                normalized = normalized[..^1];
            }

            if (normalized.IsEmpty || normalized.Length > 128)
            {
                throw new ArgumentException($"Invalid abbreviation: '{value}'.");
            }

            string item = normalized.ToString();
            if (item.Any(char.IsWhiteSpace)) throw new ArgumentException($"Invalid abbreviation: '{value}'.");
            merged.Add(item);
        }

        return merged.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    // This DTO describes additions only; the runtime catalog is immutable.
    private sealed class Extensions
    {
        public List<string>? AdditionalSentenceTerminators { get; set; }
        public List<string>? AdditionalPeriodLikeMarks { get; set; }
        public List<string>? AdditionalEllipsisMarks { get; set; }
        public List<string>? AdditionalQuestionMarks { get; set; }
        public List<string>? AdditionalExclamationMarks { get; set; }
        public List<string>? AdditionalClausePunctuation { get; set; }
        public List<string>? AdditionalPauseMarks { get; set; }
        public List<string>? AdditionalClosingPunctuation { get; set; }
        public List<string>? AdditionalAbbreviations { get; set; }
        public List<string>? AdditionalPrefixAbbreviations { get; set; }
        public List<string>? AdditionalNameBindingAbbreviations { get; set; }
        public List<string>? AdditionalNumberBindingAbbreviations { get; set; }
        public List<string>? AdditionalIntroductoryAbbreviations { get; set; }
    }
}
