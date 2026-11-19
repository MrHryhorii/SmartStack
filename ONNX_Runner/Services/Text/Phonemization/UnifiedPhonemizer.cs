using ONNX_Runner.Models.Text;
using ONNX_Runner.Services.Text.Phonemization.Adaptation;
using ONNX_Runner.Services.Text;
using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Inference;

namespace ONNX_Runner.Services.Text.Phonemization;

/// <summary>
/// Central orchestrator for phonetic transcription. Integrates language detection,
/// eSpeak, punctuation mapping, and model-specific phoneme fallbacks into an optimized pipeline.
/// </summary>
public partial class UnifiedPhonemizer
{
    private readonly EspeakWrapper _espeakWrapper;
    private readonly DynamicPunctuationMapper _punctuationMapper;
    private readonly PiperConfig _piperConfig;
    private readonly MixedLanguagePhonemizer? _mixedPhonemizer;
    private readonly PhonemeFallbackMapper? _fallbackMapper;
    private readonly ConcurrentDictionary<char, PhonemeSequence> _technicalCharacterCache = new();

    // Cached model language metadata avoids splitting/normalizing the same eSpeak code per request.
    private readonly string _modelEspeakVoice;
    private readonly string _modelEspeakCode;
    private readonly string _canonicalModelEspeakCode;
    private readonly string[] _modelEspeakParts;
    /// <summary>Connects native phonemization, model punctuation/phoneme adaptation, and optional language routing.</summary>
    public UnifiedPhonemizer(
        EspeakWrapper espeakWrapper,
        DynamicPunctuationMapper punctuationMapper,
        PiperConfig piperConfig,
        MixedLanguagePhonemizer? mixedPhonemizer = null,
        PhonemeFallbackMapper? fallbackMapper = null,
        PhonemizerSettings? settings = null)
    {
        _espeakWrapper = espeakWrapper;
        _punctuationMapper = punctuationMapper;
        _piperConfig = piperConfig;
        _mixedPhonemizer = mixedPhonemizer;
        _fallbackMapper = fallbackMapper;
        _readEmojis = settings?.ReadEmojis ?? true;

        _modelEspeakVoice = _piperConfig.Espeak.Voice ?? EspeakConfig.DefaultVoice;
        _modelEspeakCode = _modelEspeakVoice
            .Trim()
            .ToLowerInvariant();
        _canonicalModelEspeakCode = CanonicalizeEspeakVoiceCode(_modelEspeakCode);

        _modelEspeakParts = _modelEspeakCode.Split(
            ['-', '_'],
            StringSplitOptions.RemoveEmptyEntries);
    }

    // Mirrors MixedLanguagePhonemizer's own forced-language resolution (smart inheritance +
    // trim/lowercase normalization) for the no-detector fallback, so the "language" parameter
    // behaves the same regardless of whether the statistical detector is registered.
    private string ResolveFallbackLanguage(string? forcedLanguage)
    {
        if (string.IsNullOrWhiteSpace(forcedLanguage))
        {
            return _modelEspeakCode;
        }

        return MixedLanguagePhonemizer.ResolveForcedLanguageCode(
            forcedLanguage,
            _modelEspeakCode,
            _modelEspeakParts);
    }

    // Extracts raw IPA blocks to prevent the language detector and eSpeak
    // from treating hand-written phonemes as ordinary orthographic text.
    private static List<(string Text, bool IsRawPhonemes)> SplitRawPhonemeBlocks(string text)
    {
        var segments = new List<(string Text, bool IsRawPhonemes)>(4);
        int lastEnd = 0;
        ReadOnlySpan<char> textSpan = text.AsSpan();

        while (PhonemeBlockRecognizer.TryFindNext(textSpan, lastEnd, textSpan.Length, out var block))
        {
            if (block.Start > lastEnd)
            {
                segments.Add((textSpan[lastEnd..block.Start].ToString(), false));
            }

            segments.Add((textSpan[block.ContentStart..block.ContentEnd].ToString(), true));
            lastEnd = block.End;
        }

        if (lastEnd < text.Length)
        {
            segments.Add((text[lastEnd..], false));
        }

        return segments;
    }

    /// <summary>
    /// Converts a raw input string into a continuous stream of validated IPA phonemes.
    /// Foreign IPA is preserved whenever the loaded model supports it and otherwise adapted
    /// through model-specific complex-sequence rules and PHOIBLE nearest-neighbor fallbacks.
    /// </summary>
    public string GetPhonemes(string text, string? forcedLanguage = null) => ProcessText(text, forcedLanguage, false).Text;

    /// <summary>Retains prepared group encodings for the synthesis path without embedding metadata in IPA.</summary>
    public PhonemeSequence GetSequence(string text, string? forcedLanguage = null) => ProcessText(text, forcedLanguage, true);

