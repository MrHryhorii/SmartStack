using System.Runtime.InteropServices;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class CodecMixingChecks
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];
    private static readonly VoiceEffectType[] Effects = [VoiceEffectType.G711MuLaw, VoiceEffectType.G711ALaw];
    private static readonly float[] Amounts = [0, .001f, .00101f, .01f, .1f, .25f, .5f, .65f, .9f, .99f, .9999f, 1];
    private static readonly (int Rate, VoiceEffectType Effect, double Full)[] References =
    [
        (8000, VoiceEffectType.G711MuLaw, -20.1166058264),
        (8000, VoiceEffectType.G711ALaw, -20.1216184393),
        (22050, VoiceEffectType.G711MuLaw, -20.2596146032),
        (22050, VoiceEffectType.G711ALaw, -20.2613109183),
        (48000, VoiceEffectType.G711MuLaw, -20.2951385248),
        (48000, VoiceEffectType.G711ALaw, -20.2956794489)
    ];

    internal static void Run(Action<string, Action> check)
    {
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
                    if (amount <= .001f) { Require(Same(dry, actual), "Codec bypass changed PCM."); continue; }
                    if (amount == 1) { Require(Same(full, actual), "The full codec endpoint changed."); continue; }
                    Require(Same(Expected(dry, full, amount, rate), actual),
                        "Codec intensity changed the fixed wet character or the shared mixing law.");
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
                    float[] expected = amount <= .001f ? input : amount == 1 ? b : Expected(input, b, amount, rate);
                    Require(Same(expected, a), "Intensity changed codec/filter/noise state instead of only mixing.");
                }
            });
            check($"{effect} retains one mixer clock and its wet stream state at {rate} Hz", () =>
            {
                var actual = Engine(rate); var full = Engine(rate);
                var mixer = new StreamingPowerMatchedMixer(rate);
                float[] source = Signal(rate, rate / 3);
                for (int offset = 0; offset < source.Length; offset += 257)
                {
                    float[] dry = source.AsSpan(offset, Math.Min(257, source.Length - offset)).ToArray();
                    float[] a = (float[])dry.Clone(), b = (float[])dry.Clone();
                    full.ApplyEffect(b, effect, 1);
                    actual.ApplyEffect(a, effect, .5f);
                    Require(Same(Expected(dry, b, .5f, rate, mixer), a), "The streaming mixer used incorrect codec state or power.");
                }
            });
            check($"{effect} full processing is independent of partitions at {rate} Hz", () =>
            {
                float[] whole = Signal(rate, rate / 2), split = (float[])whole.Clone();
                Engine(rate).ApplyEffect(whole, effect, 1);
                var engine = Engine(rate);
                for (int offset = 0; offset < split.Length; offset += 257)
                    engine.ApplyEffect(split.AsSpan(offset, Math.Min(257, split.Length - offset)), effect, 1);
                Require(Same(whole, split), "Codec wet state depends on buffer partitions.");
            });
            check($"{effect} reset reproduces mixed PCM at {rate} Hz", () =>
            {
                var engine = Engine(rate);
                float[] source = Signal(rate, 4096), first = (float[])source.Clone(), replay = (float[])source.Clone();
                engine.ApplyEffect(first, effect, .5f);
                engine.Reset(123456);
                engine.ApplyEffect(replay, effect, .5f);
                Require(Same(first, replay), "Reset did not reproduce codec PCM.");
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
                    Require(GC.GetAllocatedBytesForCurrentThread() == before, "Warmed codec processing allocated.");
                }
            });
        }
        foreach (var reference in References)
            check($"{reference.Effect} retains its independent full-output reference at {reference.Rate} Hz", () =>
            {
                float[] samples = Signal(reference.Rate, reference.Rate * 3);
                Engine(reference.Rate).ApplyEffect(samples, reference.Effect, 1);
                Require(Math.Abs(10 * Math.Log10(Power(samples)) - reference.Full) < .01,
                    "The original full codec output level changed.");
            });
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
                Require(silence.All(float.IsFinite), "Silence created non-finite codec output.");
            });
            check($"{effect} release into exact silence stays finite through subnormal filter tails", () =>
            {
                const int rate = 22050;
                float[] source = new float[rate * 3];
                Signal(rate, 257).CopyTo(source, 0);
                float[] full = (float[])source.Clone();
                Engine(rate).ApplyEffect(full, effect, 1);
                var engine = Engine(rate);
                var tailMixer = new StreamingPowerMatchedMixer(rate);
                for (int offset = 0; offset < source.Length; offset += rate / 2)
                {
                    int length = Math.Min(rate / 2, source.Length - offset);
                    float[] dry = source.AsSpan(offset, length).ToArray();
                    float[] wet = full.AsSpan(offset, length).ToArray();
                    float[] actual = (float[])dry.Clone();
                    engine.ApplyEffect(actual, effect, .05f);
                    Require(actual.All(float.IsFinite) && Same(Expected(dry, wet, .05f, rate, tailMixer), actual),
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
                    Require(Same(expected, actual), "Concurrent codec requests shared state.");
                });
            });
        }
    }

    private static float[] Expected(float[] dry, float[] full, float amount, int rate,
        StreamingPowerMatchedMixer? mixer=null)
    {
        float[] output=(float[])full.Clone();
        (mixer??new StreamingPowerMatchedMixer(rate)).Mix(dry,output,amount);
        return output;
    }

    private static AudioEffectsEngine Engine(int rate)
    {
        var engine = new AudioEffectsEngine(new EffectsSettings(), rate);
        engine.Reset(123456);
        return engine;
    }
    private static float[] Signal(int rate, int length, int offset = 0)
    {
        float[] result = new float[length];
        for (int i = 0; i < length; i++)
            result[i] = (float)(.18 * Math.Sin(2 * Math.PI * 173 * (i + offset) / rate)
                + .12 * Math.Sin(2 * Math.PI * 971 * (i + offset) / rate)
                + .04 * Math.Sin(2 * Math.PI * 2309 * (i + offset) / rate));
        return result;
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
