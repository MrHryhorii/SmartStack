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
        foreach (int rate in new[] { 8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000 })
        {
            check($"Bitcrusher full clock follows half of its actual input rate at {rate} Hz", () =>
            {
                var engine = PreparedEngine(rate, 1f);
                Require(Value<float>(engine, "ClockStep") == .5f && Value<float>(engine, "Scale") == 8f,
                    "Full effect depends on a fixed reference-rate clock or file bit depth.");
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