    private PhonemeSequence ProcessText(string text, string? forcedLanguage, bool captureAdaptations)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new(string.Empty);
        }

        var finalPhonemes = new PhonemeOutput(Math.Max(64, text.Length * 2), captureAdaptations);
        try
        {
            var tokens = new List<TextChunk>(8);

            // Pre-process raw IPA blocks (Green Channel) and apply smart mode selection.
            foreach (var (segmentText, isRaw) in SplitRawPhonemeBlocks(text))
            {
                if (isRaw)
                {
                    tokens.Add(new TextChunk
                    {
                        Text = segmentText,
                        DetectedLanguage = "raw",
                        IsRawPhonemes = true
                    });

                    continue;
                }

                if (string.IsNullOrEmpty(segmentText))
                {
                    continue;
                }

                // Language segmentation must see the original orthography. Normalizing punctuation
                // first would destroy structural evidence inside URLs, paths, emails, code-like tokens,
                // and abbreviations before the mixed-language tokenizer can protect them. Raw IPA blocks
                // were already extracted above and remain completely untouched.
                if (string.IsNullOrWhiteSpace(segmentText))
                {
                    tokens.Add(new TextChunk
                    {
                        Text = segmentText,
                        DetectedLanguage = _modelEspeakCode,
                        IsPunctuationOrSpace = true
                    });
                    continue;
                }

                // Pass forcedLanguage to the detector. If the detector is disabled, resolve the
                // language locally using the same smart-inheritance rule.
                if (_mixedPhonemizer != null)
                {
                    tokens.AddRange(
                        _mixedPhonemizer.ProcessTextToLanguageTokens(
                            segmentText,
                            forcedLanguage));

                    continue;
                }

                AddFallbackTokens(segmentText, ResolveFallbackLanguage(forcedLanguage), tokens);
            }

            bool previousTechnicalSpeech = false;
            for (int tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
            {
                TextChunk chunk = tokens[tokenIndex];

                if (chunk.IsRawPhonemes)
                {
                    // Green channel: bypass eSpeak entirely for pre-written IPA, but still validate
                    // it against the current model so unsupported foreign sounds are adapted safely.
                    AppendValidatedPhonemes(chunk.Text, ref finalPhonemes);
                    previousTechnicalSpeech = false;
                    continue;
                }

                if (TryAppendEmojiChunk(chunk, tokenIndex, tokens, ref finalPhonemes, ref previousTechnicalSpeech)) continue;
                AppendTextChunk(chunk, tokenIndex, tokens, ref finalPhonemes, ref previousTechnicalSpeech);
            }

            return finalPhonemes.Build();
        }
        finally { finalPhonemes.Dispose(); }
    }

    /// <summary>Appends a chunk using technical named reading or ordinary punctuation-aware phonemization.</summary>
    private void AppendTextChunk(TextChunk chunk, int tokenIndex, List<TextChunk> tokens,
        ref PhonemeOutput finalPhonemes, ref bool previousTechnicalSpeech)
    {
        if (chunk.IsTechnical &&
            TechnicalTextRecognizer.ShouldSpeak(chunk.Text.AsSpan(), _punctuationMapper.Rules))
        {
            AppendTechnicalPhonemes(chunk.Text, chunk.DetectedLanguage, ref finalPhonemes);
            previousTechnicalSpeech = true;
            return;
        }

        // Punctuation normalization intentionally happens AFTER language segmentation. The
        // structural tokenizer therefore sees original technical syntax, while eSpeak/Piper
        // still receives only punctuation supported (or safely degraded) by the loaded model.
        string normalizedText = _punctuationMapper.Normalize(chunk.Text);
        if (normalizedText.Length == 0)
        {
            return;
        }

        if (chunk.IsPunctuationOrSpace)
        {
            AppendPunctuationToken(
                chunk,
                normalizedText,
                tokenIndex,
                tokens,
                finalPhonemes.Builder);

            return;
        }

        ReadOnlySpan<char> chunkSpan = normalizedText.AsSpan();

        // PREFIX, CORE, AND SUFFIX EXTRACTION:
        // Isolates the core word from surrounding punctuation to prevent eSpeak mispronunciations.
        int start = FindCoreStart(chunkSpan);
        int end = FindCoreEnd(chunkSpan, start);

        finalPhonemes.Append(chunkSpan[..start]);

        if (start < end)
        {
            string core = chunkSpan[start..end].ToString();
            string? rawPhonemes = TryGetIpaPhonemesSafely(
                core,
                chunk.DetectedLanguage);

            if (rawPhonemes != null)
            {
                // A silent quote can abut a technical name without source whitespace.
                if (previousTechnicalSpeech) EspeakPhonemePartJoiner.Separate(finalPhonemes.Builder, rawPhonemes);
                previousTechnicalSpeech = false;
                if (_fallbackMapper != null)
                {
                    AppendValidatedPhonemes(rawPhonemes, ref finalPhonemes);
                }
                else
                {
                    finalPhonemes.Append(rawPhonemes);
                }
            }
        }

        finalPhonemes.Append(chunkSpan[end..]);
    }

    // Splits a protected token without allocating a list of parts or spelling names.
    // Number/version/time/IP exceptions are handled before this method.
    private void AppendTechnicalPhonemes(string text, string detectedLanguage, ref PhonemeOutput output)
    {
        int start = 0;

        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (!_punctuationMapper.Rules.IsTechnicalSpeechSymbol(character))
            {
                continue;
            }

            if (index > start)
            {
                AppendTechnicalPart(text[start..index], detectedLanguage, ref output);
            }

            if (!_technicalCharacterCache.TryGetValue(character, out PhonemeSequence phonemes))
            {
                if (!_espeakWrapper.TryGetCharacterPhonemes(character, _modelEspeakVoice, out string nativeIpa))
                {
                    throw new InvalidOperationException(
                        $"Failed to select configured eSpeak voice '{_modelEspeakVoice}' for technical symbol.");
                }

                phonemes = _technicalCharacterCache.GetOrAdd(character, AdaptPhonemes(nativeIpa));
            }

            EspeakPhonemePartJoiner.Separate(output.Builder, phonemes.Text);
            output.Append(phonemes);
            start = index + 1;
        }

        if (start < text.Length)
        {
            AppendTechnicalPart(text[start..], detectedLanguage, ref output);
        }
    }

    /// <summary>Phonemizes a technical text fragment in its selected language and appends validated model-compatible phonemes.</summary>
    private void AppendTechnicalPart(string text, string language, ref PhonemeOutput output)
    {
        string? phonemes = TryGetIpaPhonemesSafely(text, language);
        if (string.IsNullOrWhiteSpace(phonemes))
        {
            return;
        }

        EspeakPhonemePartJoiner.Separate(output.Builder, phonemes);
        AppendValidatedPhonemes(phonemes, ref output);
    }

    // The same structural classification is used without creating a statistical detector.
    private void AddFallbackTokens(string text, string language, List<TextChunk> tokens)
    {
        int ordinaryStart = 0;
        int index = 0;
        while (index < text.Length)
        {
            if (TechnicalTextRecognizer.TryGetSpanLength(text.AsSpan(), index,
                _punctuationMapper.Rules, out int length))
            {
                if (index > ordinaryStart) AddOrdinary(text[ordinaryStart..index]);
                tokens.Add(new TextChunk
                {
                    Text = text.Substring(index, length), DetectedLanguage = language, IsTechnical = true
                });
                index += length;
                ordinaryStart = index;
                continue;
            }
            // Scan each ordinary lexical token once, rather than repeatedly scanning its suffixes.
            bool boundary = TechnicalTextRecognizer.IsBoundary(text[index], _punctuationMapper.Rules) ||
                char.GetUnicodeCategory(text[index]) is UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation;
            index++;
            if (boundary) continue;
            while (index < text.Length && !TechnicalTextRecognizer.IsBoundary(text[index], _punctuationMapper.Rules)) index++;
        }
        if (ordinaryStart < text.Length) AddOrdinary(ordinaryStart == 0 ? text : text[ordinaryStart..]);

        // Adds an ordinary fragment using the explicitly selected language.
        void AddOrdinary(string value) => tokens.Add(new TextChunk
        {
            Text = value, DetectedLanguage = language, IsPunctuationOrSpace = false
        });
    }

    // Appends a punctuation token while suppressing title and acronym periods.
    private void AppendPunctuationToken(
        TextChunk originalChunk,
        string normalizedText,
        int tokenIndex,
        List<TextChunk> tokens,
        StringBuilder output)
    {
        // TITLE & ACRONYM PERIOD STRIPPING:
        // Look back at the previous token to determine if the period belongs to a title or initial.
        bool skipPeriod = false;

        if (originalChunk.Text == "." &&
            tokenIndex > 0 &&
            tokenIndex < tokens.Count - 1)
        {
            ReadOnlySpan<char> prevText = tokens[tokenIndex - 1].Text.AsSpan().TrimEnd();
            int lastSpaceIdx = prevText.LastIndexOf(' ');
            ReadOnlySpan<char> lastWord = prevText[(lastSpaceIdx + 1)..];

            bool isTitle = _punctuationMapper.Rules.CommonAbbreviations
                .GetAlternateLookup<ReadOnlySpan<char>>()
                .Contains(lastWord);

            bool isAcronym = IsSingleLetter(lastWord);
            skipPeriod = isTitle || isAcronym;
        }

        if (!skipPeriod)
        {
            // Append the model-native punctuation selected after structural language tokenization.
            // A sentence-final abbreviation period is intentionally preserved for TTS prosody.
            output.Append(normalizedText);
        }
    }

    // Atomically selects an eSpeak voice and phonemizes the text without falling back across
    // unrelated language families. Voice selection and native transcription happen under one
    // EspeakWrapper lock, eliminating the SetVoice -> GetIpaPhonemes race between requests.
    private string? TryGetIpaPhonemesSafely(string text, string language)
    {
        string resolvedLanguage = CanonicalizeEspeakVoiceCode(language);

        if (_espeakWrapper.TryGetIpaPhonemes(
                text,
                resolvedLanguage,
                out string phonemes))
        {
            return phonemes;
        }

        // Falling back to an English/model voice for a foreign script produces spoken labels
        // such as "Chinese letter" for every Han character. Only use the model voice when the
        // failed code belongs to the same base language family; otherwise skip that core safely.
        if (!HasSameBaseLanguage(
                resolvedLanguage,
                _canonicalModelEspeakCode))
        {
            return null;
        }

        return _espeakWrapper.TryGetIpaPhonemes(
            text,
            _modelEspeakVoice,
            out phonemes)
                ? phonemes
                : null;
    }

    // Normalizes equivalent eSpeak language aliases to one canonical code.
    private static string CanonicalizeEspeakVoiceCode(string language)
    {
        if (language.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("zh-cn", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("zh-hans", StringComparison.OrdinalIgnoreCase))
        {
            return "cmn";
        }

        return language;
    }

    // Checks whether two eSpeak voice codes belong to the same base language family.
    private static bool HasSameBaseLanguage(string left, string right)
    {
        ReadOnlySpan<char> leftSpan = left.AsSpan();
        ReadOnlySpan<char> rightSpan = right.AsSpan();

        int leftSeparator = leftSpan.IndexOfAny('-', '_');
        int rightSeparator = rightSpan.IndexOfAny('-', '_');

        if (leftSeparator >= 0)
        {
            leftSpan = leftSpan[..leftSeparator];
        }

        if (rightSeparator >= 0)
        {
            rightSpan = rightSpan[..rightSeparator];
        }

        return leftSpan.Equals(rightSpan, StringComparison.OrdinalIgnoreCase);
    }

    // Identifies letters, decimal digits, and combining marks to define the "core" of a word.
    // Rune-based scanning prevents supplementary-plane letters from being mistaken for punctuation.
    private static bool IsCoreRune(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);

        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
    }

    // Finds the first rune that belongs to the speakable core of a token.
    private static int FindCoreStart(ReadOnlySpan<char> text)
    {
        int index = 0;

        while (index < text.Length)
        {
            int consumed = DecodeRune(text[index..], out Rune rune);

            if (IsCoreRune(rune))
            {
                return index;
            }

            index += consumed;
        }

        return text.Length;
    }

    // Finds the exclusive end of the speakable core of a token.
    private static int FindCoreEnd(ReadOnlySpan<char> text, int start)
    {
        int end = text.Length;

        while (end > start)
        {
            OperationStatus status = Rune.DecodeLastFromUtf16(
                text[..end],
                out Rune rune,
                out int consumed);

            if (status != OperationStatus.Done)
            {
                consumed = 1;
                rune = Rune.ReplacementChar;
            }

            if (IsCoreRune(rune))
            {
                return end;
            }

            end -= consumed;
        }

        return start;
    }

    // Checks whether a span contains exactly one Unicode letter.
    private static bool IsSingleLetter(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        int consumed = DecodeRune(text, out Rune rune);
        if (consumed != text.Length)
        {
            return false;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter;
    }

    // Decodes the first Unicode scalar and returns its UTF-16 width.
    private static int DecodeRune(ReadOnlySpan<char> text, out Rune rune)
    {
        OperationStatus status = Rune.DecodeFromUtf16(
            text,
            out rune,
            out int consumed);

        if (status == OperationStatus.Done)
        {
            return consumed;
        }

        rune = Rune.ReplacementChar;
        return 1;
    }

    private void AppendValidatedPhonemes(string phonemes, ref PhonemeOutput output)
    {
        if (_fallbackMapper == null) { output.Append(phonemes); return; }
        _fallbackMapper.Adapter.Append(phonemes.AsSpan(), ref output);
    }

    private PhonemeSequence AdaptPhonemes(string phonemes) =>
        _fallbackMapper?.Adapter.Adapt(phonemes) ?? new(phonemes);
}
