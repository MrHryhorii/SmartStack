using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class FmMixingChecks
{
    /// <summary>Checks fixed FM output, the fitted two-part mixer and streaming behavior.</summary>
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
        check("FM gains match the independent Hermite halves without a plateau in the processed share",()=>
        {
            double previousShare=0;
            for(int i=0;i<=100000;i++)
            {
                float amount=i/100000f;
                Dsp.FmGains(amount,out float dry,out float wet);
                var expected=Hermite(parameters,amount);
                Require(Math.Abs(dry-expected.Dry)<2e-6 && Math.Abs(wet-expected.Wet)<2e-6,
                    "Prepared FM gains differ from the independent quartic definition.");
                Require(dry>=0 && wet>=0 && dry<=1.000001 && wet<=1.000001,
                    "FM gains exceed their calibrated bounds.");
                double share=wet/(double)(dry+wet);
                if(i>102)
                {
                    double slope=(share-previousShare)*100000;
                    Require(slope>.29 && slope<2.08,"Processed FM share reversed or developed an early plateau.");
                }
                previousShare=share;
            }
        });
        check("FM quartic halves join with a shared value and slope",()=>
        {
            const float step=.0001f;
            Dsp.FmGains(.5f-step,out float leftDry,out float leftWet);
            Dsp.FmGains(.5f,out float dry,out float wet);
            Dsp.FmGains(.5f+step,out float rightDry,out float rightWet);
            Require(Math.Abs(dry-parameters[0])<1e-7 && Math.Abs(wet-parameters[1])<1e-7,
                "FM midpoint differs from its fitted value.");
            Require(Math.Abs((dry-leftDry)-(rightDry-dry))/step<.02 &&
                Math.Abs((wet-leftWet)-(rightWet-wet))/step<.02,"FM midpoint slope is discontinuous.");
        });
        foreach(JsonElement recording in data.GetProperty("Recordings").EnumerateArray())
            check($"FM unchanged mixer retains its baseline calibration for {recording.GetProperty("Name").GetString()}",()=>CheckCurve(recording));

        foreach(JsonElement fixture in data.GetProperty("PcmFixtures").EnumerateArray())
        {
            string name=fixture.GetProperty("Name").GetString()!;
            foreach(JsonElement point in fixture.GetProperty("Points").EnumerateArray().Where(x => x.GetProperty("Amount").GetSingle() is 0 or 1))
                check($"FM {name} matches its actual PCM fixture at {point.GetProperty("Amount").GetSingle():R}",()=>CheckFixture(fixture,point));
            check($"FM {name} mixes the same complete wet branch at every strength",()=>CheckFixedBranch(fixture));
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
        float amount=point.GetProperty("Amount").GetSingle();
        float[] output=Render(fixture,amount);
        int[] indices=fixture.GetProperty("Indices").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        float[] expected=point.GetProperty("FloatSamples").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
        double gain=amount==1?Math.Pow(10,1.7/20):1;
        CheckPower(output,point.GetProperty("FloatPower").GetDouble()*gain*gain);
        for(int i=0;i<indices.Length;i++)
            Require(Math.Abs(output[indices[i]]-expected[i]*gain)<3e-6,"The linear FM endpoint changed beyond its fixed +1.7 dB boost.");
    }

    private static void CheckCurve(JsonElement recording)
    {
        string group=recording.GetProperty("Group").GetString()!;
        foreach(bool weighted in new[]{false,true})
        {
            double clean=recording.GetProperty(weighted?"DryWeightedPower":"DryPower").GetDouble();
            double full=recording.GetProperty(weighted?"WetWeightedPower":"WetPower").GetDouble()/clean;
            double cross=recording.GetProperty(weighted?"WeightedCross":"Cross").GetDouble()/clean;
            double endpoint=10*Math.Log10(full),direction=Math.Sign(endpoint),peak=0;
            bool normal=group is "reference" or "synthesis";
            bool strong=Math.Abs(endpoint)>=.5;
            double budget=normal&&strong?.0001:.161;
            var levels=new double[1001];
            for(int i=0;i<levels.Length;i++)
            {
                Dsp.FmGains(i/1000f,out float dry,out float wet);
                double power=dry*(double)dry+full*wet*(double)wet+2*cross*dry*wet;
                levels[i]=10*Math.Log10(power);
                double directed=direction*levels[i];peak=Math.Max(peak,directed);
                Require(peak-directed<budget+1e-6,"FM curve exceeded its measured reversal budget.");
            }
            if(normal&&strong)
                for(int i=0;i<=800;i++)
                    Require(direction*(levels[i+200]-levels[i])>.06*Math.Abs(endpoint),
                        "FM reached a plateau before the end of its intensity range.");
            if(normal&&recording.GetProperty("Voice").GetString()=="piper_base")
                Require(Math.Abs(levels[500]-.5*endpoint)<.121,"Base-voice midpoint moved too far from half of its level transition.");
        }
    }

    private static (double Dry,double Wet) Hermite(double[] p,float amount)
    {
        if(float.IsNaN(amount)||amount<=.001f)return(1,0);
        if(amount>=1)return(0,1);
        bool left=amount<=.5f;double u=left?2.0*amount:2.0*(1-amount);
        double h00=2*u*u*u-3*u*u+1,h10=u*u*u-2*u*u+u,h01=-2*u*u*u+3*u*u,h11=u*u*u-u*u;
        double bulge=u*u*(1-u)*(1-u);
        return left
            ?(h00+.5*p[4]*h10+p[0]*h01+.5*p[2]*h11+p[8]*bulge,
                .5*p[5]*h10+p[1]*h01+.5*p[3]*h11+p[9]*bulge)
            :(-.5*p[6]*h10+p[0]*h01-.5*p[2]*h11+p[10]*bulge,
                h00-.5*p[7]*h10+p[1]*h01-.5*p[3]*h11+p[11]*bulge);
    }

    private static void CheckFixedBranch(JsonElement fixture)
    {
        float[] input=Input(fixture),full=Render(fixture,1);
        foreach(float amount in new[]{.1f,.25f,.5f,.75f,.9f})
        {
            Dsp.FmGains(amount,out float dryGain,out float wetGain);float[] output=Render(fixture,amount);
            for(int i=0;i<output.Length;i++)
                Require(Math.Abs(output[i]-(input[i]*(double)dryGain+full[i]*(double)wetGain))<3e-6,
                    "FM intensity altered the prepared EQ or channel-filter branch.");
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
