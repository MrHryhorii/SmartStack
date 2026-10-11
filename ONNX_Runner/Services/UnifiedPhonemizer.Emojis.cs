using System.Collections.Concurrent;
using System.Text;

namespace ONNX_Runner.Services;

public partial class UnifiedPhonemizer
{
    private readonly bool _readEmojis;
    private readonly ConcurrentDictionary<string, string> _emojiCache = new(StringComparer.Ordinal);

    // Language analysis has already finished. Emoji spelling cannot cast a new language vote.
    private bool TryAppendEmojiChunk(TextChunk chunk, int tokenIndex, List<TextChunk> tokens,
        StringBuilder output, ref bool previousTechnicalSpeech)
    {
        int relative = EmojiTextRecognizer.FindNext(chunk.Text.AsSpan(), _punctuationMapper.Rules, out int length);
        if (relative < 0) return false;

        if (!_readEmojis)
        {
            string text = RemoveEmojis(chunk.Text, relative, length);
            AppendTextChunk(chunk with { Text = text }, tokenIndex, tokens, output, ref previousTechnicalSpeech);
            return true;
        }

        int start = 0;
        while (relative >= 0)
        {
            int index = start + relative;
            if (index > start)
                AppendTextChunk(chunk with { Text = chunk.Text[start..index] }, tokenIndex, tokens,
                    output, ref previousTechnicalSpeech);

            AppendEmojiPhonemes(chunk.Text.Substring(index, length), output);
            // A skipped emoji still separates adjacent words or symbol descriptions.
            previousTechnicalSpeech = true;
            start = index + length;
            relative = EmojiTextRecognizer.FindNext(chunk.Text.AsSpan(start), _punctuationMapper.Rules, out length);
        }

        if (start < chunk.Text.Length)
            AppendTextChunk(chunk with { Text = chunk.Text[start..] }, tokenIndex, tokens,
                output, ref previousTechnicalSpeech);
        return true;
    }

    // Keep normal phrase phonemization intact when spelling is disabled. Only insert a
    // separator where removal would otherwise concatenate two adjacent lexical words.
    private string RemoveEmojis(string text, int relative, int length)
    {
        var output = new StringBuilder(text.Length);
        bool separate = false;
        int start = 0;
        while (relative >= 0)
        {
            int index = start + relative;
            AppendPart(text.AsSpan(start, index - start));
            start = index + length;
            relative = EmojiTextRecognizer.FindNext(text.AsSpan(start), _punctuationMapper.Rules, out length);
        }
        AppendPart(text.AsSpan(start));
        return output.ToString();

        void AppendPart(ReadOnlySpan<char> part)
        {
            if (part.IsEmpty) return;
            if (separate && Rune.DecodeFromUtf16(part, out Rune first, out _) == System.Buffers.OperationStatus.Done && IsCoreRune(first))
                output.Append(' ');
            output.Append(part);
            separate = Rune.DecodeLastFromUtf16(part, out Rune last, out _) == System.Buffers.OperationStatus.Done && IsCoreRune(last);
        }
    }

    private void AppendEmojiPhonemes(string emoji, StringBuilder output)
    {
        if (!_emojiCache.TryGetValue(emoji, out string? phonemes))
        {
            if (!_espeakWrapper.TryGetEmojiPhonemes(emoji, _modelEspeakVoice, out string nativeIpa))
                throw new InvalidOperationException($"Failed to select configured eSpeak voice '{_modelEspeakVoice}' for emoji.");
            var adapted = new StringBuilder(nativeIpa.Length);
            AppendValidatedPhonemes(nativeIpa, adapted);
            phonemes = _emojiCache.GetOrAdd(emoji, adapted.ToString());
        }
        EspeakPhonemePartJoiner.Append(output, phonemes);
    }
}
