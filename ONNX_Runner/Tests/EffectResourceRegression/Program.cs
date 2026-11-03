using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Dsp;
using ONNX_Runner.Models.Audio;
using ONNX_Runner.Services.Audio.Effects;
using ONNX_Runner.Services.Audio.Spatial;
using ONNX_Runner.Services.Audio.Encoding;
using ONNX_Runner.Services.Audio.Levels;
using Signalsmith;
using SoundTouch;

const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
if (args is ["--surface"])
{
    for (Type? type = typeof(Stretch); type != null; type = type.BaseType)
        Console.WriteLine(JsonSerializer.Serialize(new { Type = type.FullName, type.IsValueType,
            Fields = type.GetFields(Hidden | BindingFlags.Public | BindingFlags.DeclaredOnly).Select(field => new { field.Name, Type = field.FieldType.FullName }) }));
    return 0;
}
if (args.Length > 1) throw new ArgumentException("Provide at most one output JSON path.");
int checks = 0, failures = 0;
var signatures = new List<object>();
var allocations = new List<object>();
foreach (int rate in new[] { 8000, 22050, 48000 })
{
    foreach (VoiceEffectType effect in Enum.GetValues<VoiceEffectType>())
    foreach (float amount in new[] { .0f, .5f, 1f })
    {
        Check($"Effect PCM and steady allocations: {effect}, {rate}, {amount}", () =>
        {
            float[] source = Signal(rate, rate / 3 + 17);
            using var engine = new AudioEffectsEngine(new(), rate);
            engine.Reset(71239);
            var pcm = new List<float>();
            for (int offset = 0; offset < source.Length; offset += 257)
            {
                Span<float> block = source.AsSpan(offset, Math.Min(257, source.Length - offset));
                int length = engine.ApplyEffect(block, effect, amount);
                pcm.AddRange(block[..length].ToArray());
            }
            var complete = typeof(AudioEffectsEngine).GetMethod("Complete", Hidden)!.CreateDelegate<Completion>(engine);
            float[] tail = new float[257];
            for (int pass = 0; pass < rate * 8 / tail.Length + 100; pass++)
            {
                int length = complete(tail);
                if (length == 0) break;
                pcm.AddRange(tail.AsSpan(0, length).ToArray());
                if (pass == rate * 8 / tail.Length + 99) throw new InvalidOperationException("Effect tail did not finish.");
            }
            signatures.Add(new { Kind = "effect", Name = effect.ToString(), Rate = rate, Amount = amount,
                Samples = pcm.Count, Sha256 = Hash(pcm.ToArray()) });

            using var warm = new AudioEffectsEngine(new(), rate);
            warm.Reset(71239);
            float[] samples = Signal(rate, 1024);
            for (int i = 0; i < 24; i++) warm.ApplyEffect(samples, effect, amount);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 32; i++) warm.ApplyEffect(samples, effect, amount);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(bytes == 0, $"Steady effect processing allocated {bytes} bytes.");
        });
    }

    foreach (SpatialEnvironment environment in Enum.GetValues<SpatialEnvironment>())
    foreach (float amount in new[] { .0f, .5f, 1f })
    {
        Check($"Spatial PCM and steady allocations: {environment}, {rate}, {amount}", () =>
        {
            var engine = new SpatialEffectsEngine(rate);
            float[] source = Signal(rate, rate + 17);
            for (int offset = 0; offset < source.Length; offset += 257)
                engine.ApplyEnvironment(source.AsSpan(offset, Math.Min(257, source.Length - offset)), environment, amount);
            signatures.Add(new { Kind = "spatial", Name = environment.ToString(), Rate = rate, Amount = amount,
                Samples = source.Length, Sha256 = Hash(source) });
            float[] samples = new float[257];
            for (int i = 0; i < 4; i++) engine.ApplyEnvironment(samples, environment, amount);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 32; i++) engine.ApplyEnvironment(samples, environment, amount);
            Require(GC.GetAllocatedBytesForCurrentThread() == before, "Steady spatial processing allocated.");
        });
    }

    Check($"Unused spatial buffers stay unallocated: {rate}", () =>
    {
        _ = new SpatialEffectsEngine(rate);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var engine = new SpatialEffectsEngine(rate);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        allocations.Add(new { Rate = rate, ConstructorBytes = bytes });
        Require(bytes <= 512, $"Spatial construction allocated {bytes} bytes before selecting an environment.");
    });

    Check($"Spatial transitions and explicit reset preserve state: {rate}", () =>
    {
        var engine = new SpatialEffectsEngine(rate);
        foreach (var env in new[] { SpatialEnvironment.Muffled, SpatialEnvironment.InnerVoice, SpatialEnvironment.Room,
            SpatialEnvironment.Echo, SpatialEnvironment.InnerVoice, SpatialEnvironment.DeepWater })
        {
            float[] source = Signal(rate, rate / 2 + 17);
            engine.ApplyEnvironment(source, env, .5f);
            engine.Reset();
            float[] after = Signal(rate, rate / 2 + 17);
            engine.ApplyEnvironment(after, env, .5f);
            signatures.Add(new { Kind = "transition-reset", Name = env.ToString(), Rate = rate, Amount = .5f,
                Samples = after.Length, Sha256 = Hash(after) });
        }
    });
}

