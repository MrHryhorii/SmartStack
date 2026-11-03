using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ONNX_Runner.Models.Audio;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Inference;
using ONNX_Runner.Models.Requests;
using ONNX_Runner.Services;
using ONNX_Runner.Services.Audio.Encoding;
using ONNX_Runner.Services.Infrastructure;

int checks = 0, failures = 0;
var signatures = new List<object>();
foreach (AudioFormat format in new[] { AudioFormat.Pcm, AudioFormat.Wav, AudioFormat.Flac, AudioFormat.Opus, AudioFormat.Aac, AudioFormat.Mp3 })
{
    Check($"{format} successful encoding remains deterministic", () =>
    {
        byte[] first = Encode(format), second = Encode(format);
        Require(first.AsSpan().SequenceEqual(second), "Equivalent encoding changed bytes.");
        signatures.Add(new { Format = format.ToString(), Bytes = first.Length, Sha256 = Convert.ToHexString(SHA256.HashData(first)) });
    });
    Check($"{format} abort remains idempotent and preserves response ownership", () =>
    {
        using var target = new TestStream();
        using var encoder = new AudioStreamManager(format, 24000, target);
        encoder.WriteChunk(new float[37]);
        encoder.Abort(); encoder.Abort(); encoder.Dispose();
        Require(!target.Closed || format == AudioFormat.Wav, "The encoder closed a caller-owned response.");
    });
}
Check("Pool observer detects actual outstanding and returned leases", () =>
{
    using var events = new PoolEvents(); events.Begin();
    var buffer = ArrayPool<byte>.Shared.Rent(123);
    Require(events.Outstanding == 1, "The observer missed a rental.");
    ArrayPool<byte>.Shared.Return(buffer); events.End();
    Require(events.Outstanding == 0 && events.WasReturned(buffer), "The observer missed a return.");
});
foreach (AudioFormat format in new[] { AudioFormat.Flac, AudioFormat.Opus })
foreach (int allowed in format == AudioFormat.Flac ? new[] { 0 } : new[] { 0, 1 })
    Check($"{format} constructor failure after {allowed} writes returns every lease", () =>
    {
        using var target = new TestStream(allowed);
        using var events = new PoolEvents(); events.Begin();
        Exception? error = null;
        try { using var encoder = new AudioStreamManager(format, 24000, target); }
        catch (IOException exception) { error = exception; }
        finally { events.End(); }
        Require(error is not null, "The fixture did not fail during header creation.");
        Require(events.Outstanding == 0, $"Constructor failure left {events.Outstanding} pooled buffers outstanding.");
        Require(!target.Closed, "Failed construction closed the caller-owned response.");
    });
