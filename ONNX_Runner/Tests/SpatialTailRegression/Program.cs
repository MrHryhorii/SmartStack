using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Dsp;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class SpatialTailRegressionProgram
{
    private static readonly int[] Rates = [8000,11025,16000,22050,24000,44100,48000,96000,192000];
    private static readonly SpatialEnvironment[] Environments = Enum.GetValues<SpatialEnvironment>().Where(x=>x!=SpatialEnvironment.None).ToArray();
    private static readonly Type Pipeline = typeof(SpatialEffectsEngine).Assembly.GetType("ONNX_Runner.Services.Synthesis.AudioGenerationPipeline")!;
    private static readonly MethodInfo Drain = Pipeline.GetMethod("DrainSpatialTail",BindingFlags.NonPublic|BindingFlags.Static)!;
    private static readonly MethodInfo FlushLimiter = Pipeline.GetMethod("FlushOutputVolume",BindingFlags.NonPublic|BindingFlags.Static)!;
    private static readonly Type ResamplerType = Pipeline.Assembly.GetType("ONNX_Runner.Services.Synthesis.AudioOutputResampler")!;
    private static readonly OutputWrite WriteOutput = BindWriter();
    private delegate void OutputWrite(Span<float> samples, AudioStreamManager manager, object? resampler, BiQuadFilter? filter, VolumeShifter volume);
    private static readonly List<object> Evidence = [];
    private static int _checks, _failures;

    private static int Main(string[] args)
    {
        using Stream fixtureStream=typeof(SpatialTailRegressionProgram).Assembly.GetManifestResourceStream("SpatialTailFixtures.json")!;
        Corpus corpus=JsonSerializer.Deserialize<Corpus>(fixtureStream)!;
        foreach(Reference reference in corpus.References)
        {
                Check($"Spatial samples retain the reference {reference.Environment} response at {reference.Rate} Hz, intensity {reference.Amount}",()=>
            {
                float[] samples=new float[reference.Rate*5]; Signal(reference.Rate,reference.Rate/10).CopyTo(samples,0);
                var engine=new SpatialEffectsEngine(reference.Rate);
                var environment=Enum.Parse<SpatialEnvironment>(reference.Environment);
                // Align the LFO clock when the native fixture required silent parameter warmup.
                if(reference.WarmupFrames>0) engine.ApplyEnvironment(new float[reference.WarmupFrames],environment,reference.Amount);
                engine.ApplyEnvironment(samples,environment,reference.Amount);
                Require(Math.Abs(Energy(samples)-reference.Energy)<Math.Max(1e-10,reference.Energy*2e-5),"The environment energy changed.");
                Require(reference.Indices.Length>=32 && reference.Indices.Length==reference.Samples.Length,"Incomplete PCM reference.");
                for(int i=0;i<reference.Indices.Length;i++)
                    Require(Math.Abs(samples[reference.Indices[i]]-reference.Samples[i])<3e-6,"The environment waveform changed.");
            });
        }
        foreach(int rate in Rates)
        foreach(SpatialEnvironment environment in Environments)
        foreach(float amount in new[]{.25f,.5f,1f})
        {
            Check($"Chunk and sentence pauses keep {environment} state at {rate} Hz, intensity {amount}",()=>
            {
                float[] a=Signal(rate,rate/10), b=Signal(rate,rate/8); int pause=(int)(rate*.3f);
                float[] source=a.Concat(new float[pause]).Concat(b).Concat(new float[pause+rate]).ToArray();
                float[] whole=(float[])source.Clone(), split=(float[])source.Clone();
                new SpatialEffectsEngine(rate).ApplyEnvironment(whole,environment,amount);
                var streaming=new SpatialEffectsEngine(rate); int offset=0;
                foreach(int length in new[]{a.Length,pause,b.Length,pause,rate})
                {
                    for(int i=0;i<length;i+=137) streaming.ApplyEnvironment(split.AsSpan(offset+i,Math.Min(137,length-i)),environment,amount);
                    offset+=length;
                }
                Require(MemoryMarshal.AsBytes(whole.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(split.AsSpan())),"A chunk or pause boundary changed PCM.");
            });
        }
        foreach(int rate in Rates)
        foreach(float pause in new[]{0f,.15f,.3f})
        foreach(float amount in new[]{.25f,1f})
        {
            Check($"Forest drains delayed echoes at {rate} Hz, pause {pause}, intensity {amount}",()=>VerifyTail(rate,SpatialEnvironment.Forest,amount,pause));
        }
        foreach(int rate in new[]{8000,22050,48000})
        foreach(SpatialEnvironment environment in Environments.Where(x=>x!=SpatialEnvironment.Forest))
        foreach(float pause in new[]{0f,.3f})
        foreach(float amount in new[]{.25f,1f})
        {
            Check($"The actual drain retains {environment} tail at {rate} Hz, pause {pause}, intensity {amount}",()=>VerifyTail(rate,environment,amount,pause));
        }
        foreach(int rate in new[]{8000,22050,48000})
        foreach(SpatialEnvironment environment in new[]{SpatialEnvironment.Forest,SpatialEnvironment.ConcreteHall,SpatialEnvironment.Cave,SpatialEnvironment.InnerVoice})
        foreach(float volume in new[]{.1f,2.5118864f})
        foreach(bool profiles in new[]{false,true})
        {
            Check($"Tail detection observes final {environment} output at {rate} Hz, volume {volume}, profile {profiles}",()=>
                VerifyTail(rate,environment,.25f,.3f,volume,profiles));
        }
        foreach(int rate in Rates)
        {
            Check($"Tail safety cap is exactly four seconds at {rate} Hz",()=>
            {
                TailOutput result=Capture(rate,SpatialEnvironment.Muffled,.25f,0,floor:0);
                Require(result.GeneratedFrames==rate*4,"The final probe exceeded or shortened the cap.");
            });
            Check($"A quiet non-echoing Muffled path retains its short termination at {rate} Hz",()=>
            {
                var engine=new SpatialEffectsEngine(rate); engine.ApplyEnvironment(new float[1],SpatialEnvironment.Muffled,.25f);
                using var output=new MemoryStream(); using var manager=new AudioStreamManager(AudioFormat.Pcm,rate,output);
                var volume = new VolumeShifter(rate, 1);
                Invoke(Drain,[engine,SpatialEnvironment.Muffled,.25f,rate,.005f,null,volume,1f,manager,null,null,CancellationToken.None]);
                Invoke(FlushLimiter,[volume,manager,CancellationToken.None]);
                Require(manager.SamplesWritten==Math.Max(1,rate/50),"A non-echoing quiet path gained a long hold.");
            });
        }
        Check("Tail draining checks cancellation before producing more output",()=>
        {
            using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
            var engine=new SpatialEffectsEngine(22050); engine.ApplyEnvironment(new float[1],SpatialEnvironment.Forest,.25f);
            using var output=new MemoryStream(); using var manager=new AudioStreamManager(AudioFormat.Pcm,22050,output);
            bool stopped=false;
            try {Invoke(Drain,[engine,SpatialEnvironment.Forest,.25f,22050,.005f,null,new VolumeShifter(22050,1),1f,manager,null,null,cancellation.Token]);}
            catch(OperationCanceledException){stopped=true;}
            Require(stopped && manager.SamplesWritten==0,"Canceled tail output was emitted.");
        });
        Check("Tail draining checks cancellation between probe blocks",()=>
        {
            using var cancellation=new CancellationTokenSource(); using var output=new CancelingStream(cancellation);
            using var manager=new AudioStreamManager(AudioFormat.Pcm,22050,output);
            var engine=new SpatialEffectsEngine(22050); engine.ApplyEnvironment(Burst(22050),SpatialEnvironment.Forest,.25f);
            bool stopped=false;
            try{Invoke(Drain,[engine,SpatialEnvironment.Forest,.25f,22050,.005f,null,new VolumeShifter(22050,1),1f,manager,null,null,cancellation.Token]);}
            catch(OperationCanceledException){stopped=true;}
            Require(stopped && manager.SamplesWritten==441-new VolumeShifter(22050,1).LatencySamples,"The drain continued after cancellation.");
        });
        Check("Parallel tail drains retain independent request state",()=>
        {
            byte[] expected=Capture(22050,SpatialEnvironment.Forest,.25f,.3f).Bytes; bool[] passed=new bool[12];
            Parallel.For(0,passed.Length,i=>passed[i]=expected.AsSpan().SequenceEqual(Capture(22050,SpatialEnvironment.Forest,.25f,.3f).Bytes));
            Require(passed.All(x=>x),"Shared spatial tail state.");
        });
        foreach(int targetRate in new[]{8000,16000,48000})
        {
            Check($"The actual tail output path resamples continuously to {targetRate} Hz",()=>
            {
                TailOutput native=Capture(22050,SpatialEnvironment.Forest,.25f,.3f,writePrefix:false);
                TailOutput converted=Capture(22050,SpatialEnvironment.Forest,.25f,.3f,writePrefix:false,targetRate:targetRate);
                float[] samples=Decode(native.Bytes);
                var resampler=new AudioResampler(22050,targetRate); var expected=resampler.Resample(samples,samples.Length);
                try
                {
                    float[] actual=Decode(converted.Bytes);
                    Require(Math.Abs(expected.Length-actual.Length)<=1,"Tail conversion changed logical duration.");
                    double error=0; int count=Math.Min(expected.Length,actual.Length);
                    for(int i=0;i<count;i++)error+=Math.Pow(expected.Buffer[i]-actual[i],2);
                    Require(Math.Sqrt(error/count)<3.0/32767,"Tail conversion changed the waveform.");
                }
                finally{System.Buffers.ArrayPool<float>.Shared.Return(expected.Buffer);}
            });
        }
        Check("The spatial tail survives fragmented prefix input and final volume flush",()=>
        {
            TailOutput plain=Capture(22050,SpatialEnvironment.Forest,.25f,.3f,volume:.5f);
            TailOutput limited=Capture(22050,SpatialEnvironment.Forest,.25f,.3f,volume:.5f,prefixBlock:17);
            Require(plain.Bytes.AsSpan().SequenceEqual(limited.Bytes),"Limiter priming or flush lost spatial tail samples.");
        });
        Console.WriteLine($"Result: {_checks-_failures}/{_checks} spatial tail checks passed");
        if(args.Length==2 && args[0]=="--report") File.WriteAllText(args[1],JsonSerializer.Serialize(new{Passed=_checks-_failures,Failed=_failures,Evidence},new JsonSerializerOptions{WriteIndented=true}));
        return _failures==0?0:1;
    }

    private static void VerifyTail(int rate,SpatialEnvironment environment,float amount,float pause,float volume=1,bool profiles=false)
    {
        TailOutput actual=Capture(rate,environment,amount,pause,volume,profiles);
        int prefix=Burst(rate).Length+(int)(rate*pause);
        var prepared=new SpatialEffectsEngine(rate); prepared.ApplyEnvironment(Span<float>.Empty,environment,amount);
        int maximumTail=(int)typeof(SpatialEffectsEngine).GetProperty("MaximumTailSamples",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(prepared)!;
        float[] reference=new float[prefix+maximumTail+rate*2]; Burst(rate).CopyTo(reference,0);
        new SpatialEffectsEngine(rate).ApplyEnvironment(reference,environment,amount);
        if(profiles) AudioEqualizer.Create(["telephony-class-application"],rate)!.Apply(reference);
        // Include the final cloning-cleanup filter in the same order as WriteOutput.
        if(profiles)
        {
            BiQuadFilter filter=BiQuadFilter.LowPassFilter(rate,Math.Min(1800f,rate*.45f),.707f);
            for(int i=0;i<reference.Length;i++)reference[i]=filter.Transform(reference[i]);
        }
        int frames=actual.Bytes.Length/2;
        Require(actual.GeneratedFrames>0 && actual.GeneratedFrames<=maximumTail,"Invalid tail length.");
        float[] retained=reference.AsSpan(0,frames).ToArray();
        VolumeShifter.ApplyVolume(retained,volume,rate);
        byte[] expected=Encode(retained,rate);
        Require(expected.AsSpan().SequenceEqual(actual.Bytes),"Draining or the output chain changed retained PCM.");
        float omitted=Peak(reference.AsSpan(frames))*Math.Abs(volume);
        int lastAbove=frames;
        for(int i=frames;i<reference.Length;i++) if(Math.Abs(reference[i]*volume)>=.005f)lastAbove=i;
        Evidence.Add(new{Rate=rate,Environment=environment.ToString(),Amount=amount,PauseSeconds=pause,Volume=volume,Profiles=profiles,
            GeneratedMilliseconds=actual.GeneratedFrames*1000.0/rate,OmittedPeak=omitted,LastLaterAudibleMilliseconds=(lastAbove-frames)*1000.0/rate});
        Require(omitted<.00501f,$"The drain stopped before a later audible reflection: peak {omitted:R}, tail {actual.GeneratedFrames*1000.0/rate:F1} ms, later gap {(lastAbove-frames)*1000.0/rate:F1} ms.");
    }

    private static TailOutput Capture(int rate,SpatialEnvironment environment,float amount,float pause,float volume=1,bool profiles=false,
        float floor=.005f,bool writePrefix=true,int targetRate=0,int prefixBlock=int.MaxValue)
    {
        int prefix=Burst(rate).Length+(int)(rate*pause);
        float[] initial=new float[prefix]; Burst(rate).CopyTo(initial,0);
        var engine=new SpatialEffectsEngine(rate); engine.ApplyEnvironment(initial,environment,amount);
        AudioEqualizer? equalizer=profiles?AudioEqualizer.Create(["telephony-class-application"],rate):null;
        BiQuadFilter? filter=profiles?BiQuadFilter.LowPassFilter(rate,Math.Min(1800f,rate*.45f),.707f):null;
        equalizer?.Apply(initial);
        using var output=new MemoryStream(); using var manager=new AudioStreamManager(AudioFormat.Pcm,targetRate>0?targetRate:rate,output);
        object? resampler=null;
        var outputVolume=new VolumeShifter(targetRate>0?targetRate:rate,volume);
        if(targetRate>0)
        {
            Type type=Pipeline.Assembly.GetType("ONNX_Runner.Services.Synthesis.AudioOutputResampler")!;
            resampler=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,[rate,targetRate],null);
        }
        try
        {
            if(writePrefix)
                for(int offset=0;offset<initial.Length;offset+=Math.Min(prefixBlock,initial.Length-offset))
                    WriteOutput(initial.AsSpan(offset,Math.Min(prefixBlock,initial.Length-offset)),manager,resampler,filter,outputVolume);
            Invoke(Drain,[engine,environment,amount,rate,floor,equalizer,outputVolume,volume,manager,resampler,filter,CancellationToken.None]);
            if(resampler!=null) resampler.GetType().GetMethod("Complete",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(resampler,[manager,outputVolume,CancellationToken.None]);
            Invoke(FlushLimiter,[outputVolume,manager,CancellationToken.None]);
        }
        finally{(resampler as IDisposable)?.Dispose();}
        int generated=checked((int)manager.SamplesWritten)-(writePrefix?prefix:0);
        return new(output.ToArray(),generated);
    }
    private static OutputWrite BindWriter()
    {
        var method=new DynamicMethod("CallOutputWriter",typeof(void),[typeof(Span<float>),typeof(AudioStreamManager),typeof(object),typeof(BiQuadFilter),typeof(VolumeShifter)],typeof(SpatialTailRegressionProgram).Module,true);
        var il=method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Castclass,ResamplerType);
        il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Ldarg_S,(byte)4);
        il.Emit(OpCodes.Call,Pipeline.GetMethod("WriteOutput",BindingFlags.NonPublic|BindingFlags.Static)!); il.Emit(OpCodes.Ret);
        return method.CreateDelegate<OutputWrite>();
    }
    private static void Invoke(MethodInfo method,object?[] args)
    {
        try{method.Invoke(null,args);}
        catch(TargetInvocationException error) when(error.InnerException!=null){ExceptionDispatchInfo.Capture(error.InnerException).Throw();}
    }
    private static byte[] Encode(ReadOnlySpan<float> samples,int rate)
    {
        using var stream=new MemoryStream(); using var manager=new AudioStreamManager(AudioFormat.Pcm,rate,stream);
        float[] copy=samples.ToArray(); manager.WriteChunk(copy); return stream.ToArray();
    }
    private static float[] Decode(byte[] bytes)=>MemoryMarshal.Cast<byte,short>(bytes).ToArray().Select(x=>x/32767f).ToArray();
    private static float[] Burst(int rate)=>Enumerable.Range(0,rate/50).Select(i=>.4f*(float)Math.Sin(2*Math.PI*500*i/rate)).ToArray();
    private static float[] Signal(int rate,int count)=>Enumerable.Range(0,count).Select(i=>(float)(.2*Math.Sin(2*Math.PI*180*i/rate)+.08*Math.Sin(2*Math.PI*2300*i/rate))).ToArray();
    private static double Energy(ReadOnlySpan<float> samples){double sum=0;foreach(float x in samples)sum+=x*(double)x;return sum;}
    private static float Peak(ReadOnlySpan<float> samples){float peak=0;foreach(float x in samples)peak=Math.Max(peak,Math.Abs(x));return peak;}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static void Check(string name,Action action){_checks++;try{action();}catch(Exception error){_failures++;Console.Error.WriteLine($"FAIL {name}: {error.Message}");}}
    private sealed record Corpus(Reference[] References);
    private sealed record Reference(int Rate,string Environment,float Amount,double Energy,int[] Indices,float[] Samples,int WarmupFrames=0);
    private sealed record TailOutput(byte[] Bytes,int GeneratedFrames);
    private sealed class CancelingStream(CancellationTokenSource cancellation):MemoryStream
    {
        public override void Write(byte[] buffer,int offset,int count){base.Write(buffer,offset,count);cancellation.Cancel();}
        public override void Write(ReadOnlySpan<byte> buffer){base.Write(buffer);cancellation.Cancel();}
    }
}
