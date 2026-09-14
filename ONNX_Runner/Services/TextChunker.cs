using System.Globalization;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// High-performance text processing module responsible for splitting input text into manageable chunks.
/// It uses "Smart Splitting" logic to identify sentence boundaries while protecting abbreviations, 
/// titles, and initials from being accidentally sliced.
/// </summary>
public class TextChunker(ChunkerSettings settings)
{
    // =========================================================================================
    // SINGLE SOURCE OF TRUTH: Global multilingual array of sentence terminators.
    // Made PUBLIC so the synthesis pipeline can use it for smart context detection without duplicating data.
    // =========================================================================================
    public static readonly char[] SentenceTerminators =
    [
        // Common punctuation and explicit line/paragraph boundaries
        '.', '!', '?',
        '\n', '\r', '\u0085',  // LF, CR, NEXT LINE
        '\u2028', '\u2029',    // LINE SEPARATOR, PARAGRAPH SEPARATOR

        // Ellipsis variants — semantically equivalent to ASCII "..." for sentence chunking
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

    // Limits the length of a single audio generation task to prevent GPU timeouts.
    private readonly int _maxLength = settings.MaxChunkLength > 50 ? settings.MaxChunkLength : 250;

    // Symbol used to glue chunks together when an emergency split is necessary
    // (e.g., splitting in the middle of a long sentence without good break points).
    // A hyphen signals a soft, continuous break to the TTS engine rather than a hard pause.
    private const string EmergencyGlue = "-";

    // Additional pause marks that often indicate natural break points in sentences, even if they aren't full terminators.
    // These are NOT sentence terminators — they signal a softer pause used by SplitLongSentence for emergency splitting.
    private static readonly char[] PauseMarks =
    [
        // Common soft boundaries
        ',', // U+002C  COMMA
        ';', // U+003B  SEMICOLON
        ':', // U+003A  COLON
        '-', // U+002D  HYPHEN-MINUS
        '–', // U+2013  EN DASH
        '—', // U+2014  EM DASH

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

        // Tibetan shad-family marks are structural/phrase boundaries rather than a reliable
        // one-to-one equivalent of Western sentence endings, so use them only as soft split points.
        '།', // U+0F0D  TIBETAN MARK SHAD
        '༎', // U+0F0E  TIBETAN MARK NYIS SHAD
        '༏', // U+0F0F  TIBETAN MARK TSHEG SHAD
        '༐', // U+0F10  TIBETAN MARK NYIS TSHEG SHAD
        '༑', // U+0F11  TIBETAN MARK RIN CHEN SPUNGS SHAD
        '༒', // U+0F12  TIBETAN MARK RGYA GRAM SHAD
        '༔', // U+0F14  TIBETAN MARK GTER TSHEG

        // Ethiopic soft punctuation
        '፣', // U+1363  ETHIOPIC COMMA
        '፤', // U+1364  ETHIOPIC SEMICOLON
        '፥', // U+1365  ETHIOPIC COLON
        '፦', // U+1366  ETHIOPIC PREFACE COLON

        // Myanmar clause separator
        '၊', // U+104A  MYANMAR SIGN LITTLE SECTION

        // Khmer
        '៖', // U+17D6  KHMER SIGN CAMNUC PII KUUH

        // Tai Tham structural punctuation
        '᪨', // U+1AA8  TAI THAM SIGN KAAN
        '᪩', // U+1AA9  TAI THAM SIGN KAANKUU
        '᪪', // U+1AAA  TAI THAM SIGN SATKAAN
        '᪫', // U+1AAB  TAI THAM SIGN SATKAANKUU

        // Balinese / Javanese clause separators
        '᭞', // U+1B5E  BALINESE CARIK SIKI
        '꧈', // U+A9C8  JAVANESE PADA LINGSA

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

        // Runic structural punctuation
        '᛫', // U+16EB  RUNIC SINGLE PUNCTUATION
        '᛬', // U+16EC  RUNIC MULTIPLE PUNCTUATION
        '᛭', // U+16ED  RUNIC CROSS PUNCTUATION

        // Mongolian
        '᠂', // U+1802  MONGOLIAN COMMA
        '᠄', // U+1804  MONGOLIAN COLON
        '᠈', // U+1808  MONGOLIAN MANCHU COMMA
    ];

    // Cached because the expanded multilingual set is searched repeatedly on the hot path.
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
    /// A comprehensive list of global abbreviations and titles that should NOT trigger a sentence split.
    /// Includes titles from English, Spanish, French, German, and Slavic languages.
    /// </summary>
    public static readonly HashSet<string> CommonTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        // ================= ENGLISH =================
        "mr", "mrs", "ms", "mx", "messrs", "mmes", "msgr", "esq", "hon", "rev", "fr", "prof", "dr", "sr", "jr",
        "rep", "sen", "gov", "pres", "amb", "sec", "min", "cmdr", "cllr", "ald", "mag", "jud",
        "gen", "col", "maj", "capt", "lieut", "lt", "sgt", "cpl", "pvt", "adm", "brig", "cmdr", "comm",
        "ceo", "cfo", "cto", "vp", "dir", "mgr", "asst", "assoc",
        "mt", "ft", "st", "ave", "blvd", "rd", "hwy", "bldg", "ste", "apt", "vs", "etc",
        
        // ================= SPANISH / PORTUGUESE =================
        "srta", "sra", "don", "doña", "dra", "profa", "ldo", "lda", "arq", "gral", "cap", "sto", "sta", "av", "pza", "prof",
        
        // ================= FRENCH =================
        "mme", "mlle", "mgr", "pr", "me", "vve", "ste", "st", "bd", "av",
        
        // ================= ITALIAN =================
        "sig", "sigra", "dott", "dottssa", "avv", "arch", "geom", "rag", "prof", "profssa", "mons", "ten", "cap", "gen",
        
        // ================= GERMAN / DUTCH =================
        "herr", "frau", "ing", "frl", "mag", "dipl", "med", "dhr", "mevr", "mej", "ir", "drs", "ds", "prof", "univ", "bakk",
        
        // ================= NORDIC =================
        "hr", "fr", "fru", "frk", "kapt", "prof", "dr",
        
        // ================= POLISH / CZECH / SLOVAK =================
        "doc", "inż", "mec", "dyr", "św", "bł", "bc", "mgr", "mudr", "mvdr", "judr", "phdr", "rndr", "inž", "prof", "pan", "pani",
        
        // ================= UKRAINIAN / KYRILLIC =================
        "проф", "доц", "акад", "гр", "тов", "пан", "пані", "дир", "інж", "зав", "заст", "пом", "д-р", "ст", "мол",
        "вул", "пров", "просп", "бул", "обл", "пл", "ім", "буд", "кв", "мкр", "р-н", "пт", "сел", "смт", "рис", "табл", "див", "пор", "напр",

        // ================= RUSSIAN =================
        "г", "гр", "д-р", "доц", "акад", "проф", "тов", "ул", "пр", "пер", "бул", "пл", "наб", "ш", "пос", "дер",
        "обл", "р-н", "кв", "стр", "корп", "пом", "рис", "табл", "см", "ср", "напр", "т", "д", "п", "тп", "св",

        // ================= TURKISH =================
        "dr", "prof", "doç", "yrd", "uzm", "öğr", "mh", "sk", "cd", "bul", "sok",

        // ================= JAPANESE (Romaji titles used in multilingual contexts) =================
        "dr", "prof",

        // ================= ARABIC (Latin transliterations commonly used in multilingual AI output) =================
        "dr", "prof", "st",

        // ================= HEBREW =================
        "דר", "פרופ", "עו"
    };

