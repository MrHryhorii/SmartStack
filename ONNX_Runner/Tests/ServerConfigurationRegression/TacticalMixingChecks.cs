using System.Runtime.InteropServices;
using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class TacticalMixingChecks
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];
    private static readonly VoiceEffectType[] Effects = [VoiceEffectType.TacticalRadio];
    private static readonly float[] Amounts = [0, .001f, .00101f, .01f, .1f, .25f, .5f, .65f, .9f, .99f, .9999f, 1];
    internal static void Run(Action<string, Action> check)
    {
        using Stream stream = typeof(TacticalMixingChecks).Assembly.GetManifestResourceStream("TacticalCurveMoments.json")!;
        using JsonDocument corpus = JsonDocument.Parse(stream);
        foreach (JsonElement fixture in corpus.RootElement.GetProperty("Synthetic").EnumerateArray())
            check($"TacticalRadio retains its independent full radio fixture for {fixture.GetProperty("Name").GetString()}", () =>
            {
                int rate = fixture.GetProperty("Rate").GetInt32();
                float scale = fixture.GetProperty("Scale").GetSingle();
                JsonElement point = fixture.GetProperty("Points").EnumerateArray().Single(x => x.GetProperty("Amount").GetSingle() == 1);
                float[] actual = Signal(rate, rate * 3, 0, scale);
                Engine(rate).ApplyEffect(actual, VoiceEffectType.TacticalRadio, 1);
                double expectedPower = point.GetProperty("WetPower").GetDouble() * .63f * .63f;
                Require(Math.Abs(10 * Math.Log10(Power(actual) / expectedPower)) < .0001,
                    "The original full TacticalRadio power changed.");
                int[] indices = point.GetProperty("WetIndices").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                float[] samples = point.GetProperty("WetSamples").EnumerateArray().Select(x => x.GetSingle()).ToArray();
                for (int i = 0; i < indices.Length; i++)
                    Require(Math.Abs(actual[indices[i]] - samples[i] * .63f) < 3e-7,
                        "The original full TacticalRadio waveform changed.");
            });
        foreach (JsonElement voice in corpus.RootElement.GetProperty("Voices").EnumerateArray())
            check($"TacticalRadio matches the fixed endpoint powers for {voice.GetProperty("Name").GetString()}", () =>
            {
                double p = voice.GetProperty("DryPower").GetDouble();
                JsonElement full = voice.GetProperty("Points").EnumerateArray().Single(x => x.GetProperty("Amount").GetSingle() == 1);
                double q = full.GetProperty("WetPower").GetDouble() * .63f * .63f;
                double cross = full.GetProperty("Cross").GetDouble() * .63f;
                float[] dry = [(float)Math.Sqrt(2 * p), 0];
                float[] wet = [(float)(Math.Sqrt(2) * cross / Math.Sqrt(p)), (float)Math.Sqrt(2 * Math.Max(0, q - cross * cross / p))];
                double previous = 0;
                for (int i = 0; i <= 100; i++)
                {
                    float amount = i / 100f;
                    float[] output = (float[])wet.Clone();
                    Require(PowerMatchedMixer.Mix(dry, output, amount).Matched, "The fixed radio endpoints cancelled.");
                    double target = (1 - (double)amount) * p + amount * q;
                    double level = 10 * Math.Log10(Power(output) / p);
                    Require(Math.Abs(10 * Math.Log10(Power(output) / target)) < 1e-6,
                        "The fixed radio mix missed its measured endpoint power.");
                    if (i > 0) Require(q >= p ? level >= previous - 1e-6 : level <= previous + 1e-6,
                        "Pre-limiter radio power reversed its endpoint direction.");
                    previous = level;
                }
            });
        foreach (int rate in Rates)
        foreach (VoiceEffectType effect in Effects)
        {
            check($"{effect} interpolates fixed endpoints and restores measured power at {rate} Hz", () =>
            {
                float[] dry = Signal(rate, rate / 2), full = (float[])dry.Clone();
                Engine(rate).ApplyEffect(full, effect, 1);
                foreach (float amount in Amounts)
                {
                    float[] actual = (float[])dry.Clone();
                    Engine(rate).ApplyEffect(actual, effect, amount);
                    if (amount <= .001f) { Require(Same(dry, actual), "Radio bypass changed PCM."); continue; }
                    if (amount == 1) { Require(Same(full, actual), "The full radio endpoint changed."); continue; }
                    Require(Same(Expected(dry, full, amount), actual),
                        "Radio intensity changed the fixed wet character or the shared mixing law.");
                    double target = (1 - (double)amount) * Power(dry) + amount * Power(full);
                    Require(Math.Abs(10 * Math.Log10(Power(actual) / target)) < 1e-6,
                        "The radio did not restore the interpolated endpoint power.");
                }
            });
            check($"{effect} retains fixed wet state while intensity changes at {rate} Hz", () =>
            {
                var actual = Engine(rate); var full = Engine(rate);
                float[] amounts = [.2f, .8f, .001f, .5f, 1, .05f, .95f, 1];
                for (int step = 0; step < amounts.Length; step++)
                {
                    float amount = amounts[step];
                    float[] input = Signal(rate, 257, step * 257), a = (float[])input.Clone(), b = (float[])input.Clone();
                    if (amount > .001f) full.ApplyEffect(b, effect, 1);
                    actual.ApplyEffect(a, effect, amount);
                    float[] expected = amount <= .001f ? input : amount == 1 ? b : Expected(input, b, amount);
                    Require(Same(expected, a), "Intensity changed radio/filter/noise state instead of only mixing.");
                }
            });
            check($"{effect} restores each analysis buffer while retaining wet stream state at {rate} Hz", () =>
            {
                var actual = Engine(rate); var full = Engine(rate);
                float[] source = Signal(rate, rate / 3);
                for (int offset = 0; offset < source.Length; offset += 257)
                {
                    float[] dry = source.AsSpan(offset, Math.Min(257, source.Length - offset)).ToArray();
                    float[] a = (float[])dry.Clone(), b = (float[])dry.Clone();
                    full.ApplyEffect(b, effect, 1);
                    actual.ApplyEffect(a, effect, .5f);
                    Require(Same(Expected(dry, b, .5f), a), "The analysis buffer used incorrect radio state or power.");
                }
            });
            check($"{effect} full processing is independent of partitions at {rate} Hz", () =>
            {
                float[] whole = Signal(rate, rate / 2), split = (float[])whole.Clone();
                Engine(rate).ApplyEffect(whole, effect, 1);
                var engine = Engine(rate);
                for (int offset = 0; offset < split.Length; offset += 257)
                    engine.ApplyEffect(split.AsSpan(offset, Math.Min(257, split.Length - offset)), effect, 1);
                Require(Same(whole, split), "Radio wet state depends on buffer partitions.");
            });
            check($"{effect} reset reproduces mixed PCM at {rate} Hz", () =>
            {
                var engine = Engine(rate);
                float[] source = Signal(rate, 4096), first = (float[])source.Clone(), replay = (float[])source.Clone();
                engine.ApplyEffect(first, effect, .5f);
                engine.Reset(123456);
                engine.ApplyEffect(replay, effect, .5f);
                Require(Same(first, replay), "Reset did not reproduce radio PCM.");
            });
            check($"{effect} warmed mixing allocates no managed bytes at {rate} Hz", () =>
            {
                var engine = Engine(rate);
                float[] source = Signal(rate, 2048), buffer = new float[source.Length];
                foreach (float amount in new[] { .1f, .5f, .9f, 1f })
                {
                    for (int i = 0; i < 16; i++) { source.CopyTo(buffer, 0); engine.ApplyEffect(buffer, effect, amount); }
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 32; i++) { source.CopyTo(buffer, 0); engine.ApplyEffect(buffer, effect, amount); }
                    Require(GC.GetAllocatedBytesForCurrentThread() == before, "Warmed radio processing allocated.");
                }
            });
        }
        foreach (VoiceEffectType effect in Effects)
        {
            check($"{effect} preserves empty, silent and signed-zero bypass buffers", () =>
            {
                var engine = Engine(22050);
                engine.ApplyEffect(Span<float>.Empty, effect, .5f);
                foreach (float amount in new[] { 0f, .001f })
                {
                    float[] input = [-0f, 0f, float.Epsilon, -.25f, .5f], actual = (float[])input.Clone();
                    engine.ApplyEffect(actual, effect, amount);
                    Require(Same(input, actual), "The bypass changed a sample or signed zero.");
                }
                float[] silence = new float[4096];
                engine.ApplyEffect(silence, effect, .5f);
                Require(silence.All(float.IsFinite), "Silence created non-finite radio output.");
            });
            check($"{effect} release into exact silence stays finite through subnormal filter tails", () =>
            {
                const int rate = 22050;
                float[] source = new float[rate * 3];
                Signal(rate, 257).CopyTo(source, 0);
                float[] full = (float[])source.Clone();
                Engine(rate).ApplyEffect(full, effect, 1);
                var engine = Engine(rate);
                for (int offset = 0; offset < source.Length; offset += rate / 2)
                {
                    int length = Math.Min(rate / 2, source.Length - offset);
                    float[] dry = source.AsSpan(offset, length).ToArray();
                    float[] wet = full.AsSpan(offset, length).ToArray();
                    float[] actual = (float[])dry.Clone();
                    engine.ApplyEffect(actual, effect, .05f);
                    Require(actual.All(float.IsFinite) && Same(Expected(dry, wet, .05f), actual),
                        "A silent-tail buffer changed wet state or created invalid normalization.");
                }
            });
            check($"{effect} disabled global effects preserve input PCM", () =>
            {
                var engine = new AudioEffectsEngine(new EffectsSettings { EnableGlobalEffects = false }, 22050);
                float[] input = Signal(22050, 4096), actual = (float[])input.Clone();
                engine.ApplyEffect(actual, effect, .5f);
                Require(Same(input, actual), "Disabled effects changed PCM.");
            });
            check($"{effect} concurrent requests retain independent state", () =>
            {
                float[] input = Signal(22050, 4096), expected = (float[])input.Clone();
                Engine(22050).ApplyEffect(expected, effect, .5f);
                Parallel.For(0, 12, _ =>
                {
                    float[] actual = (float[])input.Clone();
                    Engine(22050).ApplyEffect(actual, effect, .5f);
                    Require(Same(expected, actual), "Concurrent radio requests shared state.");
                });
            });
        }
    }

    private static float[] Expected(float[] dry, float[] full, float amount)
    {
        float[] mixed = new float[dry.Length];
        double sourceEnergy = 0, wetEnergy = 0, mixedEnergy = 0;
        for (int i = 0; i < mixed.Length; i++)
        {
            mixed[i] = (float)((1 - (double)amount) * dry[i] + amount * (double)full[i]);
            sourceEnergy += dry[i] * (double)dry[i];
            wetEnergy += full[i] * (double)full[i];
            mixedEnergy += mixed[i] * (double)mixed[i];
        }
        if (mixedEnergy == 0) return mixed;
        double target = ((1 - (double)amount) * sourceEnergy + amount * wetEnergy) / mixed.Length;
        double gain = Math.Sqrt(target / (mixedEnergy / mixed.Length));
        for (int i = 0; i < mixed.Length; i++) mixed[i] = (float)(mixed[i] * gain);
        return mixed;
    }

    private static AudioEffectsEngine Engine(int rate)
    {
        var engine = new AudioEffectsEngine(new EffectsSettings(), rate);
        engine.Reset(123456);
        return engine;
    }
    private static float[] Signal(int rate, int length, int offset = 0, float scale = 1)
    {
        float[] signal = new float[length];
        for (int i = 0; i < signal.Length; i++)
        {
            double t = (double)(i + offset) / rate;
            signal[i] = scale * (float)((.18 * Math.Sin(2 * Math.PI * 110 * t) + .08 * Math.Sin(2 * Math.PI * 330 * t)
                + .05 * Math.Sin(2 * Math.PI * 1700 * t) + .03 * Math.Sin(2 * Math.PI * Math.Min(3200, rate * .3) * t))
                * (.7 + .3 * Math.Sin(2 * Math.PI * .73 * t)));
        }
        return signal;
    }
    private static double Power(float[] samples)
    {
        double energy = 0;
        foreach (float sample in samples) energy += sample * (double)sample;
        return energy / samples.Length;
    }
    private static bool Same(float[] a, float[] b) => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
