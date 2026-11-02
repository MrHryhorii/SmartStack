using PunctuationKind = ONNX_Runner.Models.Text.TextChunkerRules.PunctuationKind;

namespace ONNX_Runner.Models.Text;

/// <summary>Built-in punctuation roles and abbreviation lists, independent of optional JSON extensions.</summary>
internal static class TextChunkerDefaults
{
    [Flags]
    internal enum PunctuationRole
    {
        None = 0, Sentence = 1, PeriodLike = 2, Ellipsis = 4, Clause = 8,
        Pause = 16, Closing = 32, Line = 64, SeparatedTerminal = 128,
        ReportingDash = 256, LexicalHyphen = 512, VisualQuote = 1024,
        FormatControl = 2048, ContextualQuestion = 4096, TechnicalMarker = 8192,
        LexicalApostrophe = 16384, TechnicalSuffix = 32768, TechnicalToken = 65536, Opening = 131072
    }

    internal readonly record struct PunctuationGroup(
        PunctuationKind? Kind, PunctuationRole Roles, string Marks);

    // Each group declares both spelling behavior and boundary roles in one place.
    internal static readonly PunctuationGroup[] Punctuation =
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
        new(PunctuationKind.OpenBracket, PunctuationRole.Opening, "([{（［｛〈《【〔〖〘〚⟨⟦⟪〈"),
        new(PunctuationKind.CloseBracket, PunctuationRole.Closing, ")]}）］｝〉》】〕〗〙〛"),
        new(PunctuationKind.CloseBracket, PunctuationRole.None, "⟩⟧⟫〉"),
        new(PunctuationKind.WordSeparator, PunctuationRole.None, "·・･፡"),
        new(PunctuationKind.OpeningQuestion, PunctuationRole.None, "¿𞥟"),
        new(PunctuationKind.OpeningExclamation, PunctuationRole.None, "¡𞥞"),
        new(null, PunctuationRole.Line | PunctuationRole.Sentence, "\n\r"),
        new(null, PunctuationRole.Closing | PunctuationRole.VisualQuote, "\""),
        new(null, PunctuationRole.TechnicalMarker | PunctuationRole.TechnicalToken, "#&"),
        new(null, PunctuationRole.Closing | PunctuationRole.VisualQuote | PunctuationRole.LexicalApostrophe, "'"),
        new(null, PunctuationRole.LexicalHyphen | PunctuationRole.Pause | PunctuationRole.ReportingDash, "-"),
        new(null, PunctuationRole.TechnicalMarker | PunctuationRole.TechnicalToken, "/=@\\"),
        new(null, PunctuationRole.TechnicalToken, "_+$^<>|~%×÷≤≥≠"),
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

    internal static readonly string[] CommonAbbreviations =
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

    internal static readonly string[] PrefixAbbreviations =
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

    internal static readonly string[] NameBindingAbbreviations =
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

    internal static readonly string[] NumberBindingAbbreviations =
    [
        "no", "num", "nr", "fig", "vol", "ch", "chap", "sec", "art", "pp", "p",
        "kl", "ca", "circa", "approx", "ed", "eds",
    ];

    internal static readonly string[] IntroductoryAbbreviations =
    [
        "e.g", "i.e", "cf", "vs", "f.eks", "bl.a", "t.ex", "z.b", "d.h", "u.a",
    ];

}
