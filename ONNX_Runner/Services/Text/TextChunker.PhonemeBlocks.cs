using System.Globalization;
using ONNX_Runner.Services.Text.Phonemization;

namespace ONNX_Runner.Services.Text;

public partial class TextChunker
{
    /// <summary>Splits oversized raw blocks without losing their explicit phoneme interpretation.</summary>
    private bool TrySplitOversizedPhonemeBlocks(string source, int start, int length, bool isSentenceFinished, List<TextChunk> result)
    {
        int limit = start + length;
        if (length <= _maxLength || !TryFindOversizedPhonemeBlock(source.AsSpan(), start, limit, out var block)) return false;

        int cursor = start;
        do
        {
            int prefixStart = FindPhonemePrefixStart(source.AsSpan(), cursor, block.Start);
            AddSentence(source, source.AsSpan(cursor, prefixStart - cursor), false, result);

            ReadOnlySpan<char> context = source.AsSpan(0, limit);
            int delimiterEnd = PhonemeDelimiterEnd(context, block.End);
            int suffixEnd = ProtectEmergencyPunctuationSuffix(context, block.ContentStart, delimiterEnd, delimiterEnd, true);
            AddPhonemeFragments(source, block, prefixStart, suffixEnd,
                suffixEnd == limit && isSentenceFinished, result);
            cursor = suffixEnd;
        }
        while (TryFindOversizedPhonemeBlock(source.AsSpan(), cursor, limit, out block));

        AddSentence(source, source.AsSpan(cursor, limit - cursor), isSentenceFinished, result);
        return true;
    }

    private static int PhonemeDelimiterEnd(ReadOnlySpan<char> text, int end) =>
        end - 1 + StringInfo.GetNextTextElementLength(text[(end - 1)..]);

