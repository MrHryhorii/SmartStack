using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class FmMixingChecks
{
    private const double StrongEndpointDb = .5;
    private const double SmallReverseDb = .0855;
    private const double StrongReverseDb = .00005;

    /// <summary>Checks the accepted fixed FM branch, fitted gains, speech corpus and streaming behavior.</summary>
    internal static void Run(Action<string, Action> check)
    {
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("FmCurveMoments.json")!;
        using var document=JsonDocument.Parse(stream);
        JsonElement data=document.RootElement;
        double[] parameters=data.GetProperty("Parameters").EnumerateArray().Select(x=>x.GetDouble()).ToArray();

        check("FM gains preserve exact endpoints, guards and selected samples",()=>
        {
            foreach(float amount in new[]{float.NaN,float.NegativeInfinity,-1f,0f,.001f,1f,2f,float.PositiveInfinity})
            {
                Dsp.FmGains(amount,out float dry,out float wet);
                bool full=amount>=1;
                Require(dry==(full?0:1)&&wet==(full?1:0),"Incorrect FM endpoint.");
                Require(Dsp.FmCrossfade(.25f,-.75f,dry,wet)==(full?-.75f:.25f),"Endpoint selection changed.");
            }
        });
        check("FM gains match independent Hermite halves and retain increasing wet share",()=>
        {
            double previous=0;
            for(int i=0;i<=100000;i++)
            {
                float t=i/100000f;
                Dsp.FmGains(t,out float dry,out float wet);
                var expected=Hermite(t,parameters);
                Require(float.IsFinite(dry)&&float.IsFinite(wet)&&dry>=0&&wet>=0,"Invalid FM gain.");
                Require(Math.Abs(dry-expected.Dry)<1e-6&&Math.Abs(wet-expected.Wet)<1e-6,"FM polynomial evaluation changed.");
                double share=wet/(double)(dry+wet);
                Require(share>=previous-3e-7,"FM wet share reversed.");previous=share;
            }
        });
        check("FM midpoint retains the accepted balance and continuous first derivative",()=>
        {
            Dsp.FmGains(.5f,out float dry,out float wet);
            Require(Math.Abs(dry-parameters[0])<1e-7&&Math.Abs(wet-parameters[1])<1e-7,"FM midpoint changed.");
            Require(Math.Abs(wet/(dry+wet)-.5995605673)<1e-6,"Midpoint no longer has the accepted 40/60 balance.");
            const float h=.002f;
            Dsp.FmGains(.5f-h,out float ld1,out float lw1);Dsp.FmGains(.5f-2*h,out float ld2,out float lw2);
            Dsp.FmGains(.5f+h,out float rd1,out float rw1);Dsp.FmGains(.5f+2*h,out float rd2,out float rw2);
            Require(Math.Abs((3*dry-4*ld1+ld2)/(2*h)-parameters[2])<.003&&
                Math.Abs((-3*dry+4*rd1-rd2)/(2*h)-parameters[2])<.003,"Dry midpoint slope changed.");
            Require(Math.Abs((3*wet-4*lw1+lw2)/(2*h)-parameters[3])<.003&&
                Math.Abs((-3*wet+4*rw1-rw2)/(2*h)-parameters[3])<.003,"Wet midpoint slope changed.");
        });

        JsonElement[] recordings=data.GetProperty("Recordings").EnumerateArray().ToArray();
        check("FM corpus retains 181 cases and the additional text split",()=>
        {
            Require(recordings.Length==181,"FM corpus coverage changed.");
            Require(recordings.Count(x=>x.GetProperty("Group").GetString()!.StartsWith("validation",StringComparison.Ordinal))==64,
                "The additional-text split changed.");
        });
        foreach(JsonElement recording in recordings)
        {
            string name=recording.GetProperty("Name").GetString()!;
            foreach(bool weighted in new[]{false,true})
            {
                check($"FM {name} preserves its {(weighted?"weighted":"RMS")} level path",()=>
                {
                    double d=recording.GetProperty(weighted?"DryWeightedPower":"DryPower").GetDouble();
                    double w=recording.GetProperty(weighted?"WetWeightedPower":"WetPower").GetDouble();
                    double c=recording.GetProperty(weighted?"WeightedCross":"Cross").GetDouble();
                    var changes=new double[1001];
                    for(int i=0;i<changes.Length;i++)
                    {
                        Dsp.FmGains(i/1000f,out float dry,out float wet);wet*=.78f;
                        double power=d*dry*dry+w*wet*wet+2*c*dry*wet;
                        changes[i]=10*Math.Log10(power/d);
                    }
                    CheckProgress(changes,200);
                });
            }
            foreach(JsonElement stage in recording.GetProperty("Stages").EnumerateArray())
            {
                int boost=stage.GetProperty("BoostDb").GetInt32();
                foreach(string metric in new[]{"RmsChangesDb","WeightedChangesDb"})
                    check($"FM {name} captured PCM16 {metric} at master +{boost} dB stays within its budget",()=>
                        CheckProgress(stage.GetProperty(metric).EnumerateArray().Select(v=>v.GetDouble()).ToArray(),20));
            }
        }

        foreach(JsonElement fixture in data.GetProperty("PcmFixtures").EnumerateArray())
        {
            string name=fixture.GetProperty("Name").GetString()!;
            foreach(JsonElement point in fixture.GetProperty("Points").EnumerateArray())
                check($"FM {name} matches its actual PCM fixture at {point.GetProperty("Amount").GetSingle():R}",()=>CheckFixture(fixture,point));
            check($"FM {name} mixes the same complete wet branch at every strength",()=>CheckFixedBranch(fixture,parameters));
        }

        foreach(int rate in new[]{8000,22050,48000,96000})
        {
            check($"FM preserves stream partitions and warm bypass at {rate} Hz",()=>
            {
                float[] source=Signal(rate),a=(float[])source.Clone(),b=(float[])source.Clone();
                var whole=Engine(rate,123456);var split=Engine(rate,123456);int part=0;
                for(int start=0;start<a.Length;start+=509)
                {
                    int count=Math.Min(509,a.Length-start);float amount=new[]{.1f,.5f,1f,.25f,0f}[part++%5];
                    whole.ApplyEffect(a.AsSpan(start,count),VoiceEffectType.FmRadio,amount);
                    for(int offset=0;offset<count;offset+=31)
                        split.ApplyEffect(b.AsSpan(start+offset,Math.Min(31,count-offset)),VoiceEffectType.FmRadio,amount);
                }
                Require(BytesEqual(a,b),"FM chunk boundaries changed samples.");
                foreach(float amount in new[]{0f,.001f})
                {
                    float[] bypass=(float[])source.Clone();whole.ApplyEffect(bypass,VoiceEffectType.FmRadio,amount);
                    Require(BytesEqual(source,bypass),"Warm FM bypass changed samples.");
                }
                whole.Reset(123456);split.Reset(123456);
                a=(float[])source.Clone();b=(float[])source.Clone();
                whole.ApplyEffect(a,VoiceEffectType.FmRadio,.5f);split.ApplyEffect(b,VoiceEffectType.FmRadio,.5f);
                Require(BytesEqual(a,b),"FM reset did not reproduce its initial state.");
            });
            check($"FM hot processing allocates no memory at {rate} Hz",()=>
            {
                float[] buffer=Signal(rate);var engine=Engine(rate,123456);
                for(int i=0;i<5;i++)engine.ApplyEffect(buffer,VoiceEffectType.FmRadio,.5f);
                long before=GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<8;i++)engine.ApplyEffect(buffer,VoiceEffectType.FmRadio,.5f);
                Require(GC.GetAllocatedBytesForCurrentThread()==before,"FM allocated in its sample loop.");
            });
        }
    }

    private static void CheckProgress(double[] changes,int window)
    {
        double full=changes[^1],direction=Math.Sign(full),highest=0,reverse=0;
        foreach(double change in changes)
        {
            Require(double.IsFinite(change),"Non-finite level measurement.");
            highest=Math.Max(highest,direction*change);reverse=Math.Max(reverse,highest-direction*change);
        }
        bool strong=Math.Abs(full)>=StrongEndpointDb;
        Require(reverse<=(strong?StrongReverseDb:SmallReverseDb),$"Reverse {reverse:R} dB exceeds the accepted budget.");
        if(!strong)return;
        for(int i=0;i+window<changes.Length;i++)
            Require(direction*(changes[i+window]-changes[i])/Math.Abs(full)>=.05-2e-5,"A 20-percent interval reached a prolonged plateau.");
    }

    private static (double Dry,double Wet) Hermite(float t,double[] p)
    {
        if(t<=.001f)return(1,0);
        if(t>=1)return(0,1);
        bool left=t<.5f;double u=left?2.0*t:2.0*(1-t);
        double h00=2*u*u*u-3*u*u+1,h10=u*u*u-2*u*u+u,h01=-2*u*u*u+3*u*u,h11=u*u*u-u*u;
        double dry=left?h00+h10*.5*p[4]+h01*p[0]+h11*.5*p[2]:-h10*.5*p[6]+h01*p[0]-h11*.5*p[2];
        double wet=left?h10*.5*p[5]+h01*p[1]+h11*.5*p[3]:h00-h10*.5*p[7]+h01*p[1]-h11*.5*p[3];
        double bulge=u*u*(1-u)*(1-u);
        return(dry+p[left?8:10]*bulge,wet+p[left?9:11]*bulge);
    }

    private static float[] Input(JsonElement fixture)
    {
        byte[] bytes=Convert.FromBase64String(fixture.GetProperty("InputBase64").GetString()!);
        Require(Convert.ToHexStringLower(SHA256.HashData(bytes))==fixture.GetProperty("InputSha256").GetString(),"Frozen PCM input changed.");
        return MemoryMarshal.Cast<byte,float>(bytes).ToArray();
    }

    private static float[] Render(JsonElement fixture,float amount)
    {
        float[] input=Input(fixture);int delay=fixture.GetProperty("DelaySamples").GetInt32();
        var output=new float[input.Length+2*delay+1];input.CopyTo(output,0);
        Engine(fixture.GetProperty("Rate").GetInt32(),fixture.GetProperty("Seed").GetUInt32())
            .ApplyEffect(output,VoiceEffectType.FmRadio,amount);
        return output.AsSpan(amount==0?0:delay,input.Length).ToArray();
    }

    private static void CheckFixture(JsonElement fixture,JsonElement point)
    {
        float[] output=Render(fixture,point.GetProperty("Amount").GetSingle());
        int[] indices=fixture.GetProperty("Indices").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        float[] expected=point.GetProperty("FloatSamples").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
        CheckPower(output,point.GetProperty("FloatPower").GetDouble());
        for(int i=0;i<indices.Length;i++)Require(Math.Abs(output[indices[i]]-expected[i])<3e-6,"FM waveform checkpoint changed.");
        foreach(JsonElement stage in point.GetProperty("Stages").EnumerateArray())
        {
            var pcm=(float[])output.Clone();VolumeShifter.ApplyVolume(pcm,MathF.Pow(10,stage.GetProperty("BoostDb").GetSingle()/20));
            for(int i=0;i<pcm.Length;i++)pcm[i]=(short)(Math.Clamp(pcm[i],-1f,1f)*32767f)/32768f;
            CheckPower(pcm,stage.GetProperty("RmsPower").GetDouble());
            float[] samples=stage.GetProperty("Samples").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
            for(int i=0;i<indices.Length;i++)Require(Math.Abs(pcm[indices[i]]-samples[i])<=1.1/32768,"Master/PCM16 checkpoint changed.");
        }
    }

    private static void CheckFixedBranch(JsonElement fixture,double[] parameters)
    {
        float[] input=Input(fixture),full=Render(fixture,1);
        foreach(float amount in new[]{.1f,.25f,.5f,.75f,.9f})
        {
            var gains=Hermite(amount,parameters);float[] output=Render(fixture,amount);
            for(int i=0;i<output.Length;i++)
                Require(Math.Abs(output[i]-(input[i]*gains.Dry+full[i]*gains.Wet))<3e-6,
                    "FM intensity altered the prepared EQ, drive, noise or saturation branch.");
        }
    }

    private static void CheckPower(float[] output,double expected)
    {
        double power=0;foreach(float sample in output)power+=(double)sample*sample;power/=output.Length;
        Require(Math.Abs(power-expected)<1e-8+expected*5e-6,"FM output power changed.");
    }

    private static AudioEffectsEngine Engine(int rate,uint seed)
    {
        var engine=new AudioEffectsEngine(new EffectsSettings(),rate);engine.Reset(seed);return engine;
    }

    private static float[] Signal(int rate)=>Enumerable.Range(0,rate).Select(i=>(float)(.15*Math.Sin(2*Math.PI*137*i/rate)+.08*Math.Sin(2*Math.PI*1823*i/rate))).ToArray();
    private static bool BytesEqual(float[] a,float[] b)=>MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
    private static void Require(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException(message);
    }
}
