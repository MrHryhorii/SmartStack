using System.Runtime.InteropServices;
using ONNX_Runner.Services;

internal static class StreamingPowerMatchedMixerChecks
{
    internal static void Run(Action<string,Action> check)
    {
        foreach(int rate in new[]{8000,11025,16000,22050,24000,44100,48000,96000,192000})
        {
            check($"Streaming mixer retains exact PCM across arbitrary partitions at {rate} Hz",()=>
            {
                float[] dry=Tone(rate,2,.2f,0),wet=Tone(rate,2,.17f,.7);
                foreach(float t in new[]{0f,.0011f,.25f,.5f,.75f,.99f,1f})
                {
                    float[] whole=(float[])wet.Clone();new StreamingPowerMatchedMixer(rate).Mix(dry,whole,t);
                    foreach(int size in new[]{1,37,257,rate/2})
                    {
                        float[] split=(float[])wet.Clone();var mixer=new StreamingPowerMatchedMixer(rate);
                        for(int i=0;i<split.Length;i+=size)mixer.Mix(dry.AsSpan(i,Math.Min(size,split.Length-i)),split.AsSpan(i,Math.Min(size,split.Length-i)),t);
                        Require(Same(whole,split),"A caller buffer changed the gain clock.");
                    }
                }
            });
            check($"Streaming mixer preserves a settled quadrature sine at {rate} Hz",()=>
            {
                float[] dry=Tone(rate,3,.2f,0),wet=Tone(rate,3,.2f,Math.PI/2);
                new StreamingPowerMatchedMixer(rate).Mix(dry,wet,.5f);
                double target=.02,actual=PowerMatcher.Measure(wet.AsSpan(rate));
                Require(Math.Abs(10*Math.Log10(actual/target))<.002,"Steady interpolation missed endpoint power.");
                double fundamental=0,harmonics=0;
                for(int h=1;h<=9 && 1000*h<rate/2;h++)
                {
                    double a=0,b=0;
                    for(int i=rate;i<rate*3;i++){double angle=2*Math.PI*1000*h*i/rate;a+=wet[i]*Math.Cos(angle);b+=wet[i]*Math.Sin(angle);}
                    double e=a*a+b*b;if(h==1)fundamental=e;else harmonics+=e;
                }
                Require(Math.Sqrt(harmonics/fundamental)<.0001,"Per-sample normalization added harmonics to the sine.");
            });
            check($"Streaming mixer identity and silent reference remain transparent at {rate} Hz",()=>
            {
                float[] dry=Tone(rate,2,.2f,0),actual=(float[])dry.Clone();new StreamingPowerMatchedMixer(rate).Mix(actual,actual,.5f);
                Require(Same(dry,actual),"An identical endpoint changed PCM.");
                actual=Tone(rate,2,.003f,.3);float[] expected=actual.Select(x=>x*.5f).ToArray();
                var mixer=new StreamingPowerMatchedMixer(rate);mixer.Mix(new float[actual.Length],actual,.5f);
                Require(Same(actual,expected) && mixer.Gain==1,"Silent clean audio trained noise makeup gain.");
            });
        }
        check("Streaming mixer endpoint bits, empty input, reset and parameter validation",()=>
        {
            float[] dry=[-0f,.2f,-.4f],wet=[0f,-.5f,.7f],output=(float[])wet.Clone();var m=new StreamingPowerMatchedMixer(22050);
            m.Mix(dry,output,0);Require(Same(dry,output),"Dry endpoint changed bits.");wet.CopyTo(output,0);m.Mix(dry,output,1);Require(Same(wet,output),"Wet endpoint changed bits.");
            m.Mix([],Span<float>.Empty,.5f);Require(!m.HasSamples,"Empty input advanced audio time.");
            foreach(float t in new[]{-1f,2f,float.NaN,float.PositiveInfinity})Throws(()=>m.Prepare(t));
            Throws(()=>m.Mix(new float[1],new float[2],.5f));float[] overlap=new float[5];Throws(()=>m.Mix(overlap.AsSpan(0,3),overlap.AsSpan(1,3),.5f));
            float[] x=Tone(22050,1,.2f,0),y=Tone(22050,1,.2f,Math.PI/2);m.Mix(x,y,.5f);m.Reset();
            Require(m.Gain==1 && !m.HasSamples,"Reset retained gain history.");
        });
        check("Streaming mixer exact cancellation is bounded and does not fabricate clean audio",()=>
        {
            float[] dry=Tone(22050,3,.2f,0),wet=dry.Select(x=>-x).ToArray();var m=new StreamingPowerMatchedMixer(22050);m.Mix(dry,wet,.5f);
            Require(wet.All(x=>x==0f) && m.Gain<=StreamingPowerMatchedMixer.MaximumGain,"Cancellation generated audio or unbounded gain.");
        });
        check("Streaming mixer does not learn inaudible lead-in or add a latency buffer",()=>
        {
            const int rate=22050;float[] dry=Tone(rate,3,.00001f,0),wet=Tone(rate,3,.000009f,2.9);var m=new StreamingPowerMatchedMixer(rate);m.Mix(dry,wet,.5f);
            Require(m.Gain==1,"An inaudible reference established a startup boost.");
            float[] one=[.5f];m.Mix(one,one,.5f);Require(one[0]>.49f,"The mixer delayed the first voice sample.");
        });
        check("Nonrecoverable power tracking advances time without amplifying a filter decay",()=>
        {
            const int rate=22050;var level=new WindowedPowerMatcher(rate,initialGain:2);
            for(int i=0;i<rate;i++){float x=.2f*(float)Math.Sin(2*Math.PI*1000*i/rate);level.Process(x,.00001f,false);}
            Require(level.HasSamples && level.Gain==2,"A nonrecoverable decay changed gain or stopped the analysis clock.");
        });
    }
    private static float[] Tone(int rate,int seconds,float amplitude,double phase)=>Enumerable.Range(0,rate*seconds)
        .Select(i=>amplitude*(float)Math.Sin(2*Math.PI*1000*i/rate+phase)).ToArray();
    private static bool Same(float[] a,float[] b)=>MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
    private static void Throws(Action action){try{action();}catch(ArgumentException){return;}throw new InvalidOperationException("Expected argument validation.");}
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
