using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// High-performance text processing module responsible for splitting input text into manageable chunks.
/// It uses "Smart Splitting" logic to identify sentence boundaries while protecting abbreviations, 
/// titles, and initials from being accidentally sliced.
/// </summary>
public class TextChunker(ChunkerSettings settings)
{
    // High-performance search values for common sentence terminators across multiple languages.
    private static readonly System.Buffers.SearchValues<char> s_sentenceTerminators = System.Buffers.SearchValues.Create(".!?\n。！？؟۔։;");

    // Limits the length of a single audio generation task to prevent GPU timeouts.
    private readonly int _maxLength = settings.MaxChunkLength > 50 ? settings.MaxChunkLength : 250;

    // Symbol used to indicate an "emergency" split in the middle of a sentence (helps with prosody).
    private const string EmergencyGlue = "_";

    private static readonly char[] SentenceTerminators = ['.', '!', '?', '\n', '。', '！', '？', '؟', '۔', '։', ';'];
    private static readonly char[] PauseMarks = [',', ';', ':', '-', '—', '，', '、', '；', '：'];

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
        "вул", "пров", "просп", "бул", "обл", "пл", "ім", "буд", "кв", "мкр", "р-н", "пт", "сел", "смт", "рис", "табл", "див", "пор", "напр"
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
                if (char.IsWhiteSpace(nextChar) || SentenceTerminators.Contains(nextChar) || PauseMarks.Contains(nextChar))
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
                    bool isAbbreviation = false;

                    // ABBREVIATION DETECTION RULES:
                    // 1. Single character initials (e.g., "A. Smith").
                    if (wordBeforeDot.Length == 1 && char.IsLetter(wordBeforeDot[0]))
                    {
                        isAbbreviation = true;
                    }
                    // 2. Next word is lowercase (e.g., "He lived on St. john street").
                    else if (isNextLower)
                    {
                        isAbbreviation = true;
                    }
                    // 3. Mixed segments check (Distinguishes "U.S.A." from a URL like "site.com").
                    else if (wordBeforeDot.IndexOf('.') != -1)
                    {
                        int maxSegmentLength = 0;
                        int currentSegmentLength = 0;

                        for (int i = 0; i < wordBeforeDot.Length; i++)
                        {
                            if (wordBeforeDot[i] == '.')
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
                    // 4. Global Titles Dictionary check.
                    else
                    {
                        if (CommonTitles.Contains(wordBeforeDot.ToString()))
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
                while (endIndex < textSpan.Length && SentenceTerminators.Contains(textSpan[endIndex])) endIndex++;
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