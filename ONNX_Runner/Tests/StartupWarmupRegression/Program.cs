using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ONNX_Runner.Models.Audio;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Requests;
using ONNX_Runner.Endpoints;
using ONNX_Runner.Services;
using ONNX_Runner.Services.Infrastructure;

var checks = new Checks();
Assembly engine = typeof(SpeechSynthesisService).Assembly;
Type stateType = engine.GetType("ONNX_Runner.Services.Infrastructure.StartupWarmupState")!;
Type runnerType = engine.GetType("ONNX_Runner.Services.Infrastructure.StartupWarmup")!;
const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic;
string headerName = (string)stateType.GetField("HeaderName", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
object State() => Activator.CreateInstance(stateType)!;
string Header(object state, int pass) => (string)stateType.GetMethod("HeaderForPass", Internal)!.Invoke(state, [pass])!;
Func<HttpRequest, bool> IsStartup(object state) => stateType.GetMethod("IsStartupRequest", Internal)!.CreateDelegate<Func<HttpRequest, bool>>(state);
TryClaim Claim(object state) => stateType.GetMethod("TryClaim", Internal)!.CreateDelegate<TryClaim>(state);
void Complete(object state) => stateType.GetMethod("Complete", Internal)!.Invoke(state, null);
DefaultHttpContext Context(string? header = null)
{
    var context = new DefaultHttpContext();
    if (header != null) context.Request.Headers[headerName] = header;
    return context;
}
checks.Run("Unmarked/forged headers retain normal API behavior", () =>
{
    object state = State();
    foreach (string? header in new[] { null, "", "-1", "0", "not-a-startup-token/0" })
        Checks.Require(!IsStartup(state)(Context(header).Request), "A normal caller was accepted as startup traffic.");
});
checks.Run("Reserved requests claim -1 and 0 exactly once", () =>
{
    object state = State(); var claim = Claim(state);
    for (int pass = 0; pass < 2; pass++)
    {
        var context = Context(Header(state, pass));
        Checks.Require(IsStartup(state)(context.Request) && claim(context.Request, out long id) && id == pass - 1, "Reserved request ID changed.");
        Checks.Require(!IsStartup(state)(context.Request) && !claim(context.Request, out _), "A startup token was replayable.");
    }
});
checks.Run("Concurrent duplicate startup requests have one claimant", () =>
{
    object state = State(); string header = Header(state, 0); var claim = Claim(state); int accepted = 0;
    Parallel.For(0, 32, _ => { if (claim(Context(header).Request, out long id)) { Checks.Require(id == -1, "Wrong concurrent startup ID."); Interlocked.Increment(ref accepted); } });
    Checks.Require(accepted == 1, "A reserved ID was claimed more than once.");
});
checks.Run("Completed startup rejects all reserved markers without managed allocations", () =>
{
    object state = State(); string header = Header(state, 0); var context = Context(header); var match = IsStartup(state);
    Complete(state); Complete(state);
    HttpRequest request = context.Request;
    for (int i = 0; i < 50000; i++) match(request);
    long allocated = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 10000; i++) if (match(request)) throw new InvalidOperationException("Completed startup accepted a marker.");
    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
    Checks.Require(allocated == 0, $"Inactive startup checks allocated {allocated} bytes.");
});
foreach (var (voices, expected) in new (string[], string)[]
{
    ([], "piper_base"), (["piper_base"], "piper_base"), (["zulu", "alloy"], "alloy"), (["alloy", "female"], "female"), (["FEMALE", "piper_base"], "FEMALE")
})
foreach (bool mp3 in new[] { false, true })
    checks.Run($"Two warm-up plans select available voice/codec: {string.Join(',', voices)}, MP3={mp3}", () =>
    {
        Array passes = (Array)runnerType.GetMethod("CreatePasses", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [mp3, voices])!;
        Checks.Require(passes.Length == 2, "Startup did not create exactly two requests.");
        string Route(int pass) => (string)passes.GetValue(pass)!.GetType().GetProperty("Route")!.GetValue(passes.GetValue(pass))!;
        object Payload(int pass) => passes.GetValue(pass)!.GetType().GetProperty("Payload")!.GetValue(passes.GetValue(pass))!;
        Checks.Require(Route(0) == "/tsbk/audio/speech" && Route(1) == "/tsbk/audio/speech", "Warm-up must use explicit Tsubaki cloning controls.");
        var first = (TsubakiSpeechRequest)Payload(0); var second = (TsubakiSpeechRequest)Payload(1);
        Checks.Require(first.Voice == expected && second.Voice == expected && first.CloneIntensity == 1 && second.CloneIntensity == 1, "Both warm-up passes must request full available cloning or base fallback.");
        Checks.Require(first.Stream == false && second.Stream == true && first.StreamFormat == "audio" && second.StreamFormat == "sse" && first.ResponseFormat == (mp3 ? "mp3" : "pcm") && second.ResponseFormat == first.ResponseFormat, "Buffered audio, streaming SSE or codec fallback was lost.");
    });
