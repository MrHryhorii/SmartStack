using System.Globalization;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Text;
using ONNX_Runner.Services.Text.Phonemization;

namespace ONNX_Runner.Services.Text;

/// <summary>
/// Multilingual sentence chunker using one immutable shared punctuation and abbreviation catalog.
/// Boundary heuristics deliberately preserve ambiguous uppercase initials and uncased letters.
/// </summary>
public partial class TextChunker(ChunkerSettings settings, TextChunkerRules? rules = null,
    Dictionary<string, int[]>? phonemeIdMap = null)
{
    public TextChunkerRules Rules { get; } = rules ?? TextChunkerRules.Default;

    private readonly int _maxLength = settings.EffectiveMaxChunkLength;
    private readonly PhonemeInventory? _phonemeInventory = phonemeIdMap is { Count: > 0 } ? new(phonemeIdMap) : null;
    private const string EmergencyGlue = "-";

    /// <summary>
    /// One text chunk plus the boundary information already known by the chunker.
    /// </summary>
    public readonly record struct TextChunk(string Text, bool IsSentenceFinished);

    /// <summary>
    /// Chunks text while respecting semantic sentence boundaries. When EarlySplit is enabled,
    /// only the first non-empty semantic sentence gets one opportunity to end early at a
    /// conservative clause or quoted-continuation boundary. All following text uses normal sentence chunking and
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
                ReadOnlySpan<char> firstSpan = TrimBoundaryWhitespace(textSpan[currentIndex..firstEndIndex]);

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
                    ReadOnlySpan<char> earlyChunk = TrimBoundaryWhitespace(textSpan[currentIndex..earlyEndIndex]);
                    if (!earlyChunk.IsEmpty)
                    {
                        AddSentence(text, earlyChunk, false, result);
                    }

                    currentIndex = earlyEndIndex;
                }
                else
                {
                    AddSentence(text, textSpan[currentIndex..firstEndIndex], firstSentenceFinished, result);
                    currentIndex = firstEndIndex;
                }

                break;
            }
        }

        while (currentIndex < textSpan.Length)
        {
            int endIndex = FindSentenceEnd(textSpan, currentIndex, out bool isSentenceFinished);
            AddSentence(text, textSpan[currentIndex..endIndex], isSentenceFinished, result);
            currentIndex = endIndex;
        }

        return result;
    }

    /// <summary>
    /// Adds one normal sentence or routes an oversized sentence through emergency splitting.
    /// </summary>
    private void AddSentence(
        string originalText,
        ReadOnlySpan<char> sentenceSpan,
        bool isSentenceFinished,
        List<TextChunk> result)
    {
        sentenceSpan = TrimBoundaryWhitespace(sentenceSpan);
        if (sentenceSpan.IsEmpty) return;

        if (sentenceSpan.Length <= _maxLength)
        {
            // A whole-input sentence is known to produce one result. Avoid the default
            // four-element backing array without guessing capacity for multi-chunk input.
            if (sentenceSpan.Length == originalText.Length) result.Capacity = 1;
            result.Add(new TextChunk(MaterializeChunk(originalText, sentenceSpan), isSentenceFinished));
            return;
        }

        SplitLongSentence(originalText, sentenceSpan, isSentenceFinished, result);
    }

    // Every supplied span is a slice of originalText. Reuse the input for a whole-text
    // chunk; output slices and emergency glue still require their own immutable strings.
    private static string MaterializeChunk(string originalText, ReadOnlySpan<char> chunk)
    {
        return chunk.Length == originalText.Length ? originalText : chunk.ToString();
    }

    // Whitespace can be the base of a combining sequence. Trim complete whitespace
    // elements without detaching their marks; the common unpadded path needs no scan.
    private static ReadOnlySpan<char> TrimBoundaryWhitespace(ReadOnlySpan<char> text)
    {
        while (!text.IsEmpty && char.IsWhiteSpace(text[0]))
        {
            int length = StringInfo.GetNextTextElementLength(text);
            if (!text[..length].IsWhiteSpace()) break;
            text = text[length..];
        }

        return text.TrimEnd();
    }
}
