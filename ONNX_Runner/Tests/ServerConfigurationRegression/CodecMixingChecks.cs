using System.Runtime.InteropServices;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class CodecMixingChecks
{
    // Midpoints follow frozen multitone levels plus the independently fitted common correction.
    // Full-effect references retain the accepted, unchanged waveform.
    private static readonly (int Rate, VoiceEffectType Effect, double Midpoint, double Full)[] References =
    [
        (8000, VoiceEffectType.G711MuLaw, -18.1536254402, -20.1166058264),
        (8000, VoiceEffectType.G711ALaw, -18.1549339615, -20.1216184393),
        (22050, VoiceEffectType.G711MuLaw, -18.1501359852, -20.2596146032),
        (22050, VoiceEffectType.G711ALaw, -18.1508317287, -20.2613109183),
        (48000, VoiceEffectType.G711MuLaw, -18.1605712274, -20.2951385248),
        (48000, VoiceEffectType.G711ALaw, -18.1605263924, -20.2956794489)
    ];

    // Regular five-percent steps plus the bypass edge and points near the full-effect endpoint.
    private static readonly float[] Amounts = Enumerable.Range(0, 21).Select(i => i / 20f)
        .Concat(new[] { .001f, .00101f, .0052f, .925f, .975f, .99f, .995f, .999f, .9999f })
        .Order().ToArray();

    /// <summary>Checks the approved codec curve, loudness progression, stream state, and hot-loop allocations.</summary>
    internal static void Run(Action<string, Action> check)
    {
        check("Cubic gains preserve and clamp the dry/full endpoints", () =>
        {
            foreach (float amount in new[] { -1f, 0f, 1f, 2f })
            {
                Dsp.CubicGains(amount, out float dry, out float wet);
                bool full = amount >= 1f;
                Require(dry == (full ? 0f : 1f) && wet == (full ? 1f : 0f),
                    "Endpoint gains differ from exact dry/full bypass.");
                Require(Dsp.CubicCrossfade(.25f, -.75f, dry, wet) == (full ? -.75f : .25f),
                    "Endpoint crossfade changed the selected input.");
            }
        });

        check("Cubic halves meet with the approved midpoint value and slope", () =>
        {
            const float h = .005f;
            Dsp.CubicGains(.5f, out float middleDry, out float middleWet);
            Dsp.CubicGains(.5f - h, out float leftDry, out float leftWet);
            Dsp.CubicGains(.5f - 2 * h, out float farLeftDry, out float farLeftWet);
            Dsp.CubicGains(.5f + h, out float rightDry, out float rightWet);
            Dsp.CubicGains(.5f + 2 * h, out float farRightDry, out float farRightWet);
            Dsp.CubicGains(MathF.BitDecrement(.5f), out float adjacentLeftDry, out float adjacentLeftWet);
            Dsp.CubicGains(MathF.BitIncrement(.5f), out float adjacentRightDry, out float adjacentRightWet);
            Require(Math.Abs(middleDry - .6068605903761474) < 1e-7 && middleDry == middleWet,
                "The independently approved midpoint changed.");
            Require(Math.Abs(adjacentLeftDry - adjacentRightDry) < 2e-7 &&
                Math.Abs(adjacentLeftWet - adjacentRightWet) < 2e-7,
                "Adjacent intensities reveal a midpoint discontinuity.");
            double dryLeftSlope = (3.0 * middleDry - 4.0 * leftDry + farLeftDry) / (2 * h);
            double dryRightSlope = (-3.0 * middleDry + 4.0 * rightDry - farRightDry) / (2 * h);
            double wetLeftSlope = (3.0 * middleWet - 4.0 * leftWet + farLeftWet) / (2 * h);
            double wetRightSlope = (-3.0 * middleWet + 4.0 * rightWet - farRightWet) / (2 * h);
            Require(Math.Abs(dryLeftSlope + .6527793124905132) < .001 &&
                Math.Abs(dryRightSlope + .6527793124905132) < .001 &&
                Math.Abs(wetLeftSlope - .7297164037958086) < .001 &&
                Math.Abs(wetRightSlope - .7297164037958086) < .001,
                "One-sided slopes no longer meet at the approved midpoint.");
        });

        check("Cubic gains stay finite and nonnegative throughout the transition", () =>
        {
            for (int i = 0; i <= 10000; i++) VerifyGains(i / 10000f);
            VerifyGains(MathF.BitDecrement(1f));
        });

        foreach (var reference in References)
        {
            int rate = reference.Rate;
            VoiceEffectType effect = reference.Effect;
            check($"{effect} retains approved midpoint and full-effect levels at {rate} Hz", () =>
            {
                float[] input = Signal(rate, rate * 3, 1f);
                double midpoint = Level(Process(input, rate, effect, .5f));
                double full = Level(Process(input, rate, effect, 1f));
                Require(Math.Abs(midpoint - reference.Midpoint) < .01,
                    $"Midpoint differs from the independently recorded prototype: {midpoint:F6} dBFS.");
                Require(Math.Abs(full - reference.Full) < .01,
                    $"Full effect differs from the unchanged reference: {full:F6} dBFS.");
            });

            foreach (float inputScale in new[] { .25f, 1f, 2f })
            foreach (float boostDb in new[] { 0f, 8f })
            {
                check($"{effect} follows its loudness window at {rate} Hz, {inputScale:0.00} input, {boostDb:0} dB gain", () =>
                {
                    float[] dry = Signal(rate, rate * 3, inputScale);
                    float gain = MathF.Pow(10f, boostDb / 20f);
                    double cleanLevel = OutputLevel(Process(dry, rate, effect, 0f), gain, out double cleanFloatLevel);
                    double fullLevel = OutputLevel(Process(dry, rate, effect, 1f), gain, out double fullFloatLevel);
                    double minimum = Math.Min(cleanLevel, fullLevel);
                    double maximum = Math.Max(cleanLevel, fullLevel);
                    double previous = cleanLevel;
                    foreach (float amount in Amounts)
                    {
                        double current = OutputLevel(Process(dry, rate, effect, amount), gain, out double floatLevel);
                        if (amount == .5f)
                            Require(floatLevel >= Math.Min(cleanFloatLevel, fullFloatLevel) - 1e-9 &&
                                floatLevel <= Math.Max(cleanFloatLevel, fullFloatLevel) + 1e-9,
                                "The pre-PCM midpoint no longer passes its original endpoint window.");
                        Require(current >= minimum - 1e-5 && current <= maximum + 1e-5,
                            $"At {amount:R}, {current:F6} is outside clean/full levels {cleanLevel:F6}/{fullLevel:F6} dBFS.");
                        Require(fullLevel <= cleanLevel ? current <= previous + 1e-5 : current >= previous - 1e-5,
                            $"At {amount:R}, loudness reversed direction: {previous:F6} to {current:F6} dBFS.");
                        previous = current;
                    }
                });
            }

            check($"{effect} changing intensity is independent of stream partitions at {rate} Hz", () =>
            {
                float[] whole = Signal(rate, rate * 4, 1f);
                float[] split = (float[])whole.Clone();
                var wholeEngine = NewEngine(rate);
                var splitEngine = NewEngine(rate);
                float[] amounts = [.05f, .45f, .5f, .55f, .95f, 1f, .001f, .25f];
                int segmentLength = rate / 2;
                for (int segment = 0; segment < amounts.Length; segment++)
                {
                    int start = segment * segmentLength;
                    wholeEngine.ApplyEffect(whole.AsSpan(start, segmentLength), effect, amounts[segment]);
                    for (int offset = 0; offset < segmentLength; offset += 257)
                        splitEngine.ApplyEffect(split.AsSpan(start + offset, Math.Min(257, segmentLength - offset)),
                            effect, amounts[segment]);
                }
                Require(MemoryMarshal.AsBytes(whole.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(split.AsSpan())),
                    "Buffer partitions changed the intensity transition.");
            });

            check($"{effect} cubic mixing allocates no managed bytes after warmup at {rate} Hz", () =>
            {
                var engine = NewEngine(rate);
                float[] source = Signal(rate, 2048, 1f);
                float[] buffer = new float[source.Length];
                foreach (float amount in new[] { .1f, .5f, .9f })
                {
                    for (int i = 0; i < 200; i++)
                    {
                        source.CopyTo(buffer, 0);
                        engine.ApplyEffect(buffer, effect, amount);
                    }
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 500; i++)
                    {
                        source.CopyTo(buffer, 0);
                        engine.ApplyEffect(buffer, effect, amount);
                    }
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Require(allocated == 0, $"Warm processing at {amount:R} allocated {allocated} bytes.");
                }
            });
        }
    }

    /// <summary>Checks the curve's bounds, including its intentional small wet-gain overshoot.</summary>
    private static void VerifyGains(float amount)
    {
        Dsp.CubicGains(amount, out float dry, out float wet);
        Require(float.IsFinite(dry) && float.IsFinite(wet) && dry >= 0f && dry <= 1f && wet >= 0f && wet <= 1.07f,
            $"Invalid cubic gains at {amount:R}: {dry:R}/{wet:R}.");
    }

    /// <summary>Processes one fixture with deterministic per-request filter and codec state.</summary>
    private static float[] Process(float[] input, int rate, VoiceEffectType effect, float amount)
    {
        float[] result = (float[])input.Clone();
        NewEngine(rate).ApplyEffect(result, effect, amount);
        return result;
    }

    /// <summary>Creates the actual engine with a fixed seed and default effect settings.</summary>
    private static AudioEffectsEngine NewEngine(int rate)
    {
        var engine = new AudioEffectsEngine(new EffectsSettings(), rate);
        engine.Reset(123456);
        return engine;
    }

    /// <summary>Generates the fixed mixed-band fixture used by the independent prototype study.</summary>
    private static float[] Signal(int rate, int length, float scale)
    {
        float[] signal = new float[length];
        for (int i = 0; i < signal.Length; i++)
            signal[i] = scale * (float)(.18 * Math.Sin(2 * Math.PI * 173 * i / rate)
                + .12 * Math.Sin(2 * Math.PI * 971 * i / rate)
                + .04 * Math.Sin(2 * Math.PI * 2309 * i / rate));
        return signal;
    }

    /// <summary>Measures RMS in dBFS without changing waveform amplitude.</summary>
    private static double Level(float[] samples)
    {
        double power = 0;
        foreach (float sample in samples) power += sample * (double)sample;
        return 10 * Math.Log10(power / samples.Length);
    }

    /// <summary>Measures the mastered PCM16 output with the server's clamp, scale, and truncation convention.</summary>
    private static double OutputLevel(float[] samples, float gain, out double floatLevel)
    {
        VolumeShifter.ApplyVolume(samples, gain);
        floatLevel = Level(samples);
        double power = 0;
        foreach (float sample in samples)
        {
            float decoded = (short)(Math.Clamp(sample, -1f, 1f) * 32767f) / 32768f;
            power += decoded * (double)decoded;
        }
        return 10 * Math.Log10(power / samples.Length);
    }

    /// <summary>Reports a failed fixture or semantic contract.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