TsubakiSpeechRequest[] Plans()
{
    Array passes = (Array)runnerType.GetMethod("CreatePasses", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [true, new[] { "female", "piper_base" }])!;
    return passes.Cast<object>().Select(pass => (TsubakiSpeechRequest)pass.GetType().GetProperty("Payload")!.GetValue(pass)!).ToArray();
}
checks.Run("Both halves preserve the supplied multilingual/technical warm-up text", () =>
{
    const string text = """Maya checked config.prod.json—twice—and whispered, "No... esto no funciona; but maybe, demain, ça ira?", then added (almost laughing): Я перевірю ще раз — pero, please, don't restart https://example.com/api?v=2.3.1&mode=fast; if Dr. Smith replies, say "sí, d'accord", otherwise... wait.""";
    var plans = Plans();
    Checks.Require(plans[0].Input + " " + plans[1].Input == text, "Warm-up lost part of the supplied text.");
    Checks.Require(plans[0].Language == "en" && plans[1].Language == "auto", "Forced English and automatic detection were not separated.");
});
checks.Run("Every declared request field is included with a non-null supported value", () =>
{
    string[] fields = typeof(TsubakiSpeechRequest).GetProperties().Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name).OfType<string>().Order().ToArray();
    foreach (var plan in Plans())
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(plan));
        Checks.Require(json.RootElement.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(fields), "Warm-up does not serialize the complete request contract.");
        Checks.Require(json.RootElement.EnumerateObject().All(property => property.Value.ValueKind != JsonValueKind.Null), "A warm-up request field was left null.");
    }
});
checks.Run("Active DSP controls avoid neutral pitch, volume, effects, profiles and output clock", () =>
{
    foreach (var plan in Plans())
    {
        Checks.Require(plan.Speed != 1 && plan.Pitch is > 0 and not 1 && plan.Volume is > 0 and not 1 && plan.SampleRate > 0, "A processing branch remained neutral.");
        Checks.Require(Enum.TryParse(plan.Effect, out VoiceEffectType effect) && effect != VoiceEffectType.None && plan.EffectIntensity > 0, "The character effect was bypassed.");
        Checks.Require(Enum.TryParse(plan.Environment, out SpatialEnvironment environment) && environment != SpatialEnvironment.None && plan.EnvironmentIntensity > 0, "The spatial environment was bypassed.");
        Checks.Require(plan.Equalizer is { Length: > 0 } && plan.Equalizer.All(profile => AudioEqualizerProfiles.Find(profile) != null) && plan.LowPassQFactor > 0, "Device profiles or the filter control were unusable.");
    }
    Checks.Require(Plans()[0].EarlySplit == true && Plans()[1].EarlySplit == false && Plans()[0].ExtendReverbTail == true && Plans()[1].ExtendReverbTail == false, "Early splitting and spatial-tail choices were not exercised.");
});
await checks.RunAsync("The production HTTP reader and adapter retain the warm-up controls", async () =>
{
    var read = engine.GetType("ONNX_Runner.Endpoints.SpeechRequestJson")!.GetMethod("ReadAsync", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(TsubakiSpeechRequest));
    foreach (var plan in Plans())
    {
        var context = new DefaultHttpContext(); context.Request.ContentType = "application/json";
        using var body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(plan)); context.Request.Body = body;
        var parsed = await (Task<(TsubakiSpeechRequest?, IResult?)>)read.Invoke(null, [context.Request, CancellationToken.None])!;
        Checks.Require(parsed.Item1 != null && parsed.Item2 == null, "A full warm-up request was rejected.");
        var (request, error) = TsubakiRequestAdapter.ToSynthesisRequest(parsed.Item1!);
        Checks.Require(request != null && error == null && request.Input == plan.Input && request.Voice == plan.Voice && request.CloneIntensity == 1, "Warm-up text or cloning was lost.");
        Checks.Require(request!.Language == (plan.Language == "auto" ? null : "en") && request.Pitch == plan.Pitch && request.Volume == plan.Volume && request.Speed == plan.Speed, "Language, pitch, gain or speed changed during reading.");
        Checks.Require(request.Effect == plan.Effect && request.Environment == plan.Environment && request.EffectIntensity == plan.EffectIntensity && request.EnvironmentIntensity == plan.EnvironmentIntensity && request.Equalizer!.SequenceEqual(plan.Equalizer!), "The active DSP controls did not reach the adapter.");
        Checks.Require(request.SampleRate == plan.SampleRate && request.EarlySplit == plan.EarlySplit && request.ExtendReverbTail == plan.ExtendReverbTail && request.LowPassQFactor == plan.LowPassQFactor && request.Stream == plan.Stream && request.StreamFormat == (plan.Stream == true ? SpeechStreamFormat.Sse : SpeechStreamFormat.Audio), "The clock, filter, tail or delivery controls changed.");
    }
});
await checks.RunAsync("Warm-up failures do not advance normal request numbering", async () =>
{
    object state = State(); var logger = new CaptureLogger();
    using var services = new ServiceCollection().AddSingleton(new ApiSettings()).AddSingleton(stateType, state).BuildServiceProvider();
    using var gate = new SemaphoreSlim(1, 1);
    var synthesis = new SpeechSynthesisService(gate, services, NativeAudioDependencies.Detect(), logger);
    SynthesisRequest Request() => new() { Input = "A request-numbering fixture.", Voice = "piper_base", Format = AudioFormat.Pcm, Speed = 1 };
    for (int pass = 0; pass < 2; pass++)
        Checks.Require(await synthesis.SynthesizeAsync(Request(), Context(Header(state, pass)), CancellationToken.None) is IStatusCodeHttpResult { StatusCode: 500 }, "Missing-model failure fixture did not run.");
    Complete(state);
    await synthesis.SynthesizeAsync(Request(), Context(), CancellationToken.None);
    await synthesis.SynthesizeAsync(Request(), Context(Header(state, 0)), CancellationToken.None);
    Checks.Require(logger.Ids.SequenceEqual(new long[] { -1, 0, 1, 2 }), "Startup requests advanced or reset the normal sequence.");
});
await checks.RunAsync("Skipped startup preserves positive unique IDs under parallel requests", async () =>
{
    object state = State(); Complete(state); var logger = new CaptureLogger();
    using var services = new ServiceCollection().AddSingleton(new ApiSettings()).AddSingleton(stateType, state).BuildServiceProvider();
    using var gate = new SemaphoreSlim(1, 1);
    var synthesis = new SpeechSynthesisService(gate, services, NativeAudioDependencies.Detect(), logger);
    await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => synthesis.SynthesizeAsync(new SynthesisRequest { Input = "Numbering fixture.", Voice = "piper_base", Speed = 1, Format = AudioFormat.Pcm }, Context(), CancellationToken.None))));
    Checks.Require(logger.Ids.Count == 32 && logger.Ids.All(id => id > 0) && logger.Ids.Distinct().Count() == 32, "Normal request IDs collided or became nonpositive.");
});
await checks.RunAsync("Canceled startup closes its reserved markers without sending requests", async () =>
{
    object state = State(); var context = Context(Header(state, 0));
    using var services = new ServiceCollection().AddSingleton(new ClonerSettings { EnableCloning = false }).BuildServiceProvider();
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    var task = (Task)runnerType.GetMethod("RunAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, ["http://127.0.0.1:1", services, false, state, cancellation.Token])!;
    await task.WaitAsync(TimeSpan.FromSeconds(5));
    Checks.Require(!IsStartup(state)(context.Request) && !Claim(state)(context.Request, out _), "Canceled startup left the reserved marker active.");
});
int httpIndex = Array.IndexOf(args, "--http");
if (httpIndex >= 0) await HttpChecks.Run(checks, Path.GetFullPath(args[httpIndex + 1]));
int reportIndex = Array.IndexOf(args, "--report");
if (reportIndex >= 0) File.WriteAllText(args[reportIndex + 1], JsonSerializer.Serialize(new { checks.Total, checks.Failures, checks.Evidence }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { Checks = checks.Total, checks.Failures }));
return checks.Failures == 0 ? 0 : 1;

delegate bool TryClaim(HttpRequest request, out long id);
sealed class CaptureLogger : ILogger<SpeechSynthesisService>
{
    internal ConcurrentQueue<long> Ids { get; } = new();
    private static readonly PropertyInfo Current = typeof(SpeechSynthesisService).Assembly.GetType("ONNX_Runner.Services.Infrastructure.RequestLogContext")!.GetProperty("RequestId")!;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> format)
    {
        if (format(state, error).StartsWith("Request received |", StringComparison.Ordinal) && Current.GetValue(null) is long id) Ids.Enqueue(id);
    }
}
internal sealed class Checks
{
    internal int Total { get; private set; }
    internal int Failures { get; private set; }
    internal List<object> Evidence { get; } = [];
    internal void Run(string name, Action action) { Total++; try { action(); } catch (Exception error) { Failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); } }
    internal async Task RunAsync(string name, Func<Task> action) { Total++; try { await action(); } catch (Exception error) { Failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); } }
    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
