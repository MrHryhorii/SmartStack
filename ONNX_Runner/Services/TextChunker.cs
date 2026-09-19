using System.Globalization;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// High-performance multilingual text chunker for TTS synthesis.
/// Detects semantic sentence boundaries while protecting abbreviations, initials,
/// technical tokens, contextual ellipses, punctuation clusters, and script-specific
/// sentence terminators.
/// </summary>
public class TextChunker(ChunkerSettings settings)
{
    // =========================================================================================
    // SINGLE SOURCE OF TRUTH: Global multilingual array of sentence-boundary candidates.
    // Made PUBLIC so the synthesis pipeline can use it for smart context detection without duplicating data.
    // =========================================================================================
    public static readonly char[] SentenceTerminators =
    [
        // Common punctuation and explicit line/paragraph boundaries
        '.', '!', '?',
        '\u2024',               // ONE DOT LEADER — period-like compatibility form
        '\n', '\r', '\u0085',  // LF, CR, NEXT LINE
        '\u2028', '\u2029',    // LINE SEPARATOR, PARAGRAPH SEPARATOR

        // Ellipsis variants — contextual candidates: they may mark hesitation inside a sentence
        '…',  // U+2026  HORIZONTAL ELLIPSIS
        '‥',  // U+2025  TWO DOT LEADER
        '⋯',  // U+22EF  MIDLINE HORIZONTAL ELLIPSIS
        '᠅',  // U+1805  MONGOLIAN FOUR DOTS

        // Compound Latin punctuation — frequently used in chat and AI-generated text
        '‼',  // U+203C  DOUBLE EXCLAMATION MARK
        '‽',  // U+203D  INTERROBANG
        '⁇',  // U+2047  DOUBLE QUESTION MARK
        '⁈',  // U+2048  QUESTION EXCLAMATION MARK
        '⁉',  // U+2049  EXCLAMATION QUESTION MARK

        // East Asian / compatibility forms (Chinese, Japanese, Korean)
        '。',  // U+3002  IDEOGRAPHIC FULL STOP
        '！',  // U+FF01  FULLWIDTH EXCLAMATION MARK
        '？',  // U+FF1F  FULLWIDTH QUESTION MARK
        '｡',   // U+FF61  HALFWIDTH IDEOGRAPHIC FULL STOP
        '．',  // U+FF0E  FULLWIDTH FULL STOP
        '﹒',  // U+FE52  SMALL FULL STOP
        '﹗',  // U+FE57  SMALL EXCLAMATION MARK
        '﹖',  // U+FE56  SMALL QUESTION MARK
        '︒',  // U+FE12  PRESENTATION FORM FOR VERTICAL IDEOGRAPHIC FULL STOP
        '︕',  // U+FE15  PRESENTATION FORM FOR VERTICAL EXCLAMATION MARK
        '︖',  // U+FE16  PRESENTATION FORM FOR VERTICAL QUESTION MARK

        // Arabic / Persian / Urdu
        '؟',  // U+061F  ARABIC QUESTION MARK
        '۔',  // U+06D4  ARABIC FULL STOP

        // N'Ko
        '߹',  // U+07F9  NKO EXCLAMATION MARK

        // Syriac
        '܀',  // U+0700  SYRIAC END OF PARAGRAPH
        '܁',  // U+0701  SYRIAC SUPRALINEAR FULL STOP
        '܂',  // U+0702  SYRIAC SUBLINEAR FULL STOP

        // Hebrew Biblical punctuation
        '׃',  // U+05C3  HEBREW PUNCTUATION SOF PASUQ

        // Devanagari and danda-using Indic scripts
        '।',  // U+0964  DEVANAGARI DANDA
        '॥',  // U+0965  DEVANAGARI DOUBLE DANDA

        // Sinhala (traditional punctuation)
        '෴',  // U+0DF4  SINHALA PUNCTUATION KUNDDALIYA

        // Thai (traditional paragraph/text endings)
        '๚',  // U+0E5A  THAI CHARACTER ANGKHANKHU
        '๛',  // U+0E5B  THAI CHARACTER KHOMUT

        // Armenian
        // NOTE: U+055E ARMENIAN QUESTION MARK is intentionally NOT here: it is a tonal mark
        // placed above the stressed vowel inside a word, not a sentence-boundary character.
        '։',  // U+0589  ARMENIAN FULL STOP

        // Greek — visually resembles a semicolon but functions as a question mark
        ';',  // U+037E  GREEK QUESTION MARK

        // Ethiopic (Amharic, Tigrinya)
        '።',  // U+1362  ETHIOPIC FULL STOP
        '፧',  // U+1367  ETHIOPIC QUESTION MARK
        '፨',  // U+1368  ETHIOPIC PARAGRAPH SEPARATOR

        // Myanmar (Burmese)
        // U+104A LITTLE SECTION is a clause separator and belongs in PauseMarks.
        '။',  // U+104B  MYANMAR SIGN SECTION

        // Khmer
        '។',  // U+17D4  KHMER SIGN KHAN
        '៕',  // U+17D5  KHMER SIGN BARIYOOSAN
        '៚',  // U+17DA  KHMER SIGN KOOMUUT

        // Limbu
        '᥄',  // U+1944  LIMBU EXCLAMATION MARK
        '᥅',  // U+1945  LIMBU QUESTION MARK

        // Balinese
        '᭟',  // U+1B5F  BALINESE CARIK PAREREN

        // Ol Chiki (Santali)
        '᱾',  // U+1C7E  OL CHIKI PUNCTUATION MUCAAD
        '᱿',  // U+1C7F  OL CHIKI PUNCTUATION DOUBLE MUCAAD

        // Mongolian
        '᠃',  // U+1803  MONGOLIAN FULL STOP
        '᠉',  // U+1809  MONGOLIAN MANCHU FULL STOP

        // Canadian Syllabics — Cree, Inuktitut, etc.
        '᙮',  // U+166E  CANADIAN SYLLABICS FULL STOP

        // Vai
        '꘎',  // U+A60E  VAI FULL STOP
        '꘏',  // U+A60F  VAI QUESTION MARK

        // Bamum
        '꛳',  // U+A6F3  BAMUM FULL STOP
        '꛷',  // U+A6F7  BAMUM QUESTION MARK

        // Saurashtra
        '꣎',  // U+A8CE  SAURASHTRA DANDA
        '꣏',  // U+A8CF  SAURASHTRA DOUBLE DANDA

        // Rejang
        '꥟',  // U+A95F  REJANG SECTION MARK

        // Javanese
        '꧉',  // U+A9C9  JAVANESE PADA LUNGSI

        // Cham
        '꩝',  // U+AA5D  CHAM PUNCTUATION DANDA
        '꩞',  // U+AA5E  CHAM PUNCTUATION DOUBLE DANDA
        '꩟',  // U+AA5F  CHAM PUNCTUATION TRIPLE DANDA

        // Meetei Mayek
        '꯫',  // U+ABEB  MEETEI MAYEK CHEIKHEI
    ];

