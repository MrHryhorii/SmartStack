using System.Reflection;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class ModulationLevelChecks
{
    internal static void Run(Action<string, Action> check, VoiceEffectType effect)
    {
        foreach(double hz in new[]{100d,250d,1000d})
        foreach(float amount in new[]{.25f,.5f,1f})
        {
            check($"{effect} retains its settled modulation envelope at {hz} Hz and {amount:R}", () =>
            {
                const int rate=22050;
                float[] reference=Enumerable.Range(0,rate*24)
                    .Select(i=>.1f*(float)Math.Sin(2*Math.PI*hz*i/rate)).ToArray();
                float[] raw=(float[])reference.Clone(),matched=(float[])reference.Clone();
                var a=NewEngine(rate);var b=NewEngine(rate);
                a.ApplyEffect(Span<float>.Empty,effect,amount);
                var field=typeof(AudioEffectsEngine).GetField("_mod",BindingFlags.Instance|BindingFlags.NonPublic)!;
                object state=field.GetValue(a)!;
                state.GetType().GetField("Level")!.SetValue(state,
                    new WindowedPowerMatcher(rate,windowMs:60000,warmupMs:60000));
                field.SetValue(a,state);
                a.ApplyEffect(raw,effect,amount);b.ApplyEffect(matched,effect,amount);
                double ratio=EnvelopeIndex(matched,rate)/EnvelopeIndex(raw,rate);
                Require(ratio is > .97 and < 1.03,"Level tracking flattened or exaggerated the intended modulation envelope.");
            });
        }
        check($"{effect} preserves its power correction across one-sample calls", () =>
        {
            const int rate=16000;
            float[] reference=Enumerable.Range(0,rate*2).Select(i=>.15f*(float)Math.Sin(2*Math.PI*223*i/rate)).ToArray();
            float[] whole=(float[])reference.Clone(),split=(float[])reference.Clone();
            NewEngine(rate).ApplyEffect(whole,effect,.5f);var engine=NewEngine(rate);
            for(int i=0;i<split.Length;i++)engine.ApplyEffect(split.AsSpan(i,1),effect,.5f);
            Require(System.Runtime.InteropServices.MemoryMarshal.AsBytes(whole.AsSpan()).SequenceEqual(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(split.AsSpan())),"Buffer size changed the gain clock.");
        });
    }
    private static double EnvelopeIndex(float[] signal,int rate)
    {
        var powers=new List<double>();int size=rate/50;
        for(int start=rate*8;start+size<=signal.Length;start+=size)
            powers.Add(PowerMatcher.Measure(signal.AsSpan(start,size)));
        double mean=powers.Average();return Math.Sqrt(powers.Average(x=>(x-mean)*(x-mean)))/mean;
    }
    private static AudioEffectsEngine NewEngine(int rate)
    {
        var engine=new AudioEffectsEngine(new(),rate);engine.Reset(123456);return engine;
    }
    private static void Require(bool value,string message)
    {
        if(!value)throw new InvalidOperationException(message);
    }
}
