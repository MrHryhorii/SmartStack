using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ONNX_Runner;

public sealed class TtsInferenceEngine(TtsModelManager modelManager, TextProcessor textProcessor) : IDisposable
{
    private readonly TtsModelManager _modelManager = modelManager;
    private readonly TextProcessor _textProcessor = textProcessor;
    private readonly object _lock = new();

    public float[] GenerateSpeech(string text, string voiceSamplePath)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<float>();

        lock (_lock)
        {
            // 1. Токенізуємо текст через TextProcessor
            int[] textTokens = _textProcessor.Tokenize(text);

            // 2. Кодуємо голос
            var (spkEmbeds, spkFeats) = RunSpeechEncoder(voiceSamplePath);

            // 3. Генеруємо акустичні токени (Prefill + Decode)
            var audioTokens = RunAutoregressiveLmWithKvCache(textTokens);

            // 4. Декодуємо у звукову хвилю
            return RunVocoder(audioTokens, spkEmbeds, spkFeats);
        }
    }

    private (float[] Embeds, float[] Feats) RunSpeechEncoder(string voicePath)
    {
        string binPath = Path.ChangeExtension(voicePath, ".bin");
        if (File.Exists(binPath)) return AudioProcessor.LoadVoiceSnapshot(binPath);

        Console.WriteLine("Step 1: Running Speech Encoder...");
        float[] audioValues = AudioProcessor.LoadVoiceSample(voicePath);
        using var results = _modelManager.SpeechEncoder.Run(new[] {
            NamedOnnxValue.CreateFromTensor("audio_values", new DenseTensor<float>(audioValues, new[] { 1, audioValues.Length }))
        });

        float[] embeds = results.First(o => o.Name == "speaker_embeddings").AsTensor<float>().ToArray();
        float[] feats = results.First(o => o.Name == "speaker_features").AsTensor<float>().ToArray();

        AudioProcessor.SaveVoiceSnapshot(binPath, embeds, feats);
        return (embeds, feats);
    }

    private long[] RunAutoregressiveLmWithKvCache(int[] promptIds)
    {
        Console.WriteLine("Step 2: Starting Autoregressive LM loop (Prefill + Decode)...");
        var pastKeyValues = CreateEmptyKvCache();
        var generated = new List<long>();

        long[] currentTokens = promptIds.Select(i => (long)i).ToArray();
        int totalSeqLen = currentTokens.Length;

        var eosIds = _textProcessor.GenConfig.GetEosIds();
        float penalty = _textProcessor.GenConfig.RepetitionPenalty;

        for (int step = 0; step < 2000; step++)
        {
            var embedInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(currentTokens, new[] { 1, currentTokens.Length })),
                NamedOnnxValue.CreateFromTensor("position_ids", CreatePositionIds(totalSeqLen - currentTokens.Length, currentTokens.Length)),
                NamedOnnxValue.CreateFromTensor("exaggeration", new DenseTensor<float>(new[] { 1.0f }, new[] { 1 }))
            };

            using var embedOutputs = _modelManager.EmbedTokens.Run(embedInputs);
            var inputsEmbeds = embedOutputs.First().AsTensor<float>();

            long[] attentionMask = Enumerable.Repeat(1L, totalSeqLen).ToArray();
            var lmInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("inputs_embeds", inputsEmbeds),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, new[] { 1, totalSeqLen }))
            };

            foreach (var kv in pastKeyValues)
                lmInputs.Add(NamedOnnxValue.CreateFromTensor(kv.Key, kv.Value));

            using var lmOutputs = _modelManager.LanguageModel.Run(lmInputs);

            var logits = lmOutputs.First(o => o.Name == "logits").AsTensor<float>();
            ApplyRepetitionPenalty(logits, generated, penalty);

            long nextToken = SampleStochastic(logits, 0.8f, 50);

            if (eosIds.Contains(nextToken))
            {
                Console.WriteLine($"\n[LM] EOS reached at audio token {step}.");
                break;
            }

            generated.Add(nextToken);

            // Оновлюємо кеш (Надійно клонуємо масиви, щоб уникнути AccessViolation після Dispose)
            foreach (var r in lmOutputs.Where(o => o.Name.StartsWith("present.")))
            {
                string pastName = r.Name.Replace("present.", "past_key_values.");
                var tensor = r.AsTensor<float>();
                pastKeyValues[pastName] = new DenseTensor<float>(tensor.ToArray(), tensor.Dimensions.ToArray());
            }

            // Наступний крок: подаємо ТІЛЬКИ 1 новий згенерований токен
            currentTokens = new[] { nextToken };
            totalSeqLen++;

            if (step % 50 == 0) Console.Write(".");
        }

        return generated.ToArray();
    }

    private float[] RunVocoder(long[] audioTokens, float[] spkEmbeds, float[] spkFeats)
    {
        Console.WriteLine($"\nStep 3: Rendering {audioTokens.Length} tokens via Vocoder...");
        using var results = _modelManager.ConditionalDecoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("speech_tokens", new DenseTensor<long>(audioTokens, new[] { 1, audioTokens.Length })),
            NamedOnnxValue.CreateFromTensor("speaker_embeddings", new DenseTensor<float>(spkEmbeds, new[] { 1, spkEmbeds.Length })),
            NamedOnnxValue.CreateFromTensor("speaker_features", new DenseTensor<float>(spkFeats, new[] { 1, spkFeats.Length / 80, 80 }))
        });

        return results.First(o => o.Name == "waveform").AsTensor<float>().ToArray();
    }

    // ==========================================
    // HELPERS
    // ==========================================
    private static Dictionary<string, DenseTensor<float>> CreateEmptyKvCache()
    {
        var cache = new Dictionary<string, DenseTensor<float>>();
        var empty = new DenseTensor<float>(Array.Empty<float>(), new[] { 1, 16, 0, 64 });
        for (int i = 0; i < 30; i++)
        {
            cache[$"past_key_values.{i}.key"] = empty;
            cache[$"past_key_values.{i}.value"] = empty;
        }
        return cache;
    }

    private static DenseTensor<long> CreatePositionIds(int start, int length)
    {
        long[] pos = new long[length];
        for (int i = 0; i < length; i++) pos[i] = start + i;
        return new DenseTensor<long>(pos, new[] { 1, length });
    }

    private static void ApplyRepetitionPenalty(Tensor<float> logits, List<long> generated, float penalty)
    {
        if (generated.Count == 0 || penalty == 1.0f) return;
        var span = ((DenseTensor<float>)logits).Buffer.Span;
        int vocabSize = logits.Dimensions[2];
        int offset = (logits.Dimensions[1] - 1) * vocabSize;

        unsafe
        {
            fixed (float* ptr = span)
            {
                float* lastLogits = ptr + offset;
                foreach (var token in generated.Distinct())
                {
                    if (lastLogits[token] > 0) lastLogits[token] /= penalty;
                    else lastLogits[token] *= penalty;
                }
            }
        }
    }

    private static long SampleStochastic(Tensor<float> logits, float temp, int topK)
    {
        int vocabSize = logits.Dimensions[2];
        var span = ((DenseTensor<float>)logits).Buffer.Span;
        int offset = (logits.Dimensions[1] - 1) * vocabSize;

        var cands = new (float Logit, int Id)[vocabSize];
        unsafe
        {
            fixed (float* ptr = span)
            {
                float* lastLogits = ptr + offset;
                for (int i = 0; i < vocabSize; i++) cands[i] = (lastLogits[i], i);
            }
        }

        Array.Sort(cands, (a, b) => b.Logit.CompareTo(a.Logit));
        float maxL = cands[0].Logit;
        float sumExp = 0;
        float[] probs = new float[topK];

        for (int i = 0; i < topK; i++)
        {
            float exp = (float)Math.Exp((cands[i].Logit - maxL) / temp);
            probs[i] = exp;
            sumExp += exp;
        }

        float rnd = (float)Random.Shared.NextDouble();
        float cumulative = 0;
        for (int i = 0; i < topK; i++)
        {
            cumulative += probs[i] / sumExp;
            if (rnd <= cumulative) return cands[i].Id;
        }
        return cands[topK - 1].Id;
    }

    public void Dispose()
    {
    }
}