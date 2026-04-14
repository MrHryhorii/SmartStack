using ONNX_Runner.Models;
using System.Globalization;

namespace ONNX_Runner.Services;

public interface IPhonemizer
{
    long[] PhonemesToIds(string phonemes);
}

/// <summary>
/// Responsible for converting IPA phoneme strings into numeric ID arrays (tensors) that the Piper ONNX model can process.
/// This module implements the specific "Interspersing" logic required by VITS-based models.
/// </summary>
public class PiperPhonemizer(PiperConfig config) : IPhonemizer
{
    private readonly PiperConfig _config = config;

    /// <summary>
    /// Translates a phoneme string into a padded ID array.
    /// Uses high-performance Span-based parsing to minimize memory allocations.
    /// </summary>
    public long[] PhonemesToIds(string phonemes)
    {
        // Initial capacity of 128 is usually enough for a standard sentence to avoid list reallocations.
        var corePhonemes = new List<long>(128);

        // Retrieve service IDs from the model config, falling back to Piper defaults if missing.
        int sentenceStartId = _config.PhonemeIdMap.TryGetValue("^", out var startArr) ? startArr[0] : 1;
        int sentenceEndId = _config.PhonemeIdMap.TryGetValue("$", out var endArr) ? endArr[0] : 2;
        int padId = _config.PhonemeIdMap.TryGetValue("_", out var padArr) ? padArr[0] : 0;
        int spaceId = _config.PhonemeIdMap.TryGetValue(" ", out var spaceArr) ? spaceArr[0] : 3;

        // Sentence Initialization
        corePhonemes.Add(sentenceStartId);
        corePhonemes.Add(spaceId); // Piper models typically expect a leading space for better natural prosody.

        // =====================================================================
        // UNICODE PARSING (Zero-Allocation via Spans)
        // =====================================================================
        ReadOnlySpan<char> phonemeSpan = phonemes.AsSpan();
        int index = 0;

        while (index < phonemeSpan.Length)
        {
            // Extract the length of the next Unicode text element (phoneme/grapheme) without memory allocation.
            int length = StringInfo.GetNextTextElementLength(phonemeSpan[index..]);
            ReadOnlySpan<char> symbolSpan = phonemeSpan.Slice(index, length);

            // Handle line breaks by converting them to standard spaces to ensure consistent pauses.
            if (symbolSpan.Length == 1 && (symbolSpan[0] == '\n' || symbolSpan[0] == '\r'))
            {
                if (corePhonemes.Count > 0 && corePhonemes[^1] != spaceId)
                {
                    corePhonemes.Add(spaceId);
                }
            }
            else
            {
                // The dictionary lookup requires a string, so we convert the span here.
                string symbol = symbolSpan.ToString();

                // If the model's dictionary knows this symbol (phoneme, comma, space), append its ID.
                if (_config.PhonemeIdMap.TryGetValue(symbol, out var ids))
                {
                    corePhonemes.Add(ids[0]);
                }
            }

            index += length;
        }

        // Sentence Finalization with silence
        if (corePhonemes.Count > 0 && corePhonemes[^1] != spaceId)
        {
            corePhonemes.Add(spaceId);
        }
        corePhonemes.Add(sentenceEndId);

        // =====================================================================
        // INTERSPERSING (Architecture requirement)
        // =====================================================================
        // Neural TTS models like VITS/Piper require "Interspersing" — placing a padId (0) 
        // between every phoneme ID. This helps the model accurately predict audio durations.
        // We calculate the exact final tensor size: (Phoneme count) + (Padding count).
        int finalCount = corePhonemes.Count * 2 - 1;
        long[] resultArray = new long[finalCount];

        int resultIndex = 0;
        for (int i = 0; i < corePhonemes.Count; i++)
        {
            resultArray[resultIndex++] = corePhonemes[i];

            // Add padId (blank token) ONLY between elements, not at the very end.
            if (i < corePhonemes.Count - 1)
            {
                resultArray[resultIndex++] = padId;
            }
        }

        // Debug logging to verify the exact data being sent to the ONNX Runtime
        Console.WriteLine($"\n[DEBUG] Input Phonemes: {phonemes}");
        Console.WriteLine($"[DEBUG] Target Tensor Size: {resultArray.Length}\n");

        return resultArray;
    }
}