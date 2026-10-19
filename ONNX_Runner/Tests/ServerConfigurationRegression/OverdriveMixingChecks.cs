using System.Runtime.InteropServices;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class OverdriveMixingChecks
{
    /// <summary>Checks prepared gains, the joined transition, exact bypass and streaming state.</summary>
    internal static void Run(Action<string, Action> check)
    {
        check("Overdrive gains preserve exact endpoints and bypass", () =>
        {
            foreach (float amount in new[] { -1f, 0f, .001f, float.NaN, 1f, 2f, float.PositiveInfinity })
            {
                Dsp.DriveGains(amount, out float dry, out float wet);
                bool full = amount >= 1;
                Require(dry == (full ? 0f : 1f) && wet == (full ? 1f : 0f), "Endpoint gain changed.");
                Require(Dsp.DriveCrossfade(.25f, -.75f, dry, wet) == (full ? -.75f : .25f),
                    "Endpoint mixing changed the selected signal.");
            }
        });
        check("Overdrive gains keep finite positive weights and increasing wet share", () =>
        {
            double previous = 0;
            // Dense scalar audit only; waveform measurements retain the 21 primary strengths.
            for (int i = 101; i < 100000; i++)
            {
                float amount = i / 100000f;
                Dsp.DriveGains(amount, out float dry, out float wet);
                Require(float.IsFinite(dry) && float.IsFinite(wet) && dry >= 0 && wet >= 0, "Invalid gain.");
                double share = wet / (double)(dry + wet);
                Require(share >= previous - 5e-7, "The wet contribution moved backwards.");
                // Empirical character floor: level fitting must not make the transition nearly dry.
                Require(share >= .8 * amount - 2e-6, "The level correction suppressed the wet transition.");
                previous = share;
            }
            Dsp.DriveGains(.5f, out float middleDry, out float middleWet);
            double midpointShare = middleWet / (double)(middleDry + middleWet);
            Require(midpointShare is >= .4 and <= .65, "The midpoint lost a meaningful dry/wet blend.");
        });
        check("Overdrive cubic halves share their midpoint values and slopes", () =>
        {
            const float h = .001f;
            Dsp.DriveGains(.5f, out float dry, out float wet);
            Dsp.DriveGains(.5f - h, out float dl, out float wl);
            Dsp.DriveGains(.5f - 2 * h, out float dll, out float wll);
            Dsp.DriveGains(.5f + h, out float dr, out float wr);
            Dsp.DriveGains(.5f + 2 * h, out float drr, out float wrr);
            Require(Math.Abs((3 * dry - 4 * dl + dll) - (-3 * dry + 4 * dr - drr)) / (2 * h) < .002,
                "The dry midpoint slope is discontinuous.");
            Require(Math.Abs((3 * wet - 4 * wl + wll) - (-3 * wet + 4 * wr - wrr)) / (2 * h) < .002,
                "The wet midpoint slope is discontinuous.");
            Dsp.DriveGains(MathF.BitDecrement(.5f), out float beforeDry, out float beforeWet);
            Dsp.DriveGains(MathF.BitIncrement(.5f), out float afterDry, out float afterWet);
            Require(Math.Abs(beforeDry - afterDry) < 5e-7 && Math.Abs(beforeWet - afterWet) < 5e-7,
                "The midpoint join is discontinuous.");
        });
        check("Overdrive gain evaluation and prepared mixing allocate zero managed bytes", () =>
        {
            double sum = 0;
            for (int i = 0; i < 20000; i++) sum += Sample(i);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000; i++) sum += Sample(i);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(allocated == 0 && double.IsFinite(sum), $"Prepared mixing allocated {allocated} bytes.");

            static float Sample(int i)
            {
                Dsp.DriveGains(i % 1001 / 1000f, out float dry, out float wet);
                return Dsp.DriveCrossfade(.2f, -.1f, dry, wet);
            }
        });
        OverdriveCurveChecks.Run(check);
        foreach (var reference in new[]
        {
            (Rate: 8000, Dbfs: -22.805513801686455),
            (Rate: 22050, Dbfs: -22.875719482189687),
            (Rate: 48000, Dbfs: -22.899496225082544)
        })
        {
            check($"Overdrive retains the preceding full-strength output at {reference.Rate} Hz", () =>
            {
                float[] signal = Signal(reference.Rate, reference.Rate * 3, 1f);
                NewEngine(reference.Rate).ApplyEffect(signal, VoiceEffectType.Overdrive, 1);
                Require(Math.Abs(Level(signal) - reference.Dbfs) < .00001, "The complete wet character changed.");
            });
        }
        foreach (int rate in new[] { 8000, 22050, 48000 })
        {
            check($"Overdrive intensity changes are independent of stream partitions at {rate} Hz", () =>
            {
                float[] amounts = [0f, .001f, .00101f, .025f, .125f, .4995f, .5f, .5005f, .875f, .99f, 1f, .45f];
                int size = rate / 8;
                float[] whole = Signal(rate, size * amounts.Length, 1f), split = (float[])whole.Clone();
                var a = NewEngine(rate); var b = NewEngine(rate);
                for (int section = 0; section < amounts.Length; section++)
                {
                    a.ApplyEffect(whole.AsSpan(section * size, size), VoiceEffectType.Overdrive, amounts[section]);
                    for (int i = 0; i < size; i += 257)
                        b.ApplyEffect(split.AsSpan(section * size + i, Math.Min(257, size - i)),
                            VoiceEffectType.Overdrive, amounts[section]);
                }
                Require(MemoryMarshal.AsBytes(whole.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(split.AsSpan())),
                    "Streaming partitions changed the effect output.");
            });
            check($"Overdrive preserves clean bypass after warmup at {rate} Hz", () =>
            {
                var engine = NewEngine(rate); float[] input = Signal(rate, rate / 2, 1f), warm = (float[])input.Clone();
                engine.ApplyEffect(warm, VoiceEffectType.Overdrive, .65f);
                foreach (float amount in new[] { 0f, .001f })
                {
                    float[] output = (float[])input.Clone();
                    engine.ApplyEffect(output, VoiceEffectType.Overdrive, amount);
                    Require(MemoryMarshal.AsBytes(input.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(output.AsSpan())),
                        "Warm-state bypass changed clean samples.");
                }
            });
        }
    }

    /// <summary>Recreates the independent multi-band routing input at its original amplitude.</summary>
    internal static float[] Signal(int rate, int count, float scale)
    {
        var signal = new float[count];
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / rate;
            signal[i] = scale * (float)((.18 * Math.Sin(2 * Math.PI * 110 * t) + .08 * Math.Sin(2 * Math.PI * 330 * t)
                + .05 * Math.Sin(2 * Math.PI * 1700 * t) + .03 * Math.Sin(2 * Math.PI * Math.Min(3200, rate * .3) * t))
                * (.7 + .3 * Math.Sin(2 * Math.PI * .73 * t)));
        }
        return signal;
    }

    /// <summary>Measures complete-buffer RMS without gating or normalization.</summary>
    internal static double Level(float[] signal)
    {
        double sum = 0;
        foreach (float x in signal) sum += x * (double)x;
        return 10 * Math.Log10(sum / signal.Length);
    }

    /// <summary>Creates deterministic request-local state for routing comparisons.</summary>
    internal static AudioEffectsEngine NewEngine(int rate)
    {
        var engine = new AudioEffectsEngine(new(), rate); engine.Reset(123456); return engine;
    }

    /// <summary>Reports a violated signal contract to the console runner.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