    // High-performance search values dynamically created from the array above to prevent duplication.
    private static readonly System.Buffers.SearchValues<char> s_sentenceTerminators = System.Buffers.SearchValues.Create(SentenceTerminators);

    // Period-like characters share the same ambiguity rules as ASCII '.': abbreviations,
    // initials, decimal/version/domain separators, and compatibility-width text.
    private static readonly char[] PeriodLikeMarks =
    [
        '.',       // U+002E FULL STOP
        '\u2024', // U+2024 ONE DOT LEADER
        '﹒',      // U+FE52 SMALL FULL STOP
        '．',      // U+FF0E FULLWIDTH FULL STOP
    ];

    private static readonly System.Buffers.SearchValues<char> s_periodLikeMarks =
        System.Buffers.SearchValues.Create(PeriodLikeMarks);

    private static readonly char[] EllipsisMarks =
    [
        '…', // U+2026 HORIZONTAL ELLIPSIS
        '‥', // U+2025 TWO DOT LEADER
        '⋯', // U+22EF MIDLINE HORIZONTAL ELLIPSIS
        '᠅', // U+1805 MONGOLIAN FOUR DOTS
    ];

    private static readonly System.Buffers.SearchValues<char> s_ellipsisMarks =
        System.Buffers.SearchValues.Create(EllipsisMarks);

    // Limits the length of a single audio generation task to prevent GPU timeouts.
    private readonly int _maxLength = settings.MaxChunkLength > 50 ? settings.MaxChunkLength : 250;

    // Symbol used to glue chunks together when an emergency split is necessary
    // (e.g., splitting in the middle of a long sentence without good break points).
    // A hyphen signals a soft, continuous break to the TTS engine rather than a hard pause.
    private const string EmergencyGlue = "-";

    // Punctuation groups are composed once at type initialization. EarlySplit uses only
    // conservative clause boundaries; emergency splitting keeps the complete legacy set.
    private static readonly char[] ClausePunctuation =
    [
        // Common clause boundaries
        ',', // U+002C  COMMA
        ';', // U+003B  SEMICOLON
        ':', // U+003A  COLON

        // Greek
        '·', // U+0387  GREEK ANO TELEIA

        // Armenian
        '՝', // U+055D  ARMENIAN COMMA

        // Arabic
        '،', // U+060C  ARABIC COMMA
        '؛', // U+061B  ARABIC SEMICOLON

        // N'Ko
        '߸', // U+07F8  NKO COMMA

        // Syriac colon-family phrase separators
        '܃', // U+0703  SYRIAC SUPRALINEAR COLON
        '܄', // U+0704  SYRIAC SUBLINEAR COLON
        '܅', // U+0705  SYRIAC HORIZONTAL COLON
        '܆', // U+0706  SYRIAC COLON SKEWED LEFT
        '܇', // U+0707  SYRIAC COLON SKEWED RIGHT
        '܈', // U+0708  SYRIAC SUPRALINEAR COLON SKEWED LEFT
        '܉', // U+0709  SYRIAC SUBLINEAR COLON SKEWED RIGHT

        // Ethiopic
        '፣', // U+1363  ETHIOPIC COMMA
        '፤', // U+1364  ETHIOPIC SEMICOLON
        '፥', // U+1365  ETHIOPIC COLON
        '፦', // U+1366  ETHIOPIC PREFACE COLON

        // Myanmar
        '၊', // U+104A  MYANMAR SIGN LITTLE SECTION

        // Khmer
        '៖', // U+17D6  KHMER SIGN CAMNUC PII KUUH

        // East Asian punctuation
        '，', // U+FF0C  FULLWIDTH COMMA
        '、', // U+3001  IDEOGRAPHIC COMMA
        '；', // U+FF1B  FULLWIDTH SEMICOLON
        '：', // U+FF1A  FULLWIDTH COLON
        '﹐', // U+FE50  SMALL COMMA
        '﹑', // U+FE51  SMALL IDEOGRAPHIC COMMA
        '﹔', // U+FE54  SMALL SEMICOLON
        '﹕', // U+FE55  SMALL COLON
        '︐', // U+FE10  PRESENTATION FORM FOR VERTICAL COMMA
        '︑', // U+FE11  PRESENTATION FORM FOR VERTICAL IDEOGRAPHIC COMMA
        '︓', // U+FE13  PRESENTATION FORM FOR VERTICAL COLON
        '︔', // U+FE14  PRESENTATION FORM FOR VERTICAL SEMICOLON

        // Mongolian
        '᠂', // U+1802  MONGOLIAN COMMA
        '᠄', // U+1804  MONGOLIAN COLON
        '᠈', // U+1808  MONGOLIAN MANCHU COMMA
    ];

    private static readonly char[] DashPunctuation =
    [
        '-', // U+002D  HYPHEN-MINUS
        '–', // U+2013  EN DASH
        '—', // U+2014  EM DASH
    ];

    private static readonly char[] StructuralPunctuation =
    [
        // Tibetan shad-family marks
        '།', // U+0F0D  TIBETAN MARK SHAD
        '༎', // U+0F0E  TIBETAN MARK NYIS SHAD
        '༏', // U+0F0F  TIBETAN MARK TSHEG SHAD
        '༐', // U+0F10  TIBETAN MARK NYIS TSHEG SHAD
        '༑', // U+0F11  TIBETAN MARK RIN CHEN SPUNGS SHAD
        '༒', // U+0F12  TIBETAN MARK RGYA GRAM SHAD
        '༔', // U+0F14  TIBETAN MARK GTER TSHEG

        // Tai Tham structural punctuation
        '᪨', // U+1AA8  TAI THAM SIGN KAAN
        '᪩', // U+1AA9  TAI THAM SIGN KAANKUU
        '᪪', // U+1AAA  TAI THAM SIGN SATKAAN
        '᪫', // U+1AAB  TAI THAM SIGN SATKAANKUU

        // Balinese / Javanese structural punctuation
        '᭞', // U+1B5E  BALINESE CARIK SIKI
        '꧈', // U+A9C8  JAVANESE PADA LINGSA

        // Runic structural punctuation
        '᛫', // U+16EB  RUNIC SINGLE PUNCTUATION
        '᛬', // U+16EC  RUNIC MULTIPLE PUNCTUATION
        '᛭', // U+16ED  RUNIC CROSS PUNCTUATION
    ];

    // Conservative set used only for the optional one-time EarlySplit.
    private static readonly char[] EarlySplitPunctuation =
    [
        .. ClausePunctuation,
    ];

    // Full legacy set used by emergency MaxChunkLength splitting and abbreviation checks.
    private static readonly char[] PauseMarks =
    [
        .. ClausePunctuation,
        .. DashPunctuation,
        .. StructuralPunctuation,
    ];

    // SearchValues are built once and reused on the hot path.
    private static readonly System.Buffers.SearchValues<char> s_earlySplitPunctuation =
        System.Buffers.SearchValues.Create(EarlySplitPunctuation);

    private static readonly System.Buffers.SearchValues<char> s_pauseMarks =
        System.Buffers.SearchValues.Create(PauseMarks);

    // Closing quotes and brackets that can follow a sentence terminator (e.g., "Hello." or (Ready.)).
    // Used to look ahead and prevent periods inside quotes/brackets from being misidentified as abbreviations.
    // Made PUBLIC so the synthesis pipeline can peel back trailing quotes/brackets when checking whether a chunk
    // truly ends on a sentence terminator, mirroring SentenceTerminators above.
    public static readonly char[] ClosingPunctuation =
    [
        // Quotes / guillemets. Some glyphs can open in one language and close in another,
        // so accepting both directions after a terminator is deliberate.
        '"', '\'', '’', '”', '“', '‘', '„', '‚', '‟', '‛',
        '»', '›', '«', '‹',

        // ASCII / fullwidth / small brackets
        ')', ']', '}',
        '）', '］', '｝',
        '﹚', '﹜', '﹞',

        // CJK quotes and brackets
        '」', '』', '〕', '】', '》', '〉', '〗', '〙', '〛', '〞', '〟',

        // Vertical presentation forms of closing brackets
        '︶', '︸', '︺', '︼', '︾', '﹀', '﹂', '﹄',

        // Ornate Arabic parentheses
        '﴿', '﴾',
    ];

    private static readonly System.Buffers.SearchValues<char> s_closingPunctuation =
        System.Buffers.SearchValues.Create(ClosingPunctuation);

    /// <summary>
    /// Contextual abbreviation hints used to distinguish in-sentence periods from real
    /// sentence endings. An abbreviation may still terminate a sentence when its
    /// surrounding context indicates a new sentence.
    /// </summary>
    public static readonly HashSet<string> CommonAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
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
    };

    /// <summary>
    /// Abbreviations that normally introduce a following name/title rather than ending a sentence.
    /// For scripts with case, the source token must actually be capitalized before this stronger
    /// no-break rule is used. This keeps lowercase units such as "ms." from behaving like "Ms.".
    /// </summary>
    private static readonly HashSet<string> PrefixAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        // Shared / English
        "dr", "prof", "fr", "mgr", "mag",
        "mr", "mrs", "ms", "mx", "messrs", "mmes", "msgr", "hon", "rev",
        "gen", "cap", "cmdr", "col", "maj", "capt", "lieut", "lt", "sgt", "cpl",
        "pvt", "adm", "brig", "comm", "rep", "sen", "gov", "pres", "amb",

        // Spanish / Portuguese
        "srta", "sra", "don", "doña", "dra", "profa", "ldo", "lda", "arq", "gral",

        // French / Italian / German / Dutch / Nordic
        "mme", "mlle", "pr", "me",
        "sig", "sigra", "dott", "dottssa", "avv", "arch", "profssa", "mons",
        "herr", "frau", "frl", "dhr", "mevr", "mej",
        "hr", "fru", "frk", "kapt",

        // Central / Eastern European
        "doc", "inż", "mec", "mudr", "mvdr", "judr", "phdr", "rndr", "inž",
        "проф", "доц", "акад", "тов", "пан", "пані",

        // Turkish / Hebrew / Thai
        "doç", "yrd", "uzm",
        "דר", "פרופ",
        "ดร", "ผศ", "รศ", "นพ", "พญ", "ทพ", "ทพญ", "ภก", "ภญ",
    };

    /// <summary>
    /// One text chunk plus the boundary information already known by the chunker.
    /// </summary>
    public readonly record struct TextChunk(string Text, bool IsSentenceFinished);

    /// <summary>
    /// Chunks text while respecting semantic sentence boundaries. When EarlySplit is enabled,
    /// only the first non-empty semantic sentence gets one opportunity to end early at a
    /// conservative clause boundary. All following text uses normal sentence chunking and
    /// emergency MaxChunkLength splitting.
    /// </summary>
    public List<TextChunk> Split(string text, bool earlySplit = false)
    {
        var result = new List<TextChunk>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        ReadOnlySpan<char> textSpan = text.AsSpan();
        int currentIndex = 0;

        // EarlySplit is handled before the normal loop so disabled/consumed requests pay
        // no additional branch or punctuation search for subsequent sentences.
        if (earlySplit)
        {
            while (currentIndex < textSpan.Length)
            {
                int firstEndIndex = FindSentenceEnd(textSpan, currentIndex, out bool firstSentenceFinished);
                ReadOnlySpan<char> firstSpan = textSpan[currentIndex..firstEndIndex].Trim();

                // Ignore empty leading boundaries without consuming the one allowed EarlySplit.
                if (firstSpan.IsEmpty)
                {
                    currentIndex = firstEndIndex;
                    continue;
                }

                // MaxChunkLength is the normal synthesis upper bound. EarlySplit candidates are
                // validated contextually so punctuation embedded in technical text (https://,
                // foo:bar, etc.) is not treated as a clause boundary merely because the symbol
                // itself is present.
                int earlySearchEnd = Math.Min(firstEndIndex, currentIndex + _maxLength);
                int earlyEndIndex = FindEarlySplitEnd(textSpan, currentIndex, earlySearchEnd, firstEndIndex);

                if (earlyEndIndex >= 0)
                {
                    ReadOnlySpan<char> earlyChunk = textSpan[currentIndex..earlyEndIndex].Trim();
                    if (!earlyChunk.IsEmpty)
                    {
                        result.Add(new TextChunk(earlyChunk.ToString(), false));
                    }

                    currentIndex = earlyEndIndex;
                }
                else
                {
                    AddSentence(textSpan[currentIndex..firstEndIndex], firstSentenceFinished, result);
                    currentIndex = firstEndIndex;
                }

                break;
            }
        }

        while (currentIndex < textSpan.Length)
        {
            int endIndex = FindSentenceEnd(textSpan, currentIndex, out bool isSentenceFinished);
            AddSentence(textSpan[currentIndex..endIndex], isSentenceFinished, result);
            currentIndex = endIndex;
        }

        return result;
    }

    /// <summary>
    /// Finds the next semantic sentence boundary. Characters in SentenceTerminators are candidates;
    /// ambiguous period-like marks, ellipses, and technical-token punctuation are validated in context.
    /// </summary>
    private static int FindSentenceEnd(ReadOnlySpan<char> textSpan, int currentIndex, out bool isSentenceFinished)
    {
        int searchIndex = currentIndex;

        while (searchIndex < textSpan.Length)
        {
            int offset = textSpan[searchIndex..].IndexOfAny(s_sentenceTerminators);
            if (offset < 0)
            {
                isSentenceFinished = false;
                return textSpan.Length;
            }

            int candidateIndex = searchIndex + offset;

            if (!IsRealSentenceBoundary(textSpan, candidateIndex))
            {
                searchIndex = candidateIndex + 1;
                continue;
            }

            isSentenceFinished = true;
            return ConsumeBoundarySuffix(textSpan, candidateIndex);
        }

        isSentenceFinished = false;
        return textSpan.Length;
    }

    /// <summary>
    /// Validates one candidate terminator without assuming a particular language.
    /// </summary>
    private static bool IsRealSentenceBoundary(ReadOnlySpan<char> text, int index)
    {
        char terminator = text[index];

        // Explicit text/paragraph separators are always semantic boundaries.
        if (IsLineBoundary(terminator))
        {
            return true;
        }

        if (s_periodLikeMarks.Contains(terminator))
        {
            return IsRealPeriodBoundary(text, index);
        }

        if (s_ellipsisMarks.Contains(terminator))
        {
            return IsRealEllipsisBoundary(text, index, index + 1);
        }

        // A quoted terminal can still belong to the same grammatical sentence when a
        // lowercase reporting clause follows: "Really?!" she asked.
        if (IsQuotedReportingContinuation(text, index))
        {
            return false;
        }

        // Ambiguous Western terminal marks are accepted only after the complete attached
        // terminal/closing cluster reaches a real boundary. This protects code-like constructs
        // such as foo?.Bar(), while still accepting What?! Next and "Stop!" Then continue.
        // Script-specific hard terminators intentionally do not require whitespace because many
        // writing systems do not separate sentences with spaces.
        if (RequiresBoundarySeparation(terminator) && !HasBoundarySeparationAfterTerminalCluster(text, index))
        {
            return false;
        }

        // ASCII ? and ! may legally occur inside URLs / URI queries / technical tokens.
        // This remains as a structural safeguard even though the boundary-separation rule above
        // already rejects the common no-whitespace forms.
        if (terminator is '?' or '!' && IsEmbeddedTechnicalTerminator(text, index))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Handles '.', U+2024, U+FE52 and U+FF0E using one shared contextual rule set.
    /// </summary>
    private static bool IsRealPeriodBoundary(ReadOnlySpan<char> text, int index)
    {
        // Consecutive period-like marks are an ASCII/compatibility ellipsis rather than an
        // abbreviation separator. Treat the complete run using ellipsis context rules.
        int runEnd = index + 1;
        while (runEnd < text.Length && s_periodLikeMarks.Contains(text[runEnd]))
        {
            runEnd++;
        }

        if (runEnd - index >= 2)
        {
            return IsRealEllipsisBoundary(text, index, runEnd);
        }

        // A period directly embedded between token characters is never a sentence boundary:
        // S.T.A.L.K.E.R, 3.14, v1.2.3, example.com, user.name@example.com, etc.
        if (index + 1 < text.Length && IsWordLikeAfterPeriod(text[index + 1]))
        {
            return false;
        }

        int afterClosing = SkipClosingPunctuation(text, index + 1);

        // Period-like marks follow the same boundary-separation principle as the ambiguous
        // Western ?/! family. If text continues immediately after the complete period/closing
        // suffix, keep it in the same semantic sentence. A following terminal mark is allowed
        // to extend the terminal cluster and is validated below as one unit.
        if (afterClosing < text.Length &&
            !char.IsWhiteSpace(text[afterClosing]) &&
            !IsBoundaryFormatControl(text[afterClosing]) &&
            !s_sentenceTerminators.Contains(text[afterClosing]))
        {
            return false;
        }

        int nextVisible = SkipBoundarySpacing(text, afterClosing, out bool crossedLineBoundary);

        if (crossedLineBoundary)
        {
            return true;
        }

        // End-of-input makes the period a real sentence ending even when the final token is
        // itself an abbreviation. There is no following sentence fragment to protect.
        if (nextVisible >= text.Length)
        {
            return true;
        }

        char next = text[nextVisible];

        // A following terminal cluster (?, !, another hard full stop, etc.) belongs to the
        // same sentence. Validate the boundary only after the complete attached cluster rather
        // than committing on its first character.
        if (s_sentenceTerminators.Contains(next) && !s_periodLikeMarks.Contains(next))
        {
            return HasBoundarySeparationAfterTerminalCluster(text, index);
        }

        ReadOnlySpan<char> token = GetTokenBefore(text, index);
        ReadOnlySpan<char> cleanToken = TrimLeadingTokenPunctuation(token);

        if (cleanToken.IsEmpty)
        {
            return true;
        }

        // Abbreviations can be followed by clause punctuation before the actual continuation:
        // "e.g., this", "etc.; however", and similar multilingual constructions. Look through
        // that punctuation only for abbreviation/context classification; it is not swallowed here.
        int continuationIndex = nextVisible;
        if (s_pauseMarks.Contains(next))
        {
            while (continuationIndex < text.Length && s_pauseMarks.Contains(text[continuationIndex]))
            {
                continuationIndex++;
            }

            continuationIndex = SkipClosingPunctuation(text, continuationIndex);
            continuationIndex = SkipBoundarySpacing(text, continuationIndex, out bool pauseCrossedLineBoundary);

            if (pauseCrossedLineBoundary || continuationIndex >= text.Length)
            {
                return true;
            }

            next = text[continuationIndex];
        }

        bool nextIsLower = char.IsLower(next);
        bool nextIsUpper = char.IsUpper(next);
        bool nextIsDigit = char.IsDigit(next);
        bool nextIsLetterOrDigit = char.IsLetterOrDigit(next);

        // A single-letter token before a period is very commonly an initial (A. Smith).
        // This remains intentionally conservative because "John A. Smith" and
        // "Plan A. Tomorrow..." are structurally indistinguishable without lexical semantics.
        if (cleanToken.Length == 1 &&
            char.IsLetter(cleanToken[0]) &&
            nextIsLetterOrDigit)
        {
            return false;
        }

        bool knownAbbreviation = CommonAbbreviations
            .GetAlternateLookup<ReadOnlySpan<char>>()
            .Contains(cleanToken);

        if (knownAbbreviation && nextIsLetterOrDigit)
        {
            // Lowercase and numeric continuations strongly favor an in-sentence abbreviation:
            // "etc. before", "approx. 25.4", "no. 12".
            if (nextIsLower || nextIsDigit)
            {
                return false;
            }

            // Honorifics/ranks normally bind to a following proper name even though it starts
            // uppercase: "Dr. Smith", "Capt. Jones", "Проф. Іваненко". For cased scripts,
            // require the source abbreviation itself to be capitalized so lowercase "ms."
            // (milliseconds) can still end a sentence before "Capt.".
            if (nextIsUpper && IsCapitalizedPrefixAbbreviation(cleanToken))
            {
                return false;
            }

            // A general abbreviation followed by an uppercase token may legitimately end a
            // sentence: "etc. Next...", "ms. Capt...".
            return true;
        }

        bool dottedAbbreviation = LooksLikeDottedAbbreviation(cleanToken);
        if (dottedAbbreviation && nextIsLetterOrDigit)
        {
            if (nextIsLower || nextIsDigit)
            {
                return false;
            }

            // Uppercase dotted initialisms such as U.S. Army or S.T.A.L.K.E.R. remain intact.
            // Lowercase dotted forms such as p.m. followed by an uppercase token are allowed
            // to terminate the sentence.
            if (nextIsUpper && ContainsUppercaseLetter(cleanToken))
            {
                return false;
            }

            return true;
        }

        // Unicode-style abbreviation protection: a period followed by a lowercase continuation
        // is usually not a sentence break. Unlike the previous implementation, this rule is
        // reached only after internal-token/abbreviation checks and no longer drives all logic.
        if (nextIsLower)
        {
            return false;
        }

        return true;
    }

    private static bool IsCapitalizedPrefixAbbreviation(ReadOnlySpan<char> token)
    {
        if (!PrefixAbbreviations
            .GetAlternateLookup<ReadOnlySpan<char>>()
            .Contains(token))
        {
            return false;
        }

        bool hasCasedLetter = false;

        for (int i = 0; i < token.Length; i++)
        {
            char value = token[i];
            if (!char.IsLetter(value))
            {
                continue;
            }

            if (char.IsUpper(value))
            {
                return true;
            }

            if (char.IsLower(value))
            {
                hasCasedLetter = true;
                break;
            }
        }

        // Scripts without upper/lower case cannot satisfy a capitalization test; the curated
        // prefix list itself is therefore the strongest available signal for them.
        return !hasCasedLetter;
    }

    private static bool ContainsUppercaseLetter(ReadOnlySpan<char> token)
    {
        for (int i = 0; i < token.Length; i++)
        {
            if (char.IsUpper(token[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detects constructions such as "Really?!" she asked. The closing quote/bracket is required;
    /// punctuation followed directly by lowercase text remains a normal sentence boundary so that
    /// malformed casing does not silently merge unrelated sentences.
    /// </summary>
    private static bool IsQuotedReportingContinuation(ReadOnlySpan<char> text, int boundaryIndex)
    {
        int index = boundaryIndex + 1;
        bool sawClosing = false;

        // A terminal cluster may come before the closing quote: ?!", !!!"), etc.
        while (index < text.Length)
        {
            bool advanced = false;

            while (index < text.Length &&
                   s_sentenceTerminators.Contains(text[index]) &&
                   !IsLineBoundary(text[index]))
            {
                index++;
                advanced = true;
            }

            while (index < text.Length && s_closingPunctuation.Contains(text[index]))
            {
                sawClosing = true;
                index++;
                advanced = true;
            }

            if (!advanced)
            {
                break;
            }
        }

        if (!sawClosing)
        {
            return false;
        }

        int nextVisible = SkipBoundarySpacing(text, index, out bool crossedLineBoundary);
        if (crossedLineBoundary || nextVisible >= text.Length)
        {
            return false;
        }

        return char.IsLower(text[nextVisible]);
    }

    /// <summary>
    /// Ellipsis can express hesitation inside a sentence. It is terminal at end-of-input,
    /// across an explicit line boundary, before another terminal suffix, or before a clearly
    /// new uppercase sentence; otherwise it remains a continuation.
    /// </summary>
    private static bool IsRealEllipsisBoundary(ReadOnlySpan<char> text, int start, int runEnd)
    {
        // Merge adjacent ellipsis symbols / period-like dots into one semantic run.
        while (runEnd < text.Length &&
               (s_ellipsisMarks.Contains(text[runEnd]) || s_periodLikeMarks.Contains(text[runEnd])))
        {
            runEnd++;
        }

        int afterClosing = SkipClosingPunctuation(text, runEnd);
        int nextVisible = SkipBoundarySpacing(text, afterClosing, out bool crossedLineBoundary);

        if (crossedLineBoundary || nextVisible >= text.Length)
        {
            return true;
        }

        char next = text[nextVisible];

        // "Wait…!" / "Really…?" are terminal clusters. Validate the end of the whole
        // attached cluster so constructs without a real boundary do not split on its first mark.
        if (s_sentenceTerminators.Contains(next) &&
            !s_ellipsisMarks.Contains(next) &&
            !s_periodLikeMarks.Contains(next))
        {
            return HasBoundarySeparationAfterTerminalCluster(text, start);
        }

        // Without whitespace, ellipsis almost always connects the same thought, especially
        // in scripts without case distinctions (Japanese, Chinese, Thai, etc.).
        bool hadSpacing = nextVisible > afterClosing;
        if (!hadSpacing)
        {
            return false;
        }

        // With spacing, an uppercase letter is a useful language-independent signal of a
        // fresh sentence in bicameral scripts. Lowercase and uncased scripts stay continuous.
        return char.IsUpper(next);
    }

    /// <summary>
    /// Finds the first valid one-time EarlySplit boundary inside the first semantic sentence.
    /// ASCII clause marks (, ; :) require a real boundary after the complete attached punctuation
    /// cluster. Non-ASCII clause marks keep script-native behavior because many writing systems do
    /// not use spaces between clauses.
    /// </summary>
    private static int FindEarlySplitEnd(
        ReadOnlySpan<char> text,
        int start,
        int searchEnd,
        int sentenceEnd)
    {
        int searchIndex = start;

        while (searchIndex < searchEnd)
        {
            int offset = text[searchIndex..searchEnd].IndexOfAny(s_earlySplitPunctuation);
            if (offset < 0)
            {
                return -1;
            }

            int candidateIndex = searchIndex + offset;
            int clusterEnd = ConsumeAttachedPunctuationCluster(text, candidateIndex, sentenceEnd);

            if (IsValidEarlySplitBoundary(text, candidateIndex, clusterEnd, sentenceEnd))
            {
                return clusterEnd;
            }

            searchIndex = candidateIndex + 1;
        }

        return -1;
    }

    private static bool IsValidEarlySplitBoundary(
        ReadOnlySpan<char> text,
        int candidateIndex,
        int clusterEnd,
        int sentenceEnd)
    {
        char candidate = text[candidateIndex];

        // Non-ASCII clause punctuation is allowed to follow script-native spacing rules.
        // Examples: Chinese/Japanese fullwidth punctuation, Arabic, Myanmar, Khmer, etc.
        if (candidate > 0x7F)
        {
            return true;
        }

        // For ASCII comma/semicolon/colon, absence of a separator after the complete attached
        // punctuation cluster is strong evidence that the mark is inside a token or construct:
        // https://host, foo:bar, x,y, and similar technical text.
        if (clusterEnd >= sentenceEnd || clusterEnd >= text.Length)
        {
            return true;
        }

        char next = text[clusterEnd];
        return char.IsWhiteSpace(next) || IsBoundaryFormatControl(next);
    }

    /// <summary>
    /// Consumes directly attached punctuation as one cluster. This is intentionally broader than
    /// sentence terminators because EarlySplit candidates may sit inside constructs such as ://.
    /// Whitespace always ends the cluster.
    /// </summary>
    private static int ConsumeAttachedPunctuationCluster(ReadOnlySpan<char> text, int index, int limit)
    {
        int end = index;
        int max = Math.Min(limit, text.Length);

        while (end < max && IsPunctuationClusterChar(text[end]))
        {
            end++;
        }

        return end;
    }

    private static bool IsPunctuationClusterChar(char value)
    {
        UnicodeCategory category = char.GetUnicodeCategory(value);
        return category is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    /// <summary>
    /// Western question/exclamation marks are ambiguous when immediately glued to following text.
    /// Script-specific hard terminators remain spacing-independent.
    /// </summary>
    private static bool RequiresBoundarySeparation(char value)
    {
        return value is '!' or '?' or '‼' or '‽' or '⁇' or '⁈' or '⁉';
    }

    private static bool HasBoundarySeparationAfterTerminalCluster(ReadOnlySpan<char> text, int boundaryIndex)
    {
        int clusterEnd = ConsumeTerminalCluster(text, boundaryIndex, out bool crossedLineBoundary);

        if (crossedLineBoundary || clusterEnd >= text.Length)
        {
            return true;
        }

        char next = text[clusterEnd];
        return char.IsWhiteSpace(next) || IsBoundaryFormatControl(next);
    }

    /// <summary>
    /// Consumes a complete sentence-terminal/closing cluster, e.g. ?!, ...?!" or !").
    /// </summary>
    private static int ConsumeTerminalCluster(
        ReadOnlySpan<char> text,
        int boundaryIndex,
        out bool crossedLineBoundary)
    {
        int index = boundaryIndex;
        crossedLineBoundary = false;

        while (index < text.Length)
        {
            int before = index;

            while (index < text.Length && s_sentenceTerminators.Contains(text[index]))
            {
                if (IsLineBoundary(text[index]))
                {
                    crossedLineBoundary = true;
                }

                index++;
            }

            while (index < text.Length && s_closingPunctuation.Contains(text[index]))
            {
                index++;
            }

            if (index == before)
            {
                break;
            }
        }

        return index;
    }

    /// <summary>
    /// Protects sentence-like punctuation occurring inside a technical token. This intentionally
    /// recognizes structure rather than a language: URI schemes, www/domain paths, query strings,
    /// relative URLs, and email-like tokens.
    /// </summary>
    private static bool IsEmbeddedTechnicalTerminator(ReadOnlySpan<char> text, int index)
    {
        if (index <= 0 || index + 1 >= text.Length)
        {
            return false;
        }

        char after = text[index + 1];
        if (char.IsWhiteSpace(after) || s_closingPunctuation.Contains(after))
        {
            return false;
        }

        int tokenStart = index - 1;
        while (tokenStart >= 0 && !char.IsWhiteSpace(text[tokenStart]))
        {
            tokenStart--;
        }
        tokenStart++;

        ReadOnlySpan<char> prefix = text[tokenStart..index];
        if (prefix.IsEmpty)
        {
            return false;
        }

        if (prefix.IndexOf("://".AsSpan(), StringComparison.Ordinal) >= 0 ||
            prefix.StartsWith("www.".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            prefix.IndexOf('@') >= 0)
        {
            return true;
        }

        // Relative paths/query-like tokens such as /search?q=x or api/v1?x=1.
        bool hasPathMarker = prefix.IndexOf('/') >= 0 || prefix.IndexOf('\\') >= 0;
        bool hasTechnicalMarker = prefix.IndexOfAny(".@=&#".AsSpan()) >= 0;

        return hasPathMarker || hasTechnicalMarker;
    }

    private static bool IsLineBoundary(char value)
    {
        return value is '\n' or '\r' or '\u0085' or '\u2028' or '\u2029';
    }

    private static bool IsWordLikeAfterPeriod(char value)
    {
        return char.IsLetterOrDigit(value) ||
               char.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark
                   or UnicodeCategory.SpacingCombiningMark
                   or UnicodeCategory.EnclosingMark;
    }

    private static int SkipClosingPunctuation(ReadOnlySpan<char> text, int index)
    {
        while (index < text.Length && s_closingPunctuation.Contains(text[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// Skips ordinary spacing/format controls while preserving whether an explicit line boundary
    /// was crossed. Line separators themselves are semantic sentence evidence, not mere whitespace.
    /// </summary>
    private static int SkipBoundarySpacing(ReadOnlySpan<char> text, int index, out bool crossedLineBoundary)
    {
        crossedLineBoundary = false;

        while (index < text.Length)
        {
            char value = text[index];

            if (IsLineBoundary(value))
            {
                crossedLineBoundary = true;
                index++;
                continue;
            }

            if (char.IsWhiteSpace(value) || IsBoundaryFormatControl(value))
            {
                index++;
                continue;
            }

            break;
        }

        return index;
    }

    private static bool IsBoundaryFormatControl(char value)
    {
        return value is '\u061C' or '\u200E' or '\u200F' or '\uFEFF'
            || (value >= '\u2066' && value <= '\u2069');
    }

    private static ReadOnlySpan<char> GetTokenBefore(ReadOnlySpan<char> text, int endExclusive)
    {
        int start = endExclusive - 1;
        while (start >= 0 && !char.IsWhiteSpace(text[start]))
        {
            start--;
        }

        return text[(start + 1)..endExclusive];
    }

    private static ReadOnlySpan<char> TrimLeadingTokenPunctuation(ReadOnlySpan<char> token)
    {
        int start = 0;
        while (start < token.Length &&
               char.IsPunctuation(token[start]) &&
               !s_periodLikeMarks.Contains(token[start]))
        {
            start++;
        }

        return token[start..];
    }

    private static bool LooksLikeDottedAbbreviation(ReadOnlySpan<char> token)
    {
        int separatorCount = 0;
        int segmentLength = 0;
        int maxSegmentLength = 0;
        int minSegmentLength = int.MaxValue;

        for (int i = 0; i < token.Length; i++)
        {
            if (s_periodLikeMarks.Contains(token[i]))
            {
                if (segmentLength == 0)
                {
                    return false;
                }

                separatorCount++;
                maxSegmentLength = Math.Max(maxSegmentLength, segmentLength);
                minSegmentLength = Math.Min(minSegmentLength, segmentLength);
                segmentLength = 0;
                continue;
            }

            // Dotted acronyms/abbreviations are alphabetic. Numeric/mixed forms such as
            // 127.0.0.1 and v1.2.3 are technical tokens, not abbreviation evidence.
            if (!char.IsLetter(token[i]))
            {
                return false;
            }

            segmentLength++;
        }

        if (separatorCount == 0 || segmentLength == 0)
        {
            return false;
        }

        maxSegmentLength = Math.Max(maxSegmentLength, segmentLength);
        minSegmentLength = Math.Min(minSegmentLength, segmentLength);

        // Strong generic forms: U.S / i.e / p.m (single-letter segments), or longer chains
        // such as S.T.A.L.K.E.R where multiple short alphabetic segments are unmistakably
        // acronym-like. Avoid treating short domains such as x.ai or co.uk as abbreviations.
        if (maxSegmentLength == 1)
        {
            return true;
        }

        return separatorCount >= 2 &&
               minSegmentLength > 0 &&
               maxSegmentLength <= 3;
    }

    /// <summary>
    /// Consumes a complete terminal suffix such as ?!", ...), or mixed full-width terminal clusters.
    /// Terminators and closing punctuation may alternate; none should leak into the next sentence.
    /// </summary>
    private static int ConsumeBoundarySuffix(ReadOnlySpan<char> text, int boundaryIndex)
    {
        return ConsumeTerminalCluster(text, boundaryIndex, out _);
    }

    /// <summary>
    /// Adds one normal sentence or routes an oversized sentence through emergency splitting.
    /// </summary>
    private void AddSentence(ReadOnlySpan<char> sentenceSpan, bool isSentenceFinished, List<TextChunk> result)
    {
        sentenceSpan = sentenceSpan.Trim();
        if (sentenceSpan.IsEmpty) return;

        if (sentenceSpan.Length <= _maxLength)
        {
            result.Add(new TextChunk(sentenceSpan.ToString(), isSentenceFinished));
            return;
        }

        SplitLongSentence(sentenceSpan, isSentenceFinished, result);
    }

    /// <summary>
    /// Splits an oversized sentence using the complete pause-mark set. Intermediate chunks
    /// are continuations; only the final chunk inherits the real sentence boundary.
    /// </summary>
    private void SplitLongSentence(ReadOnlySpan<char> sentenceSpan, bool isSentenceFinished, List<TextChunk> result)
    {
        int currentIndex = 0;

        while (currentIndex < sentenceSpan.Length)
        {
            int remainingLength = sentenceSpan.Length - currentIndex;
            if (remainingLength <= _maxLength)
            {
                ReadOnlySpan<char> finalSpan = sentenceSpan[currentIndex..].Trim();
                if (!finalSpan.IsEmpty)
                {
                    result.Add(new TextChunk(finalSpan.ToString(), isSentenceFinished));
                }
                break;
            }

            int windowEnd = currentIndex + _maxLength;
            int splitIndex = FindLastOccurrence(sentenceSpan, currentIndex, windowEnd, s_pauseMarks);

            if (splitIndex == -1)
            {
                for (int i = windowEnd - 1; i >= currentIndex; i--)
                {
                    if (char.IsWhiteSpace(sentenceSpan[i]))
                    {
                        splitIndex = i;
                        break;
                    }
                }
            }

            if (splitIndex == -1 || splitIndex < currentIndex)
            {
                splitIndex = FindSafeTextElementBoundary(sentenceSpan, currentIndex, windowEnd);
            }
            else
            {
                splitIndex++;
            }

            ReadOnlySpan<char> chunkSpan = sentenceSpan[currentIndex..splitIndex].Trim();

            if (!chunkSpan.IsEmpty)
            {
                char lastChar = chunkSpan[^1];

                // Preserve the existing emergency glue behavior for hard/whitespace splits.
                string finalChunk = char.IsPunctuation(lastChar)
                                    ? chunkSpan.ToString()
                                    : string.Concat(chunkSpan, EmergencyGlue);

                result.Add(new TextChunk(finalChunk, false));
            }

            currentIndex = splitIndex;
        }
    }

    /// <summary>
    /// Finds the furthest extended-grapheme boundary that does not exceed preferredEnd.
    /// Used only by the rare hard-split fallback, so normal sentence/word splitting keeps
    /// the existing SearchValues/Span fast path with no additional per-character overhead.
    /// </summary>
    private static int FindSafeTextElementBoundary(ReadOnlySpan<char> text, int startIndex, int preferredEnd)
    {
        int cursor = startIndex;
        int lastBoundary = startIndex;

        while (cursor < preferredEnd)
        {
            int elementLength = StringInfo.GetNextTextElementLength(text[cursor..]);
            if (elementLength <= 0 || cursor + elementLength > preferredEnd)
            {
                break;
            }

            cursor += elementLength;
            lastBoundary = cursor;
        }

        if (lastBoundary > startIndex)
        {
            return lastBoundary;
        }

        // A single grapheme can itself be longer than MaxChunkLength (for example a very large
        // emoji ZWJ/combining sequence). Preserving valid Unicode is more important than forcing
        // an impossible size cap, so allow that one element through intact to guarantee progress.
        int firstElementLength = StringInfo.GetNextTextElementLength(text[startIndex..]);
        return Math.Min(text.Length, startIndex + Math.Max(firstElementLength, 1));
    }

    // Finds the last requested boundary character inside the supplied range.
    private static int FindLastOccurrence(ReadOnlySpan<char> text, int startIndex, int endIndex, System.Buffers.SearchValues<char> charsToFind)
    {
        ReadOnlySpan<char> window = text[startIndex..endIndex];
        int relativeIndex = window.LastIndexOfAny(charsToFind);

        return relativeIndex == -1 ? -1 : startIndex + relativeIndex;
    }
}