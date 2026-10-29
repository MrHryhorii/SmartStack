using System.Runtime.InteropServices;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class TelephoneMixingChecks
{
    internal static void Run(Action<string, Action> check)
    {
        foreach (int rate in new[] { 8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000 })
        {
            check($"Telephone streams the fixed endpoint through the common mixer at {rate} Hz", () =>
            {
                float[] dry = Signal(rate, rate/2), full = (float[])dry.Clone();
                NewEngine(rate).ApplyEffect(full, VoiceEffectType.Telephone, 1);
                foreach (float amount in new[]{0f,.001f,.00101f,.01f,.1f,.25f,.5f,.75f,.99f,1f})
                {
                    float[] actual=(float[])dry.Clone(), expected=(float[])full.Clone();
                    NewEngine(rate).ApplyEffect(actual,VoiceEffectType.Telephone,amount);
                    if(amount<=.001f) dry.CopyTo(expected,0);
                    else new StreamingPowerMatchedMixer(rate).Mix(dry,expected,amount);
                    Require(Same(actual,expected),"Telephone changed its fixed wet endpoint or mixer wiring.");
                }
            });
            check($"Telephone retains full wet state while intensity changes at {rate} Hz", () =>
            {
                float[] input = Signal(rate, rate / 2);
                var actualEngine = NewEngine(rate); var fullEngine = NewEngine(rate);
                float[] amounts = [.25f, .5f, .8f, .001f, 0f, 1f];
                foreach (float amount in amounts)
                {
                    float[] actual = (float[])input.Clone(), full = (float[])input.Clone();
                    if (amount > .001f) fullEngine.ApplyEffect(full, VoiceEffectType.Telephone, 1);
                    actualEngine.ApplyEffect(actual, VoiceEffectType.Telephone, amount);
                    if (amount <= .001f) Require(Same(actual, input), "Warm bypass changed PCM.");
                    if (amount == 1) Require(Same(actual, full), "Intensity changed the full wet filter or noise state.");
                }
            });
            check($"Telephone mixed processing is independent of analysis partitions at {rate} Hz", () =>
            {
                float[] whole=Signal(rate,rate/2),split=(float[])whole.Clone();
                NewEngine(rate).ApplyEffect(whole,VoiceEffectType.Telephone,.5f);var engine=NewEngine(rate);
                for(int i=0;i<split.Length;i+=37)
                    engine.ApplyEffect(split.AsSpan(i,Math.Min(37,split.Length-i)),VoiceEffectType.Telephone,.5f);
                Require(Same(whole,split),"A callback boundary changed mixed Telephone PCM.");
            });
            check($"Telephone full processing remains independent of partitions at {rate} Hz", () =>
            {
                float[] whole = Signal(rate, rate / 2), split = (float[])whole.Clone();
                NewEngine(rate).ApplyEffect(whole, VoiceEffectType.Telephone, 1);
                var engine = NewEngine(rate);
                for (int i = 0; i < split.Length; i += 257)
                    engine.ApplyEffect(split.AsSpan(i, Math.Min(257, split.Length - i)), VoiceEffectType.Telephone, 1);
                Require(Same(whole, split), "Telephone wet state depends on partitions.");
            });
        }
        foreach (var (rate, rms) in new[] { (8000, -23.950129767107828), (22050, -23.378979960549117), (48000, -23.329612906801891) })
            check($"Telephone retains the independently captured full output at {rate} Hz", () =>
            {
                float[] output = Signal(rate, rate * 3);
                NewEngine(rate).ApplyEffect(output, VoiceEffectType.Telephone, 1);
                Require(Math.Abs(10 * Math.Log10(Power(output)) - rms) < .002, "The full Telephone processor changed.");
            });
        check("Telephone warmed analysis and mixing allocate no managed bytes", () =>
        {
            const int rate = 22050;
            var engine = NewEngine(rate);
            float[] source = Signal(rate, 4096), work = new float[source.Length];
            for (int i = 0; i < 16; i++) { source.CopyTo(work, 0); engine.ApplyEffect(work, VoiceEffectType.Telephone, .5f); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 32; i++) { source.CopyTo(work, 0); engine.ApplyEffect(work, VoiceEffectType.Telephone, .5f); }
            Require(GC.GetAllocatedBytesForCurrentThread() == before, "Warmed Telephone processing allocated.");
        });
    }

    private static double Power(ReadOnlySpan<float> source)
    {
        double sum = 0;
        foreach (float sample in source) sum += sample * (double)sample;
        return source.IsEmpty ? 0 : sum / source.Length;
    }

    private static bool Same(float[] a, float[] b) => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));

    private static float[] Signal(int rate, int count)
    {
        var signal = new float[count];
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / rate;
            signal[i] = (float)((.18 * Math.Sin(2 * Math.PI * 110 * t) + .08 * Math.Sin(2 * Math.PI * 330 * t)
                + .05 * Math.Sin(2 * Math.PI * 1700 * t) + .03 * Math.Sin(2 * Math.PI * Math.Min(3200, rate * .3) * t))
                * (.7 + .3 * Math.Sin(2 * Math.PI * .73 * t)));
        }
        return signal;
    }

    private static AudioEffectsEngine NewEngine(int rate)
    {
        var engine = new AudioEffectsEngine(new(), rate); engine.Reset(123456); return engine;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
