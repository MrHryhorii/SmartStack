using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class FmMixingChecks
{
    private static readonly (int Rate, int Delay)[] Rates =
    [
        (8000, 85), (11025, 112), (16000, 155), (22050, 208), (24000, 225),
        (44100, 400), (48000, 434), (96000, 852), (192000, 1688)
    ];
    private delegate int Completion(Span<float> output);

    internal static void Run(Action<string, Action> check)
    {
        foreach (var point in Rates)
        {
            int rate = point.Rate, delay = point.Delay;
            check($"FM mixes aligned fixed endpoints and restores their measured power at {rate} Hz", () =>
            {
                float[] source = Signal(rate, rate / 5 + delay), full = (float[])source.Clone();
                Engine(rate).ApplyEffect(full, VoiceEffectType.FmRadio, 1);
                float[] dry = Delayed(source, new Queue<float>(Enumerable.Repeat(0f, delay)));
                foreach (float amount in new[] { 0f, .001f, .00101f, .01f, .1f, .25f, .5f, .65f, .9f, .9999f, 1f })
                {
                    float[] actual = (float[])source.Clone();
                    Engine(rate).ApplyEffect(actual, VoiceEffectType.FmRadio, amount);
                    float[] expected = amount <= .001f ? source : amount == 1 ? full : Mix(dry, full, amount);
                    Require(Same(actual, expected), "FM used an unaligned clean signal or changed the fixed wet endpoint.");
                    if (amount <= .001f) continue;
                    double target = (1 - (double)amount) * Power(dry) + amount * Power(full);
                    Require(Math.Abs(10 * Math.Log10(Power(actual) / target)) < 1e-6,
                        "FM did not restore the selected aligned endpoint power.");
                }
            });
            check($"FM keeps full wet processing independent of partitions at {rate} Hz", () =>
            {
                float[] whole = Signal(rate, rate / 5 + 17), split = (float[])whole.Clone();
                Engine(rate).ApplyEffect(whole, VoiceEffectType.FmRadio, 1);
                var engine = Engine(rate);
                for (int i = 0; i < split.Length; i += 31)
                    engine.ApplyEffect(split.AsSpan(i, Math.Min(31, split.Length - i)), VoiceEffectType.FmRadio, 1);
                Require(Same(whole, split), "FM full processing depends on chunk partitions.");
            });
            check($"FM changing intensity preserves fixed wet state and warm bypass at {rate} Hz", () =>
            {
                var actual = Engine(rate); var reference = Engine(rate);
                var clean = new Queue<float>(Enumerable.Repeat(0f, delay));
                float[] amounts = [.1f, .5f, 0f, .001f, .9f, 1f, .25f, 1f];
                for (int step = 0; step < amounts.Length; step++)
                {
                    float amount = amounts[step];
                    float[] source = Signal(rate, 509, step * 509), full = (float[])source.Clone(), output = (float[])source.Clone();
                    float[] dry = source;
                    if (amount > .001f)
                    {
                        reference.ApplyEffect(full, VoiceEffectType.FmRadio, 1);
                        dry = Delayed(source, clean);
                    }
                    actual.ApplyEffect(output, VoiceEffectType.FmRadio, amount);
                    float[] expected = amount <= .001f ? source : amount == 1 ? full : Mix(dry, full, amount);
                    Require(Same(output, expected), "Intensity or bypass changed retained FM state.");
                }
            });
            check($"FM completion restores aligned tail energy and terminates at {rate} Hz", () =>
            {
                foreach (float amount in new[] { .05f, .5f, 1f })
                foreach (int capacity in new[] { 1, 31, rate / 50 })
                {
                    var engine = Engine(rate); var reference = Engine(rate);
                    var clean = new Queue<float>(Enumerable.Repeat(0f, delay));
                    float[] source = Signal(rate, rate / 20 + 3), full = (float[])source.Clone(), output = (float[])source.Clone();
                    source[^1] = full[^1] = output[^1] = .3f;
                    reference.ApplyEffect(full, VoiceEffectType.FmRadio, 1);
                    engine.ApplyEffect(output, VoiceEffectType.FmRadio, amount);
                    Delayed(source, clean);
                    Completion drain = Complete(engine), referenceDrain = Complete(reference);
                    Require(drain(Span<float>.Empty) == 0, "Empty completion consumed FM state.");
                    float[] a = new float[capacity], b = new float[capacity], zeros = new float[capacity];
                    int written = 0, count;
                    while ((count = referenceDrain(b)) > 0)
                    {
                        int actualCount = drain(a);
                        Require(actualCount == count, "Partial FM completion truncated the fixed wet response.");
                        float[] dry = Delayed(zeros.AsSpan(0, count), clean);
                        float[] expected = amount == 1 ? b.AsSpan(0, count).ToArray() : Mix(dry, b.AsSpan(0, count), amount);
                        Require(Same(a.AsSpan(0, count), expected), "FM tail used a different mix or an unaligned reference.");
                        Require(a.AsSpan(0, count).ToArray().All(float.IsFinite), "FM completion created a non-finite sample.");
                        written += count;
                        Require(written < rate / 4, "FM completion did not terminate.");
                    }
                    Require(written >= 2 * delay && drain(a) == 0 && drain(a) == 0,
                        "FM completion lost its retained final response or was not idempotent.");
                }
            });
            check($"FM reset and silence preserve finite reproducible PCM at {rate} Hz", () =>
            {
                var engine = Engine(rate);
                float[] source = Signal(rate, 4096), first = (float[])source.Clone(), replay = (float[])source.Clone();
                engine.ApplyEffect(first, VoiceEffectType.FmRadio, .5f);
                engine.Reset(123456);
                engine.ApplyEffect(replay, VoiceEffectType.FmRadio, .5f);
                Require(Same(first, replay), "FM reset changed PCM.");
                engine.Reset(123456);
                float[] silence = new float[rate / 10];
                engine.ApplyEffect(silence, VoiceEffectType.FmRadio, .5f);
                Require(silence.All(x => x == 0) && Complete(engine)(new float[31]) == 0,
                    "FM silence created noise or a spurious pending tail.");
            });
            check($"FM warmed interpolation and completion allocate no managed bytes at {rate} Hz", () =>
            {
                var engine = Engine(rate); Completion drain = Complete(engine);
                float[] source = Signal(rate, 2048), buffer = new float[source.Length], tail = new float[Math.Max(1, rate / 50)];
                void Process()
                {
                    source.CopyTo(buffer, 0);
                    engine.ApplyEffect(buffer, VoiceEffectType.FmRadio, .5f);
                    while (drain(tail) > 0) { }
                }
                for (int i = 0; i < 8; i++) Process();
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 16; i++) Process();
                Require(GC.GetAllocatedBytesForCurrentThread() == before, "Warmed FM or tail interpolation allocated.");
            });
        }
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FmCurveMoments.json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        foreach (JsonElement fixture in document.RootElement.GetProperty("PcmFixtures").EnumerateArray())
            check($"FM retains the independent full PCM fixture for {fixture.GetProperty("Name").GetString()}", () => CheckFixture(fixture));
        check("FM concurrent requests retain independent filter and mix state", () =>
        {
            float[] source = Signal(22050, 4096), expected = (float[])source.Clone();
            Engine(22050).ApplyEffect(expected, VoiceEffectType.FmRadio, .5f);
            Parallel.For(0, 12, _ =>
            {
                float[] actual = (float[])source.Clone();
                Engine(22050).ApplyEffect(actual, VoiceEffectType.FmRadio, .5f);
                Require(Same(expected, actual), "Concurrent FM requests shared state.");
            });
        });
    }

    private static void CheckFixture(JsonElement fixture)
    {
        byte[] bytes = Convert.FromBase64String(fixture.GetProperty("InputBase64").GetString()!);
        Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == fixture.GetProperty("InputSha256").GetString(), "Frozen FM input changed.");
        float[] input = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        int delay = fixture.GetProperty("DelaySamples").GetInt32();
        float[] output = new float[input.Length + 2 * delay + 1]; input.CopyTo(output, 0);
        Engine(fixture.GetProperty("Rate").GetInt32()).ApplyEffect(output, VoiceEffectType.FmRadio, 1);
        ReadOnlySpan<float> voice = output.AsSpan(delay, input.Length);
        JsonElement point = fixture.GetProperty("Points").EnumerateArray().Single(x => x.GetProperty("Amount").GetSingle() == 1);
        double gain = Math.Pow(10, 1.7 / 20);
        double expectedPower = point.GetProperty("FloatPower").GetDouble() * gain * gain;
        Require(Math.Abs(Power(voice) / expectedPower - 1) < 1e-5, "The full FM fixture level changed.");
        int[] indices = fixture.GetProperty("Indices").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        float[] expected = point.GetProperty("FloatSamples").EnumerateArray().Select(x => x.GetSingle()).ToArray();
        for (int i = 0; i < indices.Length; i++)
            Require(Math.Abs(voice[indices[i]] - expected[i] * gain) < 3e-6, "The full FM fixture waveform changed.");
    }

    private static float[] Mix(ReadOnlySpan<float> dry, ReadOnlySpan<float> full, float amount)
    {
        float[] mixed = new float[dry.Length]; double source = 0, wet = 0, actual = 0;
        for (int i = 0; i < mixed.Length; i++)
        {
            mixed[i] = (float)((1 - (double)amount) * dry[i] + amount * (double)full[i]);
            source += dry[i] * (double)dry[i]; wet += full[i] * (double)full[i]; actual += mixed[i] * (double)mixed[i];
        }
        if (actual == 0) return mixed;
        double targetPower = ((1 - (double)amount) * source + amount * wet) / mixed.Length;
        double gain = Math.Sqrt(targetPower / (actual / mixed.Length));
        for (int i = 0; i < mixed.Length; i++) mixed[i] = (float)(mixed[i] * gain);
        return mixed;
    }
    private static float[] Delayed(ReadOnlySpan<float> source, Queue<float> history)
    {
        float[] result = new float[source.Length];
        for (int i = 0; i < result.Length; i++) { result[i] = history.Dequeue(); history.Enqueue(source[i]); }
        return result;
    }
    private static double Power(ReadOnlySpan<float> data)
    {
        double sum = 0; foreach (float x in data) sum += x * (double)x;
        return data.IsEmpty ? 0 : sum / data.Length;
    }
    private static Completion Complete(AudioEffectsEngine engine) => typeof(AudioEffectsEngine)
        .GetMethod("Complete", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Completion>(engine);
    private static AudioEffectsEngine Engine(int rate)
    {
        var engine = new AudioEffectsEngine(new(), rate); engine.Reset(123456); return engine;
    }
    private static float[] Signal(int rate, int count, int offset = 0) => Enumerable.Range(0, count)
        .Select(i => (float)(.2 * Math.Sin(2 * Math.PI * 173 * (i + offset) / rate)
            + .08 * Math.Sin(2 * Math.PI * 2309 * (i + offset) / rate))).ToArray();
    private static bool Same(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
