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
    // Made PUBLIC so SpeechEndpoint can use it for smart context detection without duplicating data.
    // =========================================================================================
    public static readonly char[] SentenceTerminators =
    [
        // Common Latin punctuation
        '.', '!', '?', '\n',

        // Compound Latin punctuation — frequently used in chat and AI-generated text
        '‼',  // U+203C  DOUBLE EXCLAMATION MARK
        '‽',  // U+203D  INTERROBANG (combination of ? and !)
        '⁇',  // U+2047  DOUBLE QUESTION MARK
        '⁈',  // U+2048  QUESTION EXCLAMATION MARK
        '⁉',  // U+2049  EXCLAMATION QUESTION MARK

        // East Asian — fullwidth and halfwidth variants (Chinese, Japanese, Korean)
        '。',  // U+3002  IDEOGRAPHIC FULL STOP
        '！',  // U+FF01  FULLWIDTH EXCLAMATION MARK
        '？',  // U+FF1F  FULLWIDTH QUESTION MARK
        '｡',   // U+FF61  HALFWIDTH IDEOGRAPHIC FULL STOP
        '．',  // U+FF0E  FULLWIDTH FULL STOP (common in Japanese formal text)
        '﹒',  // U+FE52  SMALL FULL STOP
        '﹗',  // U+FE57  SMALL EXCLAMATION MARK

        // Arabic and Persian
        '؟',  // U+061F  ARABIC QUESTION MARK
        '۔',  // U+06D4  ARABIC FULL STOP (Urdu)

        // Syriac — ancient Aramaic script, still used in liturgical texts
        '܀',  // U+0700  SYRIAC END OF PARAGRAPH
        '܁',  // U+0701  SYRIAC SUPRALINEAR FULL STOP
        '܂',  // U+0702  SYRIAC SUBLINEAR FULL STOP

        // Devanagari and other Indic scripts (Hindi, Sanskrit, Marathi, Nepali)
        '।',  // U+0964  DEVANAGARI DANDA (single)
        '॥',  // U+0965  DEVANAGARI DOUBLE DANDA

        // Thai
        '๚',  // U+0E5A  THAI CHARACTER ANGKHANKHU
        '๛',  // U+0E5B  THAI CHARACTER KHOMUT

        // Armenian
        '։',  // U+0589  ARMENIAN FULL STOP
        '՞',  // U+055E  ARMENIAN QUESTION MARK

        // Greek — visually identical to semicolon but a different Unicode codepoint
        ';',  // U+037E  GREEK QUESTION MARK
        ';',  // U+003B  LATIN SEMICOLON (kept as a sentence boundary for streaming heuristics)

        // Ethiopic (Amharic, Tigrinya)
        '።',  // U+1362  ETHIOPIC FULL STOP
        '፧',   // U+1367  ETHIOPIC QUESTION MARK
        '፨',  // U+1368  ETHIOPIC PARAGRAPH SEPARATOR

        // Myanmar (Burmese)
        '၊',  // U+104A  MYANMAR SIGN LITTLE SECTION (clause boundary, often sentence-level)
        '။',  // U+104B  MYANMAR SIGN SECTION (full sentence terminator)

        // Mongolian
        '᠃',  // U+1803  MONGOLIAN FULL STOP
        '᠉',  // U+1809  MONGOLIAN MANCHU FULL STOP

        // Canadian Syllabics — used for Indigenous languages of Canada (Cree, Inuktitut, etc.)
        '᙮',  // U+166E  CANADIAN SYLLABICS FULL STOP
    ];

    // High-performance search values dynamically created from the array above to prevent duplication.
    private static readonly System.Buffers.SearchValues<char> s_sentenceTerminators = System.Buffers.SearchValues.Create(SentenceTerminators);

    // Limits the length of a single audio generation task to prevent GPU timeouts.
    private readonly int _maxLength = settings.MaxChunkLength > 50 ? settings.MaxChunkLength : 250;

    // Symbol used to glue chunks together when an emergency split is necessary
    // (e.g., splitting in the middle of a long sentence without good break points).
    private const string EmergencyGlue = ",";

    // Additional pause marks that often indicate natural break points in sentences, even if they aren't full terminators.
    // These are NOT sentence terminators — they signal a softer pause used by SplitLongSentence for emergency splitting.
    private static readonly char[] PauseMarks =
    [
        ',', // U+002C  COMMA
        ';', // U+003B  SEMICOLON
        ':', // U+003A  COLON
        '-', // U+002D  HYPHEN-MINUS
        '–', // U+2013  EN DASH
        '—', // U+2014  EM DASH
        '…', // U+2026  HORIZONTAL ELLIPSIS (pause in speech)

        // Arabic pause punctuation
        '،', // U+060C  ARABIC COMMA
        '؛', // U+061B  ARABIC SEMICOLON

        // East Asian pause punctuation
        '，', // U+FF0C  FULLWIDTH COMMA
        '、', // U+3001  IDEOGRAPHIC COMMA
        '；', // U+FF1B  FULLWIDTH SEMICOLON
        '：', // U+FF1A  FULLWIDTH COLON
        '﹐', // U+FE50  SMALL COMMA
        '﹑', // U+FE51  SMALL IDEOGRAPHIC COMMA
        '﹔', // U+FE54  SMALL SEMICOLON
        '﹕', // U+FE55  SMALL COLON

        // Mongolian pause punctuation
        '᠂', // U+1802  MONGOLIAN COMMA
        '᠄', // U+1804  MONGOLIAN COLON
    ];

    /// <summary>
    /// A comprehensive list of global abbreviations and titles that should NOT trigger a sentence split.
    /// Includes titles from English, Spanish, French, German, and Slavic languages.
    /// </summary>
    private static readonly HashSet<string> CommonTitles = new(StringComparer.OrdinalIgnoreCase)
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

                // A valid period must be followed by whitespace or another punctuation mark (e.g., "End. Next").
                if (char.IsWhiteSpace(nextChar) || SentenceTerminators.AsSpan().Contains(nextChar) || PauseMarks.AsSpan().Contains(nextChar))
                {
                    int nextVisibleCharIdx = nextTerminator + 1;
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
                // Capture trailing terminators (e.g., "Wait!!!" -> captures all three exclamation marks).
                while (endIndex < textSpan.Length && SentenceTerminators.AsSpan().Contains(textSpan[endIndex])) endIndex++;
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
            int splitIndex = FindLastOccurrence(sentenceSpan, currentIndex, windowEnd, PauseMarks);

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
                splitIndex = windowEnd;
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

    private static int FindLastOccurrence(ReadOnlySpan<char> text, int startIndex, int endIndex, char[] charsToFind)
    {
        ReadOnlySpan<char> window = text[startIndex..endIndex];
        int relativeIndex = window.LastIndexOfAny(charsToFind);

        return relativeIndex == -1 ? -1 : startIndex + relativeIndex;
    }
}