using System.Reflection;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class CassetteVoiceLevelChecks
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];
    private static readonly FieldInfo Tape = typeof(AudioEffectsEngine)
        .GetField("_tape", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static void Run(Action<string, Action> check)
    {
        check("Power gain calculation preserves scalar matching and zero-power behavior", () =>
        {
            foreach (var point in new[] { (4d, 1d, 2d, true), (1d, 4d, .5d, true),
                (0d, 1d, 0d, true), (0d, 0d, 1d, true), (1d, 0d, 1d, false) })
            {
                PowerMatcher.Result result = PowerMatcher.CalculateGain(point.Item1, point.Item2);
                Require(result.Gain == point.Item3 && result.Matched == point.Item4, "Incorrect gain or silent-input status.");
            }
            foreach (double invalid in new[] { -1, double.NaN, double.PositiveInfinity })
            foreach (bool target in new[] { false, true })
            {
                bool rejected = false;
                try { PowerMatcher.CalculateGain(target ? invalid : 1, target ? 1 : invalid); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Require(rejected, "Invalid power reached the gain calculation.");
            }
        });

        foreach (int rate in Rates)
        {
            if (rate is 8000 or 22050 or 48000)
            check($"Cassette correction fades continuously around quiet voice at {rate} Hz", () =>
            {
                float[] background = new float[rate * 2];
                New(rate).ApplyEffect(background, VoiceEffectType.LoFiTape, 1);
                double previous = double.NaN;
                for (int point = 25; point <= 70; point++)
                {
                    double level = point * .00001;
                    float[] source = Enumerable.Range(0, background.Length)
                        .Select(i => (float)(level * Math.Sin(2 * Math.PI * 173 * i / rate))).ToArray();
                    double dryPower = PowerMatcher.Measure(source);
                    New(rate).ApplyEffect(source, VoiceEffectType.LoFiTape, 1);
                    for (int i = 0; i < source.Length; i++) source[i] -= background[i];
                    double gainDb = 10 * Math.Log10(PowerMatcher.Measure(source) / dryPower);
                    if (double.IsFinite(previous))
                        Require(Math.Abs(gainDb - previous) < .08,
                            "A detector threshold introduced an abrupt voice-level correction.");
                    previous = gainDb;
                }
            });

            foreach (float amount in new[] { .01f, .25f, .5f, 1f })
            {
                check($"Cassette voice level and background are partition-independent at {rate} Hz, amount={amount}", () =>
                {
                    float[] source = Signal(rate, 4099), expected = (float[])source.Clone();
                    New(rate).ApplyEffect(expected, VoiceEffectType.LoFiTape, amount);
                    foreach (int size in new[] { 1, 17, 257, 4099 })
                    {
                        var split = New(rate);
                        float[] actual = (float[])source.Clone();
                        for (int offset = 0; offset < actual.Length; offset += size)
                            split.ApplyEffect(actual.AsSpan(offset, Math.Min(size, actual.Length - offset)), VoiceEffectType.LoFiTape, amount);
                        Require(expected.AsSpan().SequenceEqual(actual), "Chunk boundaries changed cassette PCM.");
                    }
                });

                check($"Cassette background is independent of voice energy at {rate} Hz, amount={amount}", () =>
                {
                    const int count = 4099, block = 17;
                    float[] background = new float[count];
                    New(rate).ApplyEffect(background, VoiceEffectType.LoFiTape, amount);
                    foreach (float level in new[] { 1e-6f, .1f, 1f })
                    {
                        var engine = New(rate);
                        float[] source = Signal(rate, count);
                        for (int i = 0; i < source.Length; i++) source[i] *= level;
                        for (int offset = 0; offset < count; offset += block)
                        {
                            int length = Math.Min(block, count - offset);
                            engine.ApplyEffect(source.AsSpan(offset, length), VoiceEffectType.LoFiTape, amount);
                            Require(Field<float>(engine, "NoiseSample") == background[offset + length - 1],
                                "Voice normalization, compression or saturation changed additive hiss.");
                        }
                    }
                });
            }

            check($"Cassette detector preserves quiet PCM and delayed phrase endings at {rate} Hz", () =>
            {
                var voice = New(rate); var noise = New(rate);
                float[] quiet = Signal(rate, rate / 4), background = new float[quiet.Length];
                for (int i = 0; i < quiet.Length; i++) quiet[i] *= .0001f;
                voice.ApplyEffect(quiet, VoiceEffectType.LoFiTape, 1);
                noise.ApplyEffect(background, VoiceEffectType.LoFiTape, 1);
                Require(quiet.Zip(background, (a, b) => Math.Abs(a - b)).Max() > 1e-6,
                    "The detector threshold became an audible signal gate.");

                voice = New(rate); noise = New(rate);
                float[] lead = new float[rate / 10], silentLead = new float[lead.Length];
                lead[^1] = .3f;
                voice.ApplyEffect(lead, VoiceEffectType.LoFiTape, 1);
                noise.ApplyEffect(silentLead, VoiceEffectType.LoFiTape, 1);
                float[] tail = new float[Math.Max(1, rate / 100)], noiseTail = new float[tail.Length];
                voice.ApplyEffect(tail, VoiceEffectType.LoFiTape, 1);
                noise.ApplyEffect(noiseTail, VoiceEffectType.LoFiTape, 1);
                Require(tail.Zip(noiseTail, (a, b) => Math.Abs(a - b)).Max() > .01f,
                    "A silent next chunk truncated delayed voice playback.");
            });

            check($"Cassette level state resets and remains request-local at {rate} Hz", () =>
            {
                float[] source = Signal(rate, 4099), expected = (float[])source.Clone();
                var engine = New(rate);
                engine.ApplyEffect(expected, VoiceEffectType.LoFiTape, 1);
                engine.Reset(123456);
                float[] replay = (float[])source.Clone();
                engine.ApplyEffect(replay, VoiceEffectType.LoFiTape, 1);
                Require(expected.AsSpan().SequenceEqual(replay), "Reset retained level or noise state.");
                Parallel.For(0, 8, _ =>
                {
                    float[] actual = (float[])source.Clone();
                    New(rate).ApplyEffect(actual, VoiceEffectType.LoFiTape, 1);
                    Require(expected.AsSpan().SequenceEqual(actual), "Requests shared cassette detector state.");
                });
            });

            check($"Cassette level correction stays finite and bounded at {rate} Hz", () =>
            {
                var engine = New(rate);
                foreach (float level in new[] { 0f, 1e-8f, .0001f, .1f, 1f, 8f, 0f })
                {
                    float[] signal = Signal(rate, rate / 4);
                    for (int i = 0; i < signal.Length; i++) signal[i] *= level;
                    engine.ApplyEffect(signal, VoiceEffectType.LoFiTape, 1);
                    object normalizer = Field<object>(engine, "Level");
                    float gain = (float)normalizer.GetType().GetProperty("Gain", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(normalizer)!;
                    Require(gain is >= .25f and <= 4f && signal.All(float.IsFinite), "Detector exceeded its correction range.");
                }
            });
        }
    }

    private static T Field<T>(AudioEffectsEngine engine, string name)
    {
        object tape = Tape.GetValue(engine)!;
        return (T)tape.GetType().GetField(name)!.GetValue(tape)!;
    }

    private static AudioEffectsEngine New(int rate)
    {
        var engine = new AudioEffectsEngine(new(), rate);
        engine.Reset(123456);
        return engine;
    }

    private static float[] Signal(int rate, int count) => Enumerable.Range(0, count)
        .Select(i => (float)(.18 * Math.Sin(2 * Math.PI * 173 * i / rate)
            + .06 * Math.Sin(2 * Math.PI * 971 * i / rate))).ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
