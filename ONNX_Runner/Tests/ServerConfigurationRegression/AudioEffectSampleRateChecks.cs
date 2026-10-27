using System.Reflection;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class AudioEffectSampleRateChecks
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];
    private static readonly VoiceEffectType[] Effects = Enum.GetValues<VoiceEffectType>();

    /// <summary>Checks physical timing, noise calibration, streaming, and allocation budgets across host rates.</summary>
    internal static void Run(Action<string, Action> check)
    {
        foreach (int rate in Rates)
        {
            foreach (VoiceEffectType effect in Effects)
            {
                check($"{effect} preserves bypass, finite PCM, and streaming at {rate} Hz", () =>
                {
                    float[] source = Signal(rate, rate / 4);
                    foreach (float amount in new[] { 0f, .25f, .5f, 1f })
                    {
                        float[] whole = (float[])source.Clone();
                        float[] split = (float[])source.Clone();
                        NewEngine(rate).ApplyEffect(whole, effect, amount);
                        var engine = NewEngine(rate);
                        for (int offset = 0; offset < split.Length; offset += 257)
                            engine.ApplyEffect(split.AsSpan(offset, Math.Min(257, split.Length - offset)), effect, amount);
                        bool matchesEachBuffer = effect == VoiceEffectType.Bitcrusher || amount < 1 && effect is
                            VoiceEffectType.Telephone or VoiceEffectType.G711MuLaw or VoiceEffectType.G711ALaw
                            or VoiceEffectType.FmRadio or VoiceEffectType.TacticalRadio;
                        if (!matchesEachBuffer || amount == 0)
                            Require(whole.AsSpan().SequenceEqual(split), "Full processing or fixed-gain streaming depends on partitions.");
                        else
                        {
                            // Adaptive matching measures each supplied buffer; compare identical analysis partitions.
                            float[] replay = (float[])source.Clone();
                            var repeat = NewEngine(rate);
                            for (int offset = 0; offset < replay.Length; offset += 257)
                                repeat.ApplyEffect(replay.AsSpan(offset, Math.Min(257, replay.Length - offset)), effect, amount);
                            Require(split.AsSpan().SequenceEqual(replay), "Matching analysis partitions changed adaptive PCM.");
                        }
                        Require(split.All(value => float.IsFinite(value) && Math.Abs(value) < 2),
                            "Fragmented moderate input produced invalid or excessive output.");
                        Require(whole.All(value => float.IsFinite(value) && Math.Abs(value) < 2),
                            "Moderate input produced invalid or excessive output.");
                        if (amount == 0 || effect == VoiceEffectType.None)
                            Require(whole.AsSpan().SequenceEqual(source), "Bypass changed PCM.");
                    }
                });
                check($"{effect} hot processing allocates no managed bytes at {rate} Hz", () =>
                {
                    var engine = NewEngine(rate);
                    float[] source = Signal(rate, 257), block = new float[source.Length];
                    // Every block is fresh input; feeding prior output back would measure an artificial feedback loop.
                    for (int i = 0; i < 128; i++)
                    {
                        source.CopyTo(block, 0);
                        engine.ApplyEffect(block, effect, .75f);
                    }
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 128; i++)
                    {
                        source.CopyTo(block, 0);
                        engine.ApplyEffect(block, effect, .75f);
                    }
                    Require(GC.GetAllocatedBytesForCurrentThread() == before, "Sample loop allocated memory.");
                });
            }

            check($"Chorus retains its longest tap near 31 ms at {rate} Hz", () => CheckChorus(rate));
            foreach (VoiceEffectType effect in new[] { VoiceEffectType.Flanger, VoiceEffectType.Chorus })
                check($"{effect} keeps its reference causal delay at {rate} Hz", () => CheckDelayTime(rate, effect));
            check($"CVSD retains its reference-clock state after one second at {rate} Hz", () =>
            {
                var reference = NewEngine(22050);
                var actual = NewEngine(rate);
                reference.ApplyEffect(new float[22050], VoiceEffectType.TacticalRadio, 1);
                actual.ApplyEffect(new float[rate], VoiceEffectType.TacticalRadio, 1);
                object expected = State(reference, "_radio");
                object received = State(actual, "_radio");
                foreach (string name in new[] { "CvsdHistory", "CvsdStepSize", "CvsdAccumulator", "ReconState" })
                    Require(expected.GetType().GetField(name)!.GetValue(expected)!
                        .Equals(received.GetType().GetField(name)!.GetValue(received)),
                        "Host rate changed the codec's state for the same elapsed silence.");
            });
            foreach (float amount in new[] { .25f, .5f, .75f, 1f })
            {
                check($"Cassette noise stays calibrated at {rate} Hz and {amount:0.00} intensity", () =>
                {
                    double expected = NoiseRms(22050, VoiceEffectType.LoFiTape, amount);
                    double actual = NoiseRms(rate, VoiceEffectType.LoFiTape, amount);
                    Require(Math.Abs(20 * Math.Log10(actual / expected)) < .5,
                        "Cassette noise changed by more than 0.5 dB with host rate.");
                });
            }
            foreach (VoiceEffectType effect in new[] { VoiceEffectType.Telephone })
            {
                check($"{effect} reference-clock noise stays consistent at {rate} Hz", () =>
                {
                    double expected = NoiseRms(22050, effect, 1);
                    double actual = NoiseRms(rate, effect, 1);
                    Require(Math.Abs(20 * Math.Log10(actual / expected)) < 1,
                        "Radio/telephone noise changed by more than 1 dB with host rate.");
                });
            }
            if (rate >= 22050)
                check($"Overdrive retains its physical top cut at {rate} Hz", () =>
                {
                    double reference = OverdriveTopRms(22050);
                    double actual = OverdriveTopRms(rate);
                    Require(Math.Abs(20 * Math.Log10(actual / reference)) < .3,
                        "The top-cut gain changed by more than 0.3 dB with host rate.");
                });
        }

        check("Cassette playback trim lowers voice and the reference noise floor together", () =>
        {
            // Independent frozen-prototype replay is about -63.46 dBFS after the agreed
            // enclosure resonance and late output trim; the cassette noise remains audible.
            double dbfs = 20 * Math.Log10(NoiseRms(22050, VoiceEffectType.LoFiTape, 1));
            Require(dbfs > -63.76 && dbfs < -63.16, "Reference cassette noise is outside the agreed range.");
        });
        check("Bitcrusher never requests a clock faster than a low-rate source", () =>
        {
            var engine = NewEngine(8000);
            engine.ApplyEffect(Signal(8000, 8000), VoiceEffectType.Bitcrusher, 1);
            object state = State(engine, "_bc");
            float phase = (float)state.GetType().GetField("Phase")!.GetValue(state)!;
            Require(phase >= 0 && phase < 2, "Decimation phase grew beyond a valid clock interval.");
        });
        check("Effects reject a non-positive host rate", () =>
        {
            foreach (int rate in new[] { 0, -1 })
            {
                bool rejected = false;
                try { NewEngine(rate); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Require(rejected, "Invalid rate reached DSP initialization.");
            }
        });
    }

    /// <summary>Uses an impulse and a maximum-delay LFO phase to detect a wrapped or truncated chorus tap.</summary>
    private static void CheckChorus(int rate)
    {
        var engine = NewEngine(rate);
        engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.Chorus, 1);
        FieldInfo field = typeof(AudioEffectsEngine).GetField("_mod", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object state = field.GetValue(engine)!;
        state.GetType().GetField("ChorusPhase2")!.SetValue(state, MathF.PI / 2);
        field.SetValue(engine, state);
        float[] impulse = new float[rate / 20];
        impulse[0] = .1f;
        engine.ApplyEffect(impulse, VoiceEffectType.Chorus, 1);
        int first = (int)(.030 * rate), last = (int)(.032 * rate);
        Require(impulse.AsSpan(first, last - first).ToArray().Max(Math.Abs) > .01,
            "The second chorus tap disappeared or wrapped to a shorter delay.");
        Require(impulse.AsSpan(1, Math.Max(1, rate / 100 - 1)).ToArray().Max(Math.Abs) < 1e-6,
            "A wrapped chorus buffer produced an early echo.");
    }

    /// <summary>Freezes only the LFOs and measures the first impulse tap's centroid in physical seconds.</summary>
    private static void CheckDelayTime(int rate, VoiceEffectType effect)
    {
        var engine = NewEngine(rate);
        engine.ApplyEffect(Span<float>.Empty, effect, 1);
        FieldInfo field = typeof(AudioEffectsEngine).GetField("_mod", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object state = field.GetValue(engine)!;
        foreach (string name in new[] { "FlangerPhaseInc", "ChorusPhaseInc", "ChorusPhaseInc2" })
            state.GetType().GetField(name)!.SetValue(state, 0f);
        field.SetValue(engine, state);
        float[] impulse = new float[rate / 20];
        impulse[0] = .1f;
        engine.ApplyEffect(impulse, effect, 1);
        double nominal = effect == VoiceEffectType.Chorus ? .015 : .0018;
        double expected = nominal + 1.0 / 22050;
        double sum = 0, moment = 0;
        for (int i = Math.Max(1, (int)((expected - .0005) * rate));
            i < Math.Min(impulse.Length, (int)((expected + .0005) * rate) + 1); i++)
        {
            double weight = Math.Max(0, impulse[i]);
            sum += weight;
            moment += weight * i / rate;
        }
        Require(sum > .01 && Math.Abs(moment / sum - expected) < .000003,
            "The causal tap delay changed by more than 3 microseconds with host rate.");
    }

    /// <summary>Measures the steady output noise on silence, excluding one second of cold-start state.</summary>
    private static double NoiseRms(int rate, VoiceEffectType effect, float amount)
    {
        float[] buffer = new float[rate * 6];
        NewEngine(rate).ApplyEffect(buffer, effect, amount);
        double sum = 0;
        for (int i = rate; i < buffer.Length; i++) sum += (double)buffer[i] * buffer[i];
        return Math.Sqrt(sum / (buffer.Length - rate));
    }

    /// <summary>Uses a small-signal tone at the reference cutoff to isolate filter gain from saturation.</summary>
    private static double OverdriveTopRms(int rate)
    {
        const double frequency = 9922.5;
        float[] signal = Enumerable.Range(0, rate)
            .Select(i => (float)(.001 * Math.Sin(2 * Math.PI * frequency * i / rate))).ToArray();
        NewEngine(rate).ApplyEffect(signal, VoiceEffectType.Overdrive, 1);
        double sum = 0;
        for (int i = rate / 10; i < signal.Length; i++) sum += (double)signal[i] * signal[i];
        return Math.Sqrt(sum / (signal.Length - rate / 10));
    }

    /// <summary>Reads a private DSP state for tests that isolate an internal clock or tap.</summary>
    private static object State(AudioEffectsEngine engine, string name) => typeof(AudioEffectsEngine)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;

    /// <summary>Creates a reproducible effect stream without native synthesis dependencies.</summary>
    private static AudioEffectsEngine NewEngine(int rate)
    {
        var engine = new AudioEffectsEngine(new EffectsSettings(), rate);
        engine.Reset(123456);
        return engine;
    }

    /// <summary>Generates a bounded multitone input with frequencies independent of sample rate.</summary>
    private static float[] Signal(int rate, int length) => Enumerable.Range(0, length)
        .Select(i => (float)(.18 * Math.Sin(2 * Math.PI * 173 * i / rate)
            + .12 * Math.Sin(2 * Math.PI * 971 * i / rate)
            + .04 * Math.Sin(2 * Math.PI * 2309 * i / rate))).ToArray();

    /// <summary>Fails a semantic contract without replacing an unexpected result with a new fixture.</summary>
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
