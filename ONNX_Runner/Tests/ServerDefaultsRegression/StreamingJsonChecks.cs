using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ONNX_Runner.Endpoints;
using ONNX_Runner.Models.Requests;
using static TestRun;

internal static class StreamingJsonChecks
{
    internal static async Task Run(TestRun run)
    {
        var read = typeof(SynthesisRequest).Assembly.GetType("ONNX_Runner.Endpoints.SpeechRequestJson")!
            .GetMethod("ReadAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(TsubakiSpeechRequest))
            .CreateDelegate<Func<HttpRequest, CancellationToken, Task<(TsubakiSpeechRequest? Request, IResult? Error)>>>();
        var cases = new (string Name, string Json, Action<SynthesisRequest> Verify)[]
        {
            ("Two valid profiles", """{"input":"Hello.","equalizer":["handset-class-device","telephony-class-application"],"volume":0,"stream":false}""",
                request => Require(request.Equalizer is { Length: 2 } && request.Volume == 0 && request.Stream == false, "Streaming changed valid controls.")),
            ("One valid profile", """{"input":"Hello.","equalizer":["handset-class-device"]}""",
                request => Require(request.Equalizer is { Length: 1 }, "Streaming lost the profile array.")),
            ("Invalid float object", """{"pitch":{"bad":[1,{"nested":true}]},"volume":0.5,"input":"Hello."}""",
                request => Require(request.Pitch == null && request.Volume == .5f, "Streaming failed optional float fallback.")),
            ("Invalid float string", """{"volume":"NaN","input":"Hello."}""",
                request => Require(request.Volume == null, "Streaming retained a non-finite number.")),
            ("Invalid boolean object", """{"stream":{"bad":[1,2]},"volume":0.5,"input":"Hello."}""",
                request => Require(request.Stream == null && request.Volume == .5f, "Streaming failed optional boolean fallback.")),
            ("Invalid integer array", """{"samplerate":[8000,{"bad":true}],"input":"Hello.","volume":0.5}""",
                request => Require(request.SampleRate == null && request.Volume == .5f, "Streaming failed optional integer fallback.")),
            ("Invalid string object", """{"effect":{"id":"Telephone"},"input":"Hello.","volume":0.5}""",
                request => Require(request.Effect == null && request.Volume == .5f, "Streaming failed optional string fallback.")),
            ("Invalid profile entry", """{"equalizer":["handset-class-device",{"bad":[1,2]}],"input":"Hello.","volume":0.5}""",
                request => Require(request.Equalizer == null && request.Volume == .5f, "Streaming failed profile-list fallback.")),
            ("Invalid voice array", """{"voice":[{"id":"alloy"}],"input":"Hello.","volume":0.5}""",
                request => Require(request.Voice == "piper_base" && request.Volume == .5f, "Streaming failed voice fallback.")),
            ("Large capped profile array", JsonSerializer.Serialize(new { equalizer = Enumerable.Repeat("handset-class-device", 1024).ToArray(), input = "Hello.", volume = .5 }),
                request => Require(request.Equalizer is { Length: 16 } && request.Volume == .5f, "Streaming lost the raw-entry cap or following fields.")),
        };
        foreach (var item in cases)
        foreach (int fragment in new[] { 1, 7, 31, 8192 })
            await run.CheckAsync($"Fragmented JSON {item.Name}, {fragment} bytes/read", async () =>
            {
                using var body = new FragmentedStream(Encoding.UTF8.GetBytes(item.Json), fragment);
                var context = new DefaultHttpContext(); context.Request.ContentType = "application/json";
                context.Request.Body = body;
                var (dto, error) = await read(context.Request, CancellationToken.None);
                Require(error == null && dto?.Input == "Hello.", "Complete fragmented JSON was rejected.");
                var request = TsubakiRequestAdapter.ToSynthesisRequest(dto!).Request!;
                item.Verify(request);
            });
        foreach (string json in new[] { "{\"input\":\"Hello.\",\"pitch\":[}", "{\"input\":null}" })
            await run.CheckAsync("Fragmented malformed/required JSON remains rejected: " + json, async () =>
            {
                using var body = new FragmentedStream(Encoding.UTF8.GetBytes(json), 1);
                var context = new DefaultHttpContext(); context.Request.ContentType = "application/json"; context.Request.Body = body;
                var (_, error) = await read(context.Request, CancellationToken.None);
                Require(error is IStatusCodeHttpResult { StatusCode: 400 }, "Malformed JSON or an invalid required value was accepted.");
            });
        await run.CheckAsync("HTTP JSON charset conversion remains supported", async () =>
        {
            using var body = new MemoryStream(Encoding.Unicode.GetBytes("{\"input\":123,\"volume\":\"0.5\",\"stream\":\"false\"}"));
            var context = new DefaultHttpContext();
            context.Request.ContentType = "application/json; charset=utf-16";
            context.Request.Body = body;
            var (request, error) = await read(context.Request, CancellationToken.None);
            Require(error == null && request is { Input: "123", Volume: .5f, Stream: false }, "HTTP charset or scalar conversion changed.");
        });
    }

    private sealed class FragmentedStream(byte[] data, int fragment) : Stream
    {
        private readonly MemoryStream _body = new(data, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _body.Read(buffer, offset, Math.Min(fragment, count));
        public override int Read(Span<byte> buffer) => _body.Read(buffer[..Math.Min(fragment, buffer.Length)]);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _body.ReadAsync(buffer[..Math.Min(fragment, buffer.Length)], cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _body.Dispose(); base.Dispose(disposing); }
    }
}
