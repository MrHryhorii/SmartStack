using System.Reflection;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class BitcrusherQuantizationChecks
{
    private static readonly FieldInfo StateField = typeof(AudioEffectsEngine)
        .GetField("_bc", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly Func<AudioEffectsEngine, float, float, float> Quantize = typeof(AudioEffectsEngine)
        .GetMethod("Bitcrusher", BindingFlags.NonPublic | BindingFlags.Instance)!
        .CreateDelegate<Func<AudioEffectsEngine, float, float, float>>();

    /// <summary>Protects continuous depth, signed quantization, relative clocks and allocation budgets.</summary>
    internal static void Run(Action<string, Action> check)
    {
        foreach (var point in new (float Amount, float Bits)[]
        {
            (.1f, 13.72f), (.25f, 10.75f), (.5f, 7f), (.75f, 4.75f),
            (.8f, 4.48f), (.9f, 4.12f), (.94f, 4.0432f), (.99f, 4.0012f), (1f, 4f)
        })
        {
            check($"Bitcrusher prepares continuous depth {point.Bits:R} at request {point.Amount:R}", () =>
            {
                var engine = PreparedEngine(22050, point.Amount);
                double actualBits = 1 + Math.Log2(Value<float>(engine, "Scale"));
                Require(Math.Abs(actualBits - point.Bits) < 4e-6, "The continuous depth mapping changed.");
            });
        }
        check("Bitcrusher preserves signed four-bit and seven-bit rounding over every PCM16 value", () =>
        {
            foreach (var point in new[] { (Amount: .5f, Scale: 64), (Amount: 1f, Scale: 8) })
            {
                var engine = PreparedQuantizer(point.Amount);
                for (int pcm = short.MinValue; pcm <= short.MaxValue; pcm++)
                {
                    float input = pcm / 32768f;
                    float expected = Math.Clamp((int)MathF.Round(input * point.Scale), -point.Scale, point.Scale - 1)
                        / (float)point.Scale;
                    Require(Quantize(engine, input, 1f) == expected,
                        $"Signed quantization changed at scale {point.Scale} and PCM16 {pcm}.");
                }
            }
        });
        check("Bitcrusher fractional depths remain uniform, bounded and unbiased at digital zero", () =>
        {
            foreach (float bits in new[] { 4.1f, 4.5f, 4.9f, 7.3f, 10.8f, 15.9f })
            {
                var engine = PreparedQuantizer(1f - MathF.Sqrt((bits - 4f) / 12f));
                float scale = Value<float>(engine, "Scale");
                Require(Quantize(engine, 0f, 1f) == 0f, "A fractional grid introduced a noise floor.");
                foreach (float input in new[] { -2f, -1f, -.8f, -.2f, -.01f, 0f, .01f, .2f, .8f, 1f, 2f })
                {
                    float actual = Quantize(engine, input, 1f);
                    Require(float.IsFinite(actual) && actual >= -1f && actual < 1f, "Invalid signed wet PCM.");
                    double code = actual * (double)scale;
                    // Float PCM and the prepared reciprocal each contribute rounding error.
                    Require(Math.Abs(code - Math.Round(code)) <= Math.Max(1e-5, Math.Abs(code) * 2e-7),
                        "Output left the uniform code grid.");
                    if (Math.Abs(input) < .8f)
                        Require(Math.Abs(actual - input) <= .50001 / scale, "Interior quantization error exceeds half a step.");
                }
            }
        });
        check("Bitcrusher depth and clock keep moving near full intensity", () =>
        {
            var a = PreparedEngine(22050, .94f);
            var b = PreparedEngine(22050, .95f);
            Require(Value<float>(a, "Scale") > Value<float>(b, "Scale") && Value<float>(b, "Scale") > 8f,
                "Quantization reached its four-bit endpoint early.");
            Require(Value<float>(a, "ClockStep") > Value<float>(b, "ClockStep"), "Sample holding stopped progressing.");
        });
        foreach (var point in new (int Rate, float Amount, float Fraction)[]
        {
            (22050, .25f, .669833762f), (22050, .5f, .545253866f), (22050, .75f, .505444643f),
            (16000, .5f, .721898748f), (16000, 1f, 11025f / 16000), (8000, 1f, 1f),
            (48000, .5f, .276054729f), (48000, 1f, 11025f / 48000),
            (96000, 1f, 11025f / 96000), (192000, 1f, 11025f / 192000)
        })
        {
            check($"Bitcrusher sample holding reaches its logarithmic update rate at request {point.Amount:R} and {point.Rate} Hz", () =>
            {
                const int count = 4096;
                var engine = PreparedEngine(point.Rate, point.Amount);
                object state = StateField.GetValue(engine)!;
                // A fine grid makes every selected source sample distinct without changing the prepared clock.
                state.GetType().GetField("Scale")!.SetValue(state, 65536f);
                state.GetType().GetField("InverseScale")!.SetValue(state, 1f / 65536f);
                state.GetType().GetField("MinValue")!.SetValue(state, -65536);
                state.GetType().GetField("MaxValue")!.SetValue(state, 65535);
                StateField.SetValue(engine, state);
                int updates = 0;
                float previous = -1f;
                for (int i = 0; i < count; i++)
                {
                    float actual = Quantize(engine, (i + 1f) / (count + 2f), point.Amount);
                    if (actual != previous) updates++;
                    previous = actual;
                }
                Require(Math.Abs(updates - count * point.Fraction) <= 2,
                    "The held waveform did not follow the source-relative update rate.");
            });
        }
        foreach (var point in new (int Rate, float UpdateRate)[]
        {
            (8000, 8000), (11025, 11025), (16000, 11025), (22050, 11025), (24000, 11025),
            (44100, 11025), (48000, 11025), (96000, 11025), (192000, 11025)
        })
        {
            check($"Bitcrusher full clock reaches {point.UpdateRate:R} Hz at input {point.Rate} Hz", () =>
            {
                var engine = PreparedEngine(point.Rate, 1f);
                Require(MathF.Abs(Value<float>(engine, "ClockStep") * point.Rate - point.UpdateRate) < .02f
                    && Value<float>(engine, "ClockStep") <= 1f && Value<float>(engine, "Scale") == 8f,
                    "Full effect missed the source-safe target or changed the four-bit grid.");
            });
        }
        foreach (int rate in new[] { 8000, 22050, 48000 })
        {
            check($"Bitcrusher changing preparation allocates no managed bytes at {rate} Hz", () =>
            {
                var engine = PreparedEngine(rate, .5f);
                var buffer = new float[257];
                for (int i = 0; i < 256; i++) engine.ApplyEffect(buffer, VoiceEffectType.Bitcrusher, .01f + i % 99 / 100f);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 256; i++) engine.ApplyEffect(buffer, VoiceEffectType.Bitcrusher, .01f + i % 99 / 100f);
                Require(GC.GetAllocatedBytesForCurrentThread() == before, "Preparing depth, filters or gain allocated memory.");
            });
        }
    }

    /// <summary>Isolates amplitude quantization from sample holding for the grid unit checks.</summary>
    private static AudioEffectsEngine PreparedQuantizer(float amount)
    {
        var engine = PreparedEngine(22050, amount);
        object state = StateField.GetValue(engine)!;
        state.GetType().GetField("ClockStep")!.SetValue(state, 1f);
        StateField.SetValue(engine, state);
        return engine;
    }

    private static AudioEffectsEngine PreparedEngine(int rate, float amount)
    {
        var engine = new AudioEffectsEngine(new(), rate);
        engine.Reset(123456);
        engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.Bitcrusher, amount);
        return engine;
    }

    private static T Value<T>(AudioEffectsEngine engine, string name)
    {
        object state = StateField.GetValue(engine)!;
        return (T)state.GetType().GetField(name)!.GetValue(state)!;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
