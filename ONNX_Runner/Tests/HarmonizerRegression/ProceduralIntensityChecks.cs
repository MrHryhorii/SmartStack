using System.Reflection;
using System.Runtime.InteropServices;
using ONNX_Runner.Models.Audio;
using ONNX_Runner.Services.Audio.Effects;
using ONNX_Runner.Services.Audio.Levels;
using Signalsmith;

internal static class ProceduralIntensityChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private delegate int Completion(Span<float> samples);

    internal static void Run(Action<string, Action> check)
    {
        foreach (int rate in new[] { 8000, 16000, 22050, 24000, 44100, 48000, 96000, 192000 })
        {
            check($"Voice contributions precede local level correction: {rate} Hz", () =>
            {
                float[] source = Signal(rate, rate / 2 + 17);
                float[][] voices = OfflineVoices(source, rate);
                foreach (float amount in new[] { .00101f, .01f, .25f, .5f, .75f, 1f })
                {
                    float[] expected = Reference(voices, rate, amount);
                    float[] actual = HarmonizerRegression.Render(source, rate, amount, 113, 7);
                    Require(Same(expected, actual), $"Voice weighting or level correction changed at {amount}.");
                }
                float[] lateMix = HarmonizerRegression.ReferenceB(source, rate);
                new StreamingPowerMatchedMixer(rate).Mix(source, lateMix, .5f);
                Require(!Same(lateMix, HarmonizerRegression.Render(source, rate, .5f, 113, 7)),
                    "Intensity still blends the corrected full effect with dry audio.");
            });

            check($"All intensities retain the native latency and exact completion duration: {rate} Hz", () =>
            {
                using var voice = new Stretch(42);
                voice.PresetDefault(1, rate, false);
                int latency = voice.InputLatency() + voice.OutputLatency();
                foreach (float amount in new[] { .00101f, .01f, .5f, 1f })
                {
                    using var engine = new AudioEffectsEngine(new(), rate);
                    var complete = typeof(AudioEffectsEngine).GetMethod("Complete", Hidden)!.CreateDelegate<Completion>(engine);
                    float[] input = Signal(rate, latency - 1);
                    Require(engine.ApplyEffect(input, VoiceEffectType.Harmonizer, amount) == 0, "Priming emitted padding.");
                    Require(engine.ApplyEffect(new[] { .2f }, VoiceEffectType.Harmonizer, amount) == 0, "Latency shortened.");
                    Require(engine.ApplyEffect(new[] { .1f }, VoiceEffectType.Harmonizer, amount) == 1, "Latency increased.");
                    int retained = 0, ready;
                    float[] tail = new float[17];
                    while ((ready = complete(tail)) > 0) retained += ready;
                    Require(retained == latency && complete(tail) == 0, "Completion lost or added samples.");
                }
            });
        }

        foreach (int rate in new[] { 16000, 22050, 48000 })
        {
            check($"Both harmony bands grow progressively relative to the original voice: {rate} Hz", () =>
            {
                float[] source = Enumerable.Range(0, rate * 2)
                    .Select(i => (float)(.2 * Math.Sin(2 * Math.PI * 180 * i / rate))).ToArray();
                double previousThird = 0, previousFifth = 0;
                foreach (float amount in new[] { .01f, .25f, .5f, .75f, 1f })
                {
                    float[] output = HarmonizerRegression.Render(source, rate, amount, 257, 17);
                    ReadOnlySpan<float> stable = output.AsSpan(rate);
                    double fundamental = Band(stable, rate, 180);
                    double third = Band(stable, rate, 180 * Math.Pow(2, 4d / 12)) / fundamental;
                    double fifth = Band(stable, rate, 180 * Math.Pow(2, 7d / 12)) / fundamental;
                    Require(third > previousThird && fifth > previousFifth, "A harmony band did not increase with intensity.");
                    previousThird = third;
                    previousFifth = fifth;
                }
            });

            check($"The procedural signal approaches dry PCM near bypass: {rate} Hz", () =>
            {
                float[] source = Signal(rate, rate);
                float[] actual = HarmonizerRegression.Render(source, rate, .00101f, 257, 17);
                double error = 0, power = 0;
                for (int i = 0; i < source.Length; i++)
                {
                    double difference = actual[i] - (double)source[i];
                    error += difference * difference;
                    power += source[i] * (double)source[i];
                }
                Require(Math.Sqrt(error / power) < .003, "Near-bypass intensity diverged from the original voice.");
            });

            check($"Intensity changes during priming reach the complete retained signal: {rate} Hz", () =>
            {
                float[] source = Signal(rate, 17);
                using var engine = new AudioEffectsEngine(new(), rate);
                Require(engine.ApplyEffect(source.AsSpan(0, 1), VoiceEffectType.Harmonizer, .25f) == 0, "Short input emitted padding.");
                Require(engine.ApplyEffect(source.AsSpan(1), VoiceEffectType.Harmonizer, .75f) == 0, "Short input emitted padding.");
                var complete = typeof(AudioEffectsEngine).GetMethod("Complete", Hidden)!.CreateDelegate<Completion>(engine);
                float[] actual = new float[source.Length];
                int count = complete(actual);
                Require(count == source.Length && Same(Reference(OfflineVoices(source, rate), rate, .75f), actual),
                    "Completion lost the current intensity or dry alignment.");
            });
        }

        check("Silence stays exactly silent at every intensity", () =>
        {
            foreach (float amount in new[] { .00101f, .01f, .25f, .5f, .75f, 1f })
                Require(HarmonizerRegression.Render(new float[22050], 22050, amount, 257, 1).All(x => x == 0f),
                    "The procedural harmonizer added signal to silence.");
        });
    }

    private static float[][] OfflineVoices(float[] source, int rate)
    {
        var voices = new List<float[]> { source };
        foreach (int semitones in new[] { 4, 7 })
        {
            using var shift = new Stretch(42);
            shift.PresetDefault(1, rate, false);
            shift.SetTransposeSemitones(semitones, 1);
            shift.SetFormantSemitones(0, true);
            shift.SetFormantBase(0);
            int pre = shift.InputLatency(), post = shift.OutputLatency();
            float[] buffer = new float[source.Length + pre + post];
            shift.Process(source, buffer.AsSpan(0, source.Length));
            shift.Process(new float[pre], buffer.AsSpan(source.Length, pre));
            shift.Flush(buffer.AsSpan(source.Length + pre, post), 1);
            voices.Add(buffer.AsSpan(pre + post, source.Length).ToArray());
        }
        return voices.ToArray();
    }

    private static float[] Reference(float[][] voices, int rate, float amount)
    {
        double harmony = amount * (1 / Math.Sqrt(3));
        double direct = 1d - amount + harmony;
        float[] output = new float[voices[0].Length];
        for (int voice = 0; voice < voices.Length; voice++)
        for (int i = 0; i < output.Length; i++)
            output[i] += (float)(voices[voice][i] * (voice == 0 ? direct : harmony));
        var (process, _) = HarmonizerRegression.NewLevel(rate, amount);
        for (int i = 0; i < output.Length; i++) output[i] = process(voices[0][i], output[i]);
        return output;
    }

    private static double Band(ReadOnlySpan<float> samples, int rate, double frequency)
    {
        double real = 0, imaginary = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double window = .5 - .5 * Math.Cos(2 * Math.PI * i / (samples.Length - 1));
            double phase = 2 * Math.PI * frequency * i / rate;
            real += samples[i] * window * Math.Cos(phase);
            imaginary += samples[i] * window * Math.Sin(phase);
        }
        return Math.Sqrt(real * real + imaginary * imaginary);
    }

    private static float[] Signal(int rate, int length) => Enumerable.Range(0, length).Select(i =>
        (float)((.15 * Math.Sin(2 * Math.PI * 180 * i / rate) + .05 * Math.Sin(2 * Math.PI * 830 * i / rate)) *
            (i < length / 8 ? 0 : .6 + .4 * Math.Sin(2 * Math.PI * 3 * i / rate)))).ToArray();

    private static bool Same(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
