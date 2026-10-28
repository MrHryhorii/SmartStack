using System.Runtime.InteropServices;
using System.Reflection;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class CrusherMixingChecks
{
    /// <summary>Protects wet-only routing, gradual onset, measured playback gain and stream state.</summary>
    internal static void Run(Action<string, Action> check)
    {
        check("Crusher bypass and disabled effects do not prepare a quantizer", () =>
        {
            foreach (float amount in new[] { 0f, .001f })
            {
                var engine = NewEngine(22050);
                float[] input = Signal(22050, 4096), output = (float[])input.Clone();
                engine.ApplyEffect(output, VoiceEffectType.Bitcrusher, amount);
                Require(SameBits(input, output) && State<float>(engine, "Scale") == 0f, "Bypass prepared or changed PCM.");
            }
            var disabled = new AudioEffectsEngine(new() { EnableGlobalEffects = false }, 22050);
            float[] source = Signal(22050, 4096), actual = (float[])source.Clone();
            disabled.ApplyEffect(actual, VoiceEffectType.Bitcrusher, 1f);
            Require(SameBits(source, actual) && State<float>(disabled, "Scale") == 0f, "Disabled processing prepared a quantizer.");
        });
        check("Crusher empty preparation does not advance its sample clock", () =>
        {
            var engine = NewEngine(22050);
            engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.Bitcrusher, .5f);
            Require(State<float>(engine, "Phase") == 1f && State<float>(engine, "Scale") == 512f,
                "Empty preparation advanced transport or failed to set depth.");
        });
        check("Crusher matching reaches the clean power of the entire supplied fragment", () =>
        {
            const int rate = 22050;
            float[] source = Signal(rate, rate), output = (float[])source.Clone();
            NewEngine(rate).ApplyEffect(output, VoiceEffectType.Bitcrusher, 1f);
            Require(Math.Abs(Level(output) - Level(source)) < 1e-5, "Full-effect mean-square target was missed.");
        });
        check("Crusher active matching allocates no managed bytes", () =>
        {
            var engine = NewEngine(22050);
            float[] source = Signal(22050, 4096), buffer = new float[source.Length];
            for (int i = 0; i < 128; i++) { source.CopyTo(buffer, 0); engine.ApplyEffect(buffer, VoiceEffectType.Bitcrusher, .75f); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 128; i++) { source.CopyTo(buffer, 0); engine.ApplyEffect(buffer, VoiceEffectType.Bitcrusher, .75f); }
            Require(GC.GetAllocatedBytesForCurrentThread() == before, "Active matching allocated managed memory.");
        });
        foreach (int rate in new[] { 8000, 16000, 22050, 48000, 96000 })
        {
            check($"Bitcrusher retains no dry signal at intermediate quantizer dead zones at {rate} Hz", () =>
            {
                float[] source = Signal(rate, rate / 4, .0001f);
                Require(source.Any(x => x != 0f), "The dead-zone probe was silent.");
                float[] output = (float[])source.Clone();
                NewEngine(rate).ApplyEffect(output, VoiceEffectType.Bitcrusher, .5f);
                Require(output.All(x => x == 0f), "An intermediate effect restored unquantized dry samples.");
            });
            check($"Bitcrusher keeps low voice-band energy near zero intensity at {rate} Hz", () =>
            {
                float[] source = new float[rate];
                for (int i = 0; i < source.Length; i++) source[i] = .2f * MathF.Sin(2 * MathF.PI * 80 * i / rate);
                float[] output = (float[])source.Clone();
                NewEngine(rate).ApplyEffect(output, VoiceEffectType.Bitcrusher, .01f);
                double delta = Level(output.AsSpan(rate / 2)) - Level(source.AsSpan(rate / 2));
                Require(Math.Abs(delta) < .03, "Tiny intensity immediately removed the voice bass.");
            });
            check($"Bitcrusher core state is independent of streaming partitions at {rate} Hz", () =>
            {
                int size = rate / 8;
                float[] amounts = [0f, .001f, .00101f, .025f, .125f, .4995f, .5f, .5005f, .875f, .99f, 1f, .45f];
                float[] whole = Signal(rate, size * amounts.Length), split = (float[])whole.Clone();
                var wholeEngine = NewEngine(rate);
                var splitEngine = NewEngine(rate);
                for (int segment = 0; segment < amounts.Length; segment++)
                {
                    wholeEngine.ApplyEffect(whole.AsSpan(segment * size, size), VoiceEffectType.Bitcrusher, amounts[segment]);
                    for (int offset = 0; offset < size; offset += 257)
                        splitEngine.ApplyEffect(split.AsSpan(segment * size + offset, Math.Min(257, size - offset)),
                            VoiceEffectType.Bitcrusher, amounts[segment]);
                }
                foreach (string name in new[] { "Phase", "Hold", "Scale", "ClockStep", "InverseScale", "MinValue", "MaxValue" })
                    Require(State<object>(wholeEngine, name).Equals(State<object>(splitEngine, name)),
                        "Buffer boundaries changed quantizer state.");
                foreach (float value in split) Require(float.IsFinite(value), "A partition produced invalid PCM.");
            });
            check($"Bitcrusher preserves warm-state bypass and deterministic reset at {rate} Hz", () =>
            {
                var engine = NewEngine(rate);
                float[] input = Signal(rate, rate / 2), warm = (float[])input.Clone();
                engine.ApplyEffect(warm, VoiceEffectType.Bitcrusher, .65f);
                foreach (float amount in new[] { 0f, .001f })
                {
                    float[] output = (float[])input.Clone();
                    engine.ApplyEffect(output, VoiceEffectType.Bitcrusher, amount);
                    Require(SameBits(input, output), "Warm bypass changed the input.");
                }
                engine.Reset(123456);
                float[] reset = (float[])input.Clone();
                engine.ApplyEffect(reset, VoiceEffectType.Bitcrusher, .65f);
                Require(SameBits(warm, reset), "Reset changed the seeded output.");
            });
            check($"Crusher cutoff settings cannot leak into another effect at {rate} Hz", () =>
            {
                float[] warm = Signal(rate, rate / 8);
                var crusher = NewEngine(rate);
                var control = NewEngine(rate);
                crusher.ApplyEffect((float[])warm.Clone(), VoiceEffectType.Bitcrusher, .1f);
                // Ring and Crusher advance the shared thermal generator by the same sample count.
                control.ApplyEffect((float[])warm.Clone(), VoiceEffectType.RingModulator, .1f);
                float[] a = Signal(rate, rate / 4), b = (float[])a.Clone();
                crusher.ApplyEffect(a, VoiceEffectType.Telephone, .5f);
                control.ApplyEffect(b, VoiceEffectType.Telephone, .5f);
                Require(SameBits(a, b), "Crusher's adaptive DC corner leaked into Telephone.");
            });
        }
        check("Bitcrusher full-strength playback matches the clean synthetic level", () =>
        {
            const int rate = 22050;
            var source = new float[rate * 3];
            for (int i = 0; i < source.Length; i++)
            {
                double t = i / (double)rate;
                source[i] = (float)((.18 * Math.Sin(2 * Math.PI * 110 * t) + .08 * Math.Sin(2 * Math.PI * 330 * t)
                    + .05 * Math.Sin(2 * Math.PI * 1700 * t) + .03 * Math.Sin(2 * Math.PI * 3200 * t))
                    * (.7 + .3 * Math.Sin(2 * Math.PI * .73 * t)));
            }
            double expected = Level(source);
            NewEngine(rate).ApplyEffect(source, VoiceEffectType.Bitcrusher, 1f);
            Require(Math.Abs(Level(source) - expected) < 1e-5, "Full-strength playback missed the clean power target.");
        });
    }

    private static T State<T>(AudioEffectsEngine engine, string name)
    {
        object state = typeof(AudioEffectsEngine).GetField("_bc", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        return (T)state.GetType().GetField(name)!.GetValue(state)!;
    }

    /// <summary>Creates a voice-band probe without recording or model dependencies.</summary>
    private static float[] Signal(int rate, int count, float amplitude = .2f)
    {
        var signal = new float[count];
        for (int i = 0; i < count; i++)
            signal[i] = amplitude * (float)(.7 * Math.Sin(2 * Math.PI * 173 * i / rate)
                + .3 * Math.Sin(2 * Math.PI * 1817 * i / rate));
        return signal;
    }

    private static AudioEffectsEngine NewEngine(int rate)
    {
        var engine = new AudioEffectsEngine(new(), rate);
        engine.Reset(123456);
        return engine;
    }

    private static double Level(ReadOnlySpan<float> samples)
    {
        double power = 0;
        foreach (float sample in samples) power += sample * (double)sample;
        return 10 * Math.Log10(power / samples.Length);
    }

    private static bool SameBits(float[] a, float[] b) =>
        MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