Check("DI keeps externally registered instances caller-owned", () =>
{
    var resource = new CounterResource();
    using (var provider = new ServiceCollection().AddSingleton(resource).BuildServiceProvider())
        Require(ReferenceEquals(provider.GetRequiredService<CounterResource>(), resource), "DI replaced the instance.");
    Require(resource.Disposals == 0, "The external-instance ownership premise changed."); resource.Dispose();
});
Check("DI disposes resolved factory singletons exactly once", () =>
{
    var resource = new CounterResource();
    using (var provider = new ServiceCollection().AddSingleton(_ => resource).BuildServiceProvider())
        Require(ReferenceEquals(provider.GetRequiredService<CounterResource>(), resource), "DI replaced the instance.");
    Require(resource.Disposals == 1, "The host-owned singleton was not disposed exactly once.");
});
Type pipeline = typeof(AudioStreamManager).Assembly.GetType("ONNX_Runner.Services.Synthesis.ResponsePipeline")!;
foreach (bool sse in new[] { false, true })
foreach (string scenario in new[] { "success", "cancel", "write-failure" })
    await CheckAsync($"{(sse ? "SSE" : "audio")} {scenario} returns current and queued network buffers", async () =>
    {
        var channel = Channel.CreateBounded<(byte[] Buffer, int Length)>(2);
        byte[] first = ArrayPool<byte>.Shared.Rent(32), second = ArrayPool<byte>.Shared.Rent(32);
        first.AsSpan(0, 32).Fill(1); second.AsSpan(0, 32).Fill(2);
        Require(channel.Writer.TryWrite((first, 32)) && channel.Writer.TryWrite((second, 32)), "The queue fixture is incomplete.");
        if (scenario == "success") channel.Writer.TryComplete();
        using var cancellation = new CancellationTokenSource();
        if (scenario == "cancel") cancellation.Cancel();
        using var target = new TestStream(scenario == "write-failure" ? 0 : int.MaxValue);
        var writer = PipeWriter.Create(target, new StreamPipeWriterOptions(leaveOpen: true));
        using var events = new PoolEvents(); events.Begin(allThreads: true);
        Exception? error = null;
        try
        {
            object[] arguments = sse
                ? [channel, writer, Enum.ToObject(pipeline.GetNestedType("PayloadKind", BindingFlags.NonPublic)!, 0), cancellation.Token]
                : [channel, target, cancellation.Token];
            var task = (Task)pipeline.GetMethod(sse ? "SendSseToNetworkAsync" : "SendToNetworkAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments)!;
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) { error = exception; }
        finally { events.End(); await writer.CompleteAsync(error); }
        Require(scenario == "success" ? error is null : error is IOException or OperationCanceledException, $"Unexpected network outcome: {error}");
        Require(events.WasReturned(first) && events.WasReturned(second), "A network chunk was not returned.");
        Require(!channel.Reader.TryRead(out _), "The response retained queued chunks.");
    });
await CheckAsync("Canceling a queued request does not invent a request permit", async () =>
{
    using var services = BasicServices(); using var gate = new SemaphoreSlim(0, 1); using var cancellation = new CancellationTokenSource();
    var synthesis = new SpeechSynthesisService(gate, services, NativeAudioDependencies.Detect(), NullLogger<SpeechSynthesisService>.Instance);
    var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
    var pending = synthesis.SynthesizeAsync(Request(), context, cancellation.Token); cancellation.Cancel();
    await pending.WaitAsync(TimeSpan.FromSeconds(5)); Require(gate.CurrentCount == 0, "A canceled waiter released another request's permit.");
});
await CheckAsync("A request setup failure releases its acquired permit", async () =>
{
    using var services = BasicServices(); using var gate = new SemaphoreSlim(1, 1);
    var synthesis = new SpeechSynthesisService(gate, services, NativeAudioDependencies.Detect(), NullLogger<SpeechSynthesisService>.Instance);
    // Missing context services cause a failure after the request permit has been acquired.
    var result = await synthesis.SynthesizeAsync(Request(), new DefaultHttpContext(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    Require(result is IStatusCodeHttpResult { StatusCode: 500 } && gate.CurrentCount == 1, "Failed setup leaked the permit.");
});
if (args.Length == 1) File.WriteAllText(args[0], JsonSerializer.Serialize(signatures, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { Checks = checks, Failures = failures, EncodedFormats = signatures.Count }));
return failures == 0 ? 0 : 1;

byte[] Encode(AudioFormat format)
{
    using var target = new MemoryStream();
    using (var encoder = new AudioStreamManager(format, 24000, target))
    {
        float[] samples = new float[6000];
        for (int i = 0; i < samples.Length; i++) samples[i] = .15f * MathF.Sin(i * .137f);
        for (int i = 0; i < samples.Length; i += 317) encoder.WriteChunk(samples.AsSpan(i, Math.Min(317, samples.Length - i)));
    }
    byte[] encoded = target.ToArray();
    if (format != AudioFormat.Opus) return encoded;
    // Ogg serials are random. All remaining header, timing, lacing and payload bytes are compared.
    for (int position = 0; position < encoded.Length;)
    {
        Require(encoded.AsSpan(position, 4).SequenceEqual("OggS"u8), "Malformed Ogg page.");
        encoded.AsSpan(position + 14, 4).Clear(); encoded.AsSpan(position + 22, 4).Clear();
        int segments = encoded[position + 26], payload = 0;
        for (int i = 0; i < segments; i++) payload += encoded[position + 27 + i];
        position += 27 + segments + payload;
    }
    return encoded;
}
void Check(string name, Action body)
{
    checks++; try { body(); Console.WriteLine("PASS " + name); } catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
async Task CheckAsync(string name, Func<Task> body)
{
    checks++; try { await body(); Console.WriteLine("PASS " + name); } catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static ServiceProvider BasicServices() => new ServiceCollection().AddSingleton(new ApiSettings()).AddSingleton(new PiperConfig()).AddSingleton(new StreamSettings()).BuildServiceProvider();
static SynthesisRequest Request() => new() { Input = "Resource ownership check.", Voice = "piper_base", Format = AudioFormat.Pcm, Speed = 1 };
sealed class CounterResource : IDisposable { public int Disposals { get; private set; } public void Dispose() => Disposals++; }