    private int FindPhonemePrefixStart(ReadOnlySpan<char> text, int start, int opening)
    {
        while (opening > start)
        {
            int previous = opening - 1;
            if (char.IsLowSurrogate(text[previous]) || char.GetUnicodeCategory(text[previous]) is
                UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                previous = PreviousTextElementStart(text, start, opening);
            char value = text[previous];
            if (!Rules.OpeningPunctuation.Contains(value) &&
                !(Rules.VisualQuotes.Contains(value) && !IsClosingPunctuationAt(text, previous))) break;
            opening = previous;
        }
        return opening;
    }

    private bool TryFindOversizedPhonemeBlock(ReadOnlySpan<char> text, int start, int limit, out PhonemeBlockRecognizer.Block block)
    {
        while (PhonemeBlockRecognizer.TryFindNext(text, start, limit, out block))
        {
            if (block.End > limit) break;
            if (block.Length > _maxLength) return true;
            ReadOnlySpan<char> context = text[..limit];
            int end = PhonemeDelimiterEnd(context, block.End);
            end = ProtectEmergencyPunctuationSuffix(context, block.ContentStart, end, end, true);
            if (end - FindPhonemePrefixStart(text, start, block.Start) > _maxLength) return true;
            start = block.End;
        }
        block = default;
        return false;
    }

    private void AddPhonemeFragments(string source, PhonemeBlockRecognizer.Block block, int prefixStart, int suffixEnd,
        bool isSentenceFinished, List<TextChunk> result)
    {
        ReadOnlySpan<char> content = source.AsSpan(block.ContentStart, block.ContentEnd - block.ContentStart);
        int position = 0;
        int lastContentStart = 0;
        for (int index = 0; index < content.Length;)
        {
            int end = NextPhonemeElementEnd(content, index);
            if (HasContent(content[index..end])) lastContentStart = index;
            index = end;
        }

        while (position < content.Length)
        {
            int prefixLength = position == 0 ? block.Start - prefixStart : 0;
            int suffixLength = suffixEnd - block.End;
            int capacity = Math.Max(1, _maxLength - 4 - prefixLength);
            int preferredEnd = position + Math.Min(content.Length - position, capacity);
            int end = FindPhonemeFragmentEnd(content, position, preferredEnd);
            bool last = end == content.Length;
            if (last && end - position + 4 + prefixLength + suffixLength > _maxLength && lastContentStart > position)
            {
                end = lastContentStart;
                last = false;
            }

            string chunk = MaterializePhonemeFragment(source, prefixStart, prefixLength,
                block.ContentStart + position, end - position, block.End, last ? suffixLength : 0);
            result.Add(new TextChunk(chunk, last && isSentenceFinished));
            position = end;
        }
    }

    /// <summary>Uses ordinary pause/space preferences, with phonetic elements as the hard-cut minimum.</summary>
    private int FindPhonemeFragmentEnd(ReadOnlySpan<char> text, int start, int preferredEnd)
    {
        int lastSafe = start, pause = -1, space = -1;
        bool hasContent = false;
        for (int index = start; index < preferredEnd;)
        {
            int end = NextPhonemeElementEnd(text, index);
            if (end > preferredEnd) break;
            ReadOnlySpan<char> element = text[index..end];
            hasContent |= HasContent(element);
            if (hasContent && element.IndexOfAny(Rules.PauseMarks) >= 0) pause = end;
            if (hasContent && element.IsWhiteSpace()) space = end;
            lastSafe = end;
            index = end;
        }
        if (lastSafe == text.Length) return lastSafe;
        if (pause > start) return pause;
        if (space > start) return space;
        return lastSafe > start ? lastSafe : NextPhonemeElementEnd(text, start);
    }

    /// <summary>Combines IPA attachment rules with complete tokens exposed by the active model.</summary>
    private int NextPhonemeElementEnd(ReadOnlySpan<char> text, int start)
    {
        int end = NextIpaElementEnd(text, start);
        if (_phonemeInventory == null) return end;
        int cursor = start;
        while (cursor < end)
        {
            int length = _phonemeInventory.TryMatch(text[cursor..], out int matched, out _)
                ? matched : PhonemeInventory.ScalarLength(text[cursor..]);
            int tokenEnd = cursor + length;
            while (end < tokenEnd) end = NextIpaElementEnd(text, end);
            cursor = tokenEnd;
        }
        return end;
    }

    /// <summary>Keeps diacritics, tied consonants, stress, modifiers and tone sequences on their phonetic base.</summary>
    private static int NextIpaElementEnd(ReadOnlySpan<char> text, int start)
    {
        if (char.IsWhiteSpace(text[start])) return start + StringInfo.GetNextTextElementLength(text[start..]);
        int end = start;
        bool join;
        do
        {
            while (end < text.Length && (SingleBracketPhonemeRecognizer.IsPrefixMark(text[end]) ||
                SingleBracketPhonemeRecognizer.IsModifier(text[end])))
                end += StringInfo.GetNextTextElementLength(text[end..]);
            if (end >= text.Length) break;

            int elementStart = end;
            end += StringInfo.GetNextTextElementLength(text[end..]);
            join = text[elementStart..end].IndexOfAny('\u035C', '\u0361') >= 0;
            while (end < text.Length && SingleBracketPhonemeRecognizer.IsAttachedMark(text[end]))
                end += StringInfo.GetNextTextElementLength(text[end..]);
            if (end < text.Length && text[end] == '‿')
            {
                end += StringInfo.GetNextTextElementLength(text[end..]);
                join = true;
            }
        }
        while (join && end < text.Length && !char.IsWhiteSpace(text[end]) && !char.IsPunctuation(text[end]));

        // Attached punctuation is emitted with a content element, including complete ?! / ...?! clusters.
        while (end < text.Length && char.IsPunctuation(text[end]) && text[end] != '‿')
            end += StringInfo.GetNextTextElementLength(text[end..]);
        return end;
    }

    private static string MaterializePhonemeFragment(string source, int prefixStart, int prefixLength,
        int contentStart, int contentLength, int suffixStart, int suffixLength)
    {
        return string.Create(prefixLength + contentLength + suffixLength + 4,
            (source, prefixStart, prefixLength, contentStart, contentLength, suffixStart, suffixLength), static (output, state) =>
            {
                state.source.AsSpan(state.prefixStart, state.prefixLength).CopyTo(output);
                int cursor = state.prefixLength;
                "[[".AsSpan().CopyTo(output[cursor..]);
                cursor += 2;
                state.source.AsSpan(state.contentStart, state.contentLength).CopyTo(output[cursor..]);
                cursor += state.contentLength;
                "]]".AsSpan().CopyTo(output[cursor..]);
                cursor += 2;
                state.source.AsSpan(state.suffixStart, state.suffixLength).CopyTo(output[cursor..]);
            });
    }
}