foreach (string outcome in new[] { "dispose", "reset", "switch", "invalid-intensity" })
    Check($"Incomplete native harmony ownership: {outcome}", () =>
    {
        using var engine = new AudioEffectsEngine(new(), 22050);
        if (outcome == "invalid-intensity")
        {
            bool failed = false;
            try { engine.ApplyEffect(new float[] { .1f }, VoiceEffectType.Harmonizer, float.NaN); }
            catch (ArgumentOutOfRangeException) { failed = true; }
            Require(failed, "The fault fixture did not throw.");
        }
        else engine.ApplyEffect(new float[] { .1f }, VoiceEffectType.Harmonizer, .5f);
        object harmony = typeof(AudioEffectsEngine).GetField("_harmonizer", Hidden)!.GetValue(engine)!;
        object third = harmony.GetType().GetField("_third", Hidden)!.GetValue(harmony)!;
        object fifth = harmony.GetType().GetField("_fifth", Hidden)!.GetValue(harmony)!;
        Require(LiveHandles(third) > 0 && LiveHandles(fifth) > 0, "No live native handles were observed.");
        if (outcome == "reset") engine.Reset(71239);
        else if (outcome == "switch") engine.ApplyEffect(new float[] { .1f }, VoiceEffectType.Telephone, 1);
        else { engine.Dispose(); engine.Dispose(); }
        Require(LiveHandles(third) == 0 && LiveHandles(fifth) == 0, "Native harmony handles survived ownership cleanup.");
    });

foreach (var effect in new[] { VoiceEffectType.Telephone, VoiceEffectType.FmRadio })
    Check($"Crossfade failure returns the dry pool lease: {effect}", () =>
    {
        using var engine = new AudioEffectsEngine(new(), 22050);
        using var events = new PoolEvents();
        events.Begin();
        bool failed = false;
        try { engine.ApplyEffect(new float[] { .1f }, effect, float.NaN); }
        catch (ArgumentOutOfRangeException) { failed = true; }
        finally { events.End(); }
        Require(failed && events.Outstanding == 0, $"Failure left {events.Outstanding} pool leases.");
    });

foreach (bool spatial in new[] { false, true })
foreach (bool canceled in new[] { false, true })
    Check($"Tail pool cleanup: spatial={spatial}, canceled={canceled}", () =>
    {
        using var engine = new AudioEffectsEngine(new(), 22050);
        var environment = new SpatialEffectsEngine(22050);
        if (spatial) environment.ApplyEnvironment(Signal(22050, 4096), SpatialEnvironment.InnerVoice, .5f);
        else engine.ApplyEffect(Signal(22050, 4096), VoiceEffectType.Harmonizer, .5f);
        using var stream = new TestStream(canceled ? int.MaxValue : 0);
        using var manager = new AudioStreamManager(AudioFormat.Pcm, 22050, stream);
        using var cancellation = new CancellationTokenSource();
        if (canceled) cancellation.Cancel();
        Type pipeline = typeof(AudioEffectsEngine).Assembly.GetType("ONNX_Runner.Services.Synthesis.AudioGenerationPipeline")!;
        object[] arguments = spatial
            ? [environment, SpatialEnvironment.InnerVoice, .5f, 22050, .005f, null!, new VolumeShifter(22050, 1), 1f, manager, null!, null!, cancellation.Token]
            : [engine, null!, SpatialEnvironment.None, 0f, 22050, null!, new VolumeShifter(22050, 1), manager, null!, null!, cancellation.Token];
        using var events = new PoolEvents();
        events.Begin();
        Exception? error = null;
        try { pipeline.GetMethod(spatial ? "DrainSpatialTail" : "DrainEffectTail", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments); }
        catch (TargetInvocationException failure) { error = failure.InnerException; }
        finally { events.End(); manager.Abort(); }
        Require(canceled ? error is OperationCanceledException : error is IOException, "The tail failure fixture did not reach the intended exit.");
        Require(events.Outstanding == 0, $"Tail failure retained {events.Outstanding} pool leases.");
    });

if (args.Length == 1) File.WriteAllText(args[0], JsonSerializer.Serialize(new { Checks = checks, Failures = failures, Signatures = signatures, Allocations = allocations,
    ThirdParty = new { FftDisposable = typeof(IDisposable).IsAssignableFrom(typeof(FftProcessor)),
        SoundTouchDisposable = typeof(IDisposable).IsAssignableFrom(typeof(SoundTouchProcessor)),
        StretchBase = typeof(Stretch).BaseType?.FullName,
        StretchValueType = typeof(Stretch).IsValueType,
        StretchFields = typeof(Stretch).GetFields(Hidden | BindingFlags.Public).Select(field => new { field.Name, Type = field.FieldType.FullName }).ToArray() } },
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { Checks = checks, Failures = failures, Signatures = signatures.Count }));
return failures == 0 ? 0 : 1;

void Check(string name, Action body)
{
    checks++;
    try { body(); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static float[] Signal(int rate, int length)
{
    var result = new float[length];
    for (int i = 0; i < length; i++)
        result[i] = i < rate / 3 ? .13f * MathF.Sin(i * (2 * MathF.PI * 173 / rate)) + .02f * MathF.Cos(i * .117f) : 0;
    return result;
}
static string Hash(float[] samples) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(samples.AsSpan())));
static unsafe int LiveHandles(object value) => ((Stretch)value).Handle == null ? 0 : 1;
delegate int Completion(Span<float> samples);
