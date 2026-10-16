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

    /// <summary>Checks tenth-bit grids, legacy integer depths, bounded PCM, and preparation allocations.</summary>
    internal static void Run(Action<string, Action> check)
    {
        check("Bitcrusher exposes all 121 tenth-bit depths while preserving the 16-to-4-bit range", () =>
        {
            for (int tenth = 40; tenth <= 160; tenth++)
            {
                float bits = tenth / 10f;
                float amount = tenth == 160 ? .00101f : RequestForDepth(bits);
                var engine = PreparedEngine(8000, amount);
                double actualBits = 1 + Math.Log2(Value<float>(engine, "Scale"));
                Require(Math.Abs(actualBits - bits) < 3e-6, $"Missing {bits:F1}-bit quantization grid.");
                Require(Quantize(engine, 0f, 1f) == 0f, "A fractional grid introduced a noise floor.");
            }
        });

        foreach (var point in new (float Amount, float Bits)[]
        {
            (.1f, 13.7f), (.25f, 10.8f), (.5f, 7f), (.75f, 4.8f),
            (.78f, 4.6f), (.79f, 4.5f), (.8f, 4.5f), (.9f, 4.1f), (.94f, 4f), (1f, 4f)
        })
        {
            check($"Bitcrusher prepares {point.Bits:F1} bits at request {point.Amount:R}", () =>
            {
                var engine = PreparedEngine(22050, point.Amount);
                double actualBits = 1 + Math.Log2(Value<float>(engine, "Scale"));
                Require(Math.Abs(actualBits - point.Bits) < 3e-6, "The declared tenth-bit mapping changed.");
            });
        }

        check("Bitcrusher retains the exact original quantizer at every integer depth", () =>
        {
            for (int bits = 4; bits <= 16; bits++)
            {
                var engine = PreparedEngine(8000, bits == 16 ? .00101f : RequestForDepth(bits));
                int scale = 1 << (bits - 1);
                for (int pcm = short.MinValue; pcm <= short.MaxValue; pcm++)
                {
                    float input = pcm / 32768f;
                    float expected = Math.Clamp((int)MathF.Round(input * scale), -scale, scale - 1) / (float)scale;
                    Require(Quantize(engine, input, 1f) == expected,
                        $"The accepted integer grid changed at {bits} bits and PCM16 {pcm}.");
                }
            }
        });

        check("Bitcrusher fractional grids keep uniform code spacing and bounded signed PCM", () =>
        {
            foreach (float bits in new[] { 4.1f, 4.5f, 4.9f, 7.3f, 10.8f, 15.9f })
            {
                var engine = PreparedEngine(8000, RequestForDepth(bits));
                double scale = Math.Pow(2, bits - 1);
                int minCode = (int)Math.Ceiling(-scale), maxCode = (int)Math.Ceiling(scale) - 1;
                foreach (float input in new[] { -2f, -1f, -.8f, -.2f, -.01f, 0f, .01f, .2f, .8f, 1f, 2f })
                {
                    float actual = Quantize(engine, input, 1f);
                    int code = Math.Clamp((int)Math.Round(Math.Clamp(input, -1, 1) * scale), minCode, maxCode);
                    double expected = code / scale;
                    Require(float.IsFinite(actual) && actual >= -1f && actual < 1f,
                        "A fractional grid produced invalid or out-of-range wet PCM.");
                    Require(Math.Abs(actual - expected) < 3e-7, "Output left the declared uniform quantization grid.");
                }
            }
        });

        check("Bitcrusher neighboring tenth-bit grids produce distinct quantization", () =>
        {
            float previous = float.NaN;
            foreach (float bits in new[] { 4f, 4.1f, 4.2f, 4.3f, 4.4f, 4.5f })
            {
                var engine = PreparedEngine(8000, RequestForDepth(bits));
                float current = Quantize(engine, .2f, 1f);
                Require(current != previous, "Adjacent low-bit settings collapsed into one whole-bit step.");
                previous = current;
            }
        });

        check("Bitcrusher decimation keeps moving inside a fixed tenth-bit step", () =>
        {
            var a = PreparedEngine(22050, .94f);
            var b = PreparedEngine(22050, .95f);
            Require(Value<float>(a, "Scale") == 8f && Value<float>(b, "Scale") == 8f,
                "The selected requests no longer share the same four-bit grid.");
            Require(Value<float>(a, "ClockStep") > Value<float>(b, "ClockStep"),
                "Decimation inherited the quantizer's stepped control.");
        });

        foreach (int rate in new[] { 8000, 22050, 48000 })
        {
            check($"Bitcrusher changing tenth-bit preparation allocates no managed bytes at {rate} Hz", () =>
            {
                var engine = PreparedEngine(rate, .5f);
                var buffer = new float[257];
                for (int i = 0; i < 256; i++)
                    engine.ApplyEffect(buffer, VoiceEffectType.Bitcrusher, .01f + i % 99 / 100f);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 256; i++)
                    engine.ApplyEffect(buffer, VoiceEffectType.Bitcrusher, .01f + i % 99 / 100f);
                Require(GC.GetAllocatedBytesForCurrentThread() == before, "Preparing fractional depth allocated managed bytes.");
            });
        }
    }

    /// <summary>Inverts the accepted wet-strength curve to select a declared quantization depth.</summary>
    private static float RequestForDepth(float bits) => 1f - MathF.Sqrt((bits - 4f) / 12f);

    /// <summary>Prepares actual production state without running input filters or advancing its clock.</summary>
    private static AudioEffectsEngine PreparedEngine(int rate, float amount)
    {
        var engine = new AudioEffectsEngine(new(), rate);
        engine.Reset(123456);
        engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.Bitcrusher, amount);
        return engine;
    }

    /// <summary>Reads prepared quantization parameters without adding a production testing API.</summary>
    private static T Value<T>(AudioEffectsEngine engine, string name)
    {
        object state = StateField.GetValue(engine)!;
        return (T)state.GetType().GetField(name)!.GetValue(state)!;
    }

    /// <summary>Reports a violated quantizer contract to the existing console runner.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
