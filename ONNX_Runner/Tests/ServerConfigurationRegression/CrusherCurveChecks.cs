using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class CrusherCurveChecks
{
    /// <summary>Measures real quantized PCM rather than checking a historical fixed playback curve.</summary>
    internal static void Run(Action<string, Action> check)
    {
        foreach (int rate in new[] { 8000, 16000, 22050, 48000, 96000 })
        foreach (float amplitude in new[] { .08f, .2f, .6f })
        {
            check($"Crusher matches clean mean-square power across intensity at {rate} Hz and amplitude {amplitude:R}", () =>
            {
                float[] source = Signal(rate, rate, amplitude);
                double target = PowerMatcher.Measure(source);
                foreach (float amount in new[] { .00101f, .01f, .1f, .25f, .5f, .75f, .95f, 1f })
                {
                    float[] output = (float[])source.Clone();
                    NewEngine(rate).ApplyEffect(output, VoiceEffectType.Bitcrusher, amount);
                    double actual = PowerMatcher.Measure(output);
                    Require(actual > 0 && Math.Abs(10 * Math.Log10(actual / target)) < 1e-5,
                        "Playback no longer matches the measured clean target.");
                }
            });
        }
        foreach (int rate in new[] { 8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000 })
        {
            check($"Crusher preserves digital silence and the four-bit dead zone at {rate} Hz", () =>
            {
                foreach (float amount in new[] { .00101f, .01f, .25f, .5f, .75f, 1f })
                {
                    float[] silence = new float[rate / 4];
                    NewEngine(rate).ApplyEffect(silence, VoiceEffectType.Bitcrusher, amount);
                    Require(silence.All(x => x == 0f), "Matching introduced sound into digital silence.");
                }
                float[] quiet = Signal(rate, rate, .03f);
                NewEngine(rate).ApplyEffect(quiet, VoiceEffectType.Bitcrusher, 1f);
                Require(quiet.All(x => x == 0f), "Matching restored samples removed by the four-bit dead zone.");
            });
            check($"Crusher matches voice-sized streaming fragments at {rate} Hz", () =>
            {
                float[] source = Signal(rate, rate, .2f);
                foreach (int size in new[] { rate / 8, rate / 2 })
                {
                    var engine = NewEngine(rate);
                    for (int offset = 0; offset < source.Length; offset += size)
                    {
                        float[] block = source.AsSpan(offset, Math.Min(size, source.Length - offset)).ToArray();
                        double target = PowerMatcher.Measure(block);
                        engine.ApplyEffect(block, VoiceEffectType.Bitcrusher, .75f);
                        double actual = PowerMatcher.Measure(block);
                        Require(block.All(float.IsFinite), "A short block produced invalid PCM.");
                        if (actual == 0 || block.Length < rate / 100) continue;
                        Require(Math.Abs(10 * Math.Log10(actual / target)) < 1e-5,
                            "A nonzero fragment missed its own power target.");
                    }
                }
            });
        }
        check("Crusher does not amplify DC-filter decay when a quiet fragment is below the quantizer grid", () =>
        {
            const int rate = 22050;
            var engine = NewEngine(rate);
            engine.ApplyEffect(Signal(rate, rate / 2, .2f), VoiceEffectType.Bitcrusher, 1f);
            engine.ApplyEffect(new float[rate / 20], VoiceEffectType.Bitcrusher, 1f);
            float[] quiet = Signal(rate, rate, .003f);
            double dry = PowerMatcher.Measure(quiet);
            engine.ApplyEffect(quiet, VoiceEffectType.Bitcrusher, 1f);
            Require(PowerMatcher.Measure(quiet) < dry * .01,
                "The matcher amplified a filter tail into a replacement for quantized-away voice.");
        });
        check("Crusher parallel request state remains isolated", () =>
        {
            const int rate = 22050;
            float[] source = Signal(rate, rate, .2f), expected = (float[])source.Clone();
            NewEngine(rate).ApplyEffect(expected, VoiceEffectType.Bitcrusher, .75f);
            Parallel.For(0, 16, _ =>
            {
                float[] output = (float[])source.Clone();
                NewEngine(rate).ApplyEffect(output, VoiceEffectType.Bitcrusher, .75f);
                Require(output.AsSpan().SequenceEqual(expected), "Parallel requests shared mutable matching state.");
            });
        });
        foreach (double targetRatio in new[] { .25, 1.44 })
        {
            check($"Power matching with a gain ramp reaches target ratio {targetRatio:R}", () =>
            {
                float[] source = [.1f, .25f, -.2f, .15f, .35f, .4f], output = (float[])source.Clone();
                double measured = PowerMatcher.Measure(source), target = measured * targetRatio;
                const double startGain = .7;
                PowerMatcher.Result result = PowerMatcher.MatchRamped(output, target, measured, startGain, 3, 4);
                Require(result.Matched && Math.Abs(10 * Math.Log10(PowerMatcher.Measure(output) / target)) < 1e-6,
                    "A feasible gain ramp missed its power target.");
                Require(output[0] == (float)(source[0] * startGain), "The ramp changed its starting gain.");
                Require(output[^1] == (float)(source[^1] * result.Gain), "The ramp did not reach its declared final gain.");
            });
        }
        check("Power matching bounds makeup gain and retains floating-point headroom", () =>
        {
            float[] samples = [.1f, .25f, -.2f, .15f, .35f, .4f];
            double measured = PowerMatcher.Measure(samples);
            PowerMatcher.Result result = PowerMatcher.MatchRamped(samples, measured * 100, measured, 1, 3, 4);
            Require(!result.Matched && result.Gain == 4 && samples.Max(Math.Abs) > 1f,
                "Bounded matching hid an unattainable target or clipped floating-point output.");
        });
        check("Power matching reaches independent gain-envelope targets across scales and fragment lengths", () =>
        {
            var random = new Random(123456);
            foreach (int length in new[] { 1, 2, 17, 257, 4099 })
            foreach (int transition in new[] { 1, 3, 110, 5000 })
            foreach (double amplitude in new[] { 1e-12, .001, .3 })
            {
                float[] source = new float[length], expected = new float[length];
                double start = .2 + random.NextDouble() * 3, end = .2 + random.NextDouble() * 3;
                int head = Math.Min(length, transition);
                for (int i = 0; i < length; i++)
                {
                    source[i] = (float)((random.NextDouble() * 2 - 1) * amplitude);
                    double weight = head == 1 || i >= head ? 1 : i / (double)(head - 1);
                    expected[i] = (float)(source[i] * (start + (end - start) * weight));
                }
                double target = PowerMatcher.Measure(expected);
                float[] actual = (float[])source.Clone();
                PowerMatcher.Result result = PowerMatcher.MatchRamped(actual, target, PowerMatcher.Measure(source), start, transition, 4);
                Require(result.Matched && Math.Abs(10 * Math.Log10(PowerMatcher.Measure(actual) / target)) < 1e-6,
                    "An independent gain-envelope target was missed.");
                if (head > 1) Require(actual[0] == expected[0], "The carried gain changed at the first sample.");
            }
        });
        check("Power matching reports an unattainable ramp prefix without breaking its starting gain", () =>
        {
            float[] samples = [1f, 0f, 0f];
            PowerMatcher.Result result = PowerMatcher.MatchRamped(samples, 0, 1d / 3, 2, 3, 4);
            Require(!result.Matched && result.Gain == 0 && samples[0] == 2f,
                "The fixed ramp prefix was changed to claim an impossible target.");
        });
        check("Power matching cannot create energy from a zero stream", () =>
        {
            float[] samples = new float[257];
            PowerMatcher.Result result = PowerMatcher.MatchRamped(samples, .1, 0, 1.3, 5, 4);
            Require(!result.Matched && result.Gain == 1.3 && samples.All(x => x == 0),
                "Zero input changed matching state or gained artificial energy.");
            result = PowerMatcher.MatchRamped(Span<float>.Empty, 0, 0, 1.3, 5, 4);
            Require(result.Matched && result.Gain == 1.3, "An empty fragment changed the last gain.");
        });
        check("Power matching rejects invalid ramp and gain parameters", () =>
        {
            foreach (double maximum in new[] { .5, double.NaN, double.PositiveInfinity })
                MustThrow(() => PowerMatcher.MatchRamped(new float[2], 1, 1, 1, 2, maximum));
            foreach (double start in new[] { -1, double.NaN, 5 })
                MustThrow(() => PowerMatcher.MatchRamped(new float[2], 1, 1, start, 2, 4));
            foreach (int count in new[] { -1, 0 })
                MustThrow(() => PowerMatcher.MatchRamped(new float[2], 1, 1, 1, count, 4));
        });
    }

    private static void MustThrow(Action action)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("Invalid parameters were accepted.");
    }

    private static float[] Signal(int rate, int length, float amplitude)
    {
        var samples = new float[length];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = amplitude * (float)(.7 * Math.Sin(2 * Math.PI * 173 * i / rate)
                + .3 * Math.Sin(2 * Math.PI * 1817 * i / rate));
        return samples;
    }

    private static AudioEffectsEngine NewEngine(int rate)
    {
        var engine = new AudioEffectsEngine(new(), rate);
        engine.Reset(123456);
        return engine;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