    /// <summary>
    /// Chunks the text into sentences while respecting linguistic rules.
    /// </summary>
    public List<string> Split(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        // ZERO-ALLOCATION: ReadOnlySpan allows us to slice the text without creating thousands of small string objects.
        ReadOnlySpan<char> textSpan = text.AsSpan();
        int currentIndex = 0;

        while (currentIndex < textSpan.Length)
        {
            int nextTerminator = currentIndex;
            bool foundValidTerminator = false;

            while (nextTerminator < textSpan.Length)
            {
                // Find the next potential terminator using hardware-accelerated SearchValues
                int offset = textSpan[nextTerminator..].IndexOfAny(s_sentenceTerminators);
                if (offset == -1)
                {
                    nextTerminator = -1;
                    break;
                }

                nextTerminator += offset;

                if (nextTerminator + 1 >= textSpan.Length)
                {
                    foundValidTerminator = true;
                    break;
                }

                char currentTerminator = textSpan[nextTerminator];

                // ====================================================================
                // ASIAN & GLOBAL TERMINATOR LOGIC
                // Symbols like '。' or '؟' are 100% sentence endings. 
                // They don't have abbreviations or "middle names" associated with them.
                // ====================================================================
                if (currentTerminator != '.')
                {
                    foundValidTerminator = true;
                    break;
                }

                // If the terminator is a standard period ('.'), apply smart abbreviation logic.
                char nextChar = textSpan[nextTerminator + 1];

                // ====================================================================
                // CLOSING PUNCTUATION SKIP
                // Skip past any closing quotes or brackets (e.g., ".)" or ".”") to find the true 
                // next character, ensuring periods inside quotes aren't treated as abbreviations.
                // ====================================================================
                int afterClosingIdx = nextTerminator + 1;
                while (afterClosingIdx < textSpan.Length && s_closingPunctuation.Contains(textSpan[afterClosingIdx]))
                {
                    afterClosingIdx++;
                }
                char charAfterClosing = afterClosingIdx < textSpan.Length ? textSpan[afterClosingIdx] : ' ';

                // Check if the character after the closing punctuation is a valid sentence boundary.
                bool boundaryAfterClosing = afterClosingIdx >= textSpan.Length
                    || char.IsWhiteSpace(charAfterClosing)
                    || s_sentenceTerminators.Contains(charAfterClosing)
                    || s_pauseMarks.Contains(charAfterClosing);

                if (char.IsWhiteSpace(nextChar) || s_sentenceTerminators.Contains(nextChar) || s_pauseMarks.Contains(nextChar)
                    || (afterClosingIdx > nextTerminator + 1 && boundaryAfterClosing))
                {
                    int nextVisibleCharIdx = afterClosingIdx;
                    while (nextVisibleCharIdx < textSpan.Length && char.IsWhiteSpace(textSpan[nextVisibleCharIdx]))
                    {
                        nextVisibleCharIdx++;
                    }

                    // Logic: If the next word starts with a lowercase letter, the period is likely an abbreviation.
                    bool isNextLower = nextVisibleCharIdx < textSpan.Length && char.IsLower(textSpan[nextVisibleCharIdx]);

                    int wordStart = nextTerminator - 1;
                    while (wordStart >= 0 && !char.IsWhiteSpace(textSpan[wordStart]))
                    {
                        wordStart--;
                    }
                    wordStart++;

                    ReadOnlySpan<char> wordBeforeDot = textSpan[wordStart..nextTerminator];

                    // Clean the word from leading punctuation (e.g., opening brackets or quotes like "(Mr" or "«Dr")
                    ReadOnlySpan<char> cleanWord = wordBeforeDot;
                    while (cleanWord.Length > 0 && char.IsPunctuation(cleanWord[0]))
                    {
                        cleanWord = cleanWord[1..];
                    }

                    bool isAbbreviation = false;

                    // ABBREVIATION DETECTION RULES:
                    // 1. Single character initials (e.g., "A. Smith").
                    if (cleanWord.Length == 1 && char.IsLetter(cleanWord[0]))
                    {
                        isAbbreviation = true;
                    }
                    // 2. Next word is lowercase (e.g., "He lived on St. john street").
                    else if (isNextLower)
                    {
                        isAbbreviation = true;
                    }
                    // 3. Mixed segments check (Distinguishes "U.S.A." from a URL like "site.com").
                    else if (cleanWord.IndexOf('.') != -1)
                    {
                        int maxSegmentLength = 0;
                        int currentSegmentLength = 0;

                        for (int i = 0; i < cleanWord.Length; i++)
                        {
                            if (cleanWord[i] == '.')
                            {
                                if (currentSegmentLength > maxSegmentLength) maxSegmentLength = currentSegmentLength;
                                currentSegmentLength = 0;
                            }
                            else
                            {
                                currentSegmentLength++;
                            }
                        }
                        if (currentSegmentLength > maxSegmentLength) maxSegmentLength = currentSegmentLength;

                        // Abbreviations typically have short segments (e.g., "i.e.").
                        if (maxSegmentLength <= 3) isAbbreviation = true;
                    }
                    // 4. Global Titles Dictionary check. (Zero-allocation using .NET 8+ alternate lookup)
                    else
                    {
                        if (CommonTitles.GetAlternateLookup<ReadOnlySpan<char>>().Contains(cleanWord))
                        {
                            isAbbreviation = true;
                        }
                    }

                    if (!isAbbreviation)
                    {
                        foundValidTerminator = true;
                        break;
                    }
                }

                nextTerminator++; // Period found was an abbreviation, continue searching.
            }

            int endIndex;
            if (!foundValidTerminator || nextTerminator == -1)
            {
                endIndex = textSpan.Length;
            }
            else
            {
                endIndex = nextTerminator + 1;

                // Capture any closing quotes/brackets immediately after the terminator.
                while (endIndex < textSpan.Length && s_closingPunctuation.Contains(textSpan[endIndex])) endIndex++;

                // Capture trailing terminators (e.g., "Wait!!!" -> captures all three exclamation marks).
                while (endIndex < textSpan.Length && s_sentenceTerminators.Contains(textSpan[endIndex])) endIndex++;
            }

            string sentence = textSpan[currentIndex..endIndex].Trim().ToString();

            if (!string.IsNullOrWhiteSpace(sentence))
            {
                // If a sentence is unusually long, we perform an emergency split to keep the engine stable.
                if (sentence.Length <= _maxLength) result.Add(sentence);
                else result.AddRange(SplitLongSentence(sentence));
            }

            currentIndex = endIndex;
        }

        return result;
    }

    /// <summary>
    /// Breaks down extremely long sentences into smaller chunks at logical pause points (commas, colons, etc.).
    /// </summary>
    private List<string> SplitLongSentence(string sentence)
    {
        var result = new List<string>();
        int currentIndex = 0;
        ReadOnlySpan<char> sentenceSpan = sentence.AsSpan();

        while (currentIndex < sentenceSpan.Length)
        {
            int remainingLength = sentenceSpan.Length - currentIndex;
            if (remainingLength <= _maxLength)
            {
                result.Add(sentenceSpan[currentIndex..].Trim().ToString());
                break;
            }

            int windowEnd = currentIndex + _maxLength;

            // Search for the last pause mark within the current chunk window.
            int splitIndex = FindLastOccurrence(sentenceSpan, currentIndex, windowEnd, s_pauseMarks);

            if (splitIndex == -1)
            {
                // FALLBACK: Search for the last space character if no punctuation pause marks are found.
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
                // Final fallback for unbroken text (long URLs, hashes, CJK/emoji runs without
                // recognized punctuation, etc.). Never split inside an extended grapheme cluster:
                // this preserves surrogate pairs, combining sequences, variation selectors, and
                // emoji ZWJ sequences even if one text element has to slightly exceed MaxChunkLength.
                splitIndex = FindSafeTextElementBoundary(sentenceSpan, currentIndex, windowEnd);
            }
            else
            {
                splitIndex++; // Include the found punctuation/space in the current chunk.
            }

            ReadOnlySpan<char> chunkSpan = sentenceSpan[currentIndex..splitIndex].Trim();

            if (!chunkSpan.IsEmpty)
            {
                char lastChar = chunkSpan[^1];
                string finalChunk = chunkSpan.ToString();

                // If we split in a way that left the chunk without a proper ending, add an emergency 
                // marker to signal the TTS engine to handle it gracefully.
                if (!char.IsPunctuation(lastChar))
                {
                    finalChunk += EmergencyGlue;
                }

                result.Add(finalChunk);
            }

            currentIndex = splitIndex;
        }

        return result;
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