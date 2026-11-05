using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ONNX_Runner.Models.Configuration;

internal static class ResponseChecks
{
    private const string Delta = "data: {\"type\":\"speech.audio.delta\",\"audio\":\"AQID\"}\n\n";
    private const string Done = "data: {\"type\":\"speech.audio.done\"}\n\n";

    internal static async Task Run(Checks checks, Type runnerType, Type stateType)
    {
        var validate = runnerType.GetMethod("ValidateResponseAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<HttpResponseMessage, bool, CancellationToken, Task>>();

        foreach (var (name, body, sse, mediaType, status, succeeds) in new[]
        {
            ("Buffered audio completes", "audio", false, "audio/pcm", HttpStatusCode.OK, true),
            ("Empty buffered audio fails", "", false, "audio/pcm", HttpStatusCode.OK, false),
            ("HTTP failure cannot pass with audio", "audio", false, "audio/pcm", HttpStatusCode.InternalServerError, false),
            ("SSE audio and completion pass", Delta + Done, true, "text/event-stream", HttpStatusCode.OK, true),
            ("SSE comments and CRLF framing pass", ": heartbeat\r\n\r\n" + (Delta + Done).Replace("\n", "\r\n"), true, "text/event-stream", HttpStatusCode.OK, true),
            ("Empty HTTP 200 SSE fails", "", true, "text/event-stream", HttpStatusCode.OK, false),
            ("SSE audio without completion fails", Delta, true, "text/event-stream", HttpStatusCode.OK, false),
            ("SSE completion without audio fails", Done, true, "text/event-stream", HttpStatusCode.OK, false),
            ("SSE empty audio cannot pass", "data: {\"type\":\"speech.audio.delta\",\"audio\":\"\"}\n\n" + Done, true, "text/event-stream", HttpStatusCode.OK, false),
            ("Malformed SSE JSON fails", Delta + "data: {\"type\":\"speech.audio.done\"\n\n", true, "text/event-stream", HttpStatusCode.OK, false),
            ("JSON HTTP 200 cannot replace SSE", "{\"error\":\"generation failed\"}", true, "application/json", HttpStatusCode.OK, false),
            ("SSE error property overrides completion", Delta + Done + "data: {\"error\":\"generation failed\"}\n\n", true, "text/event-stream", HttpStatusCode.OK, false),
            ("HTTP failure cannot pass with SSE completion", Delta + Done, true, "text/event-stream", HttpStatusCode.InternalServerError, false)
        })
            await checks.RunAsync("Warm-up response: " + name, async () =>
            {
                using var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, mediaType)
                };
                bool accepted = false;
                try { await validate(response, sse, CancellationToken.None); accepted = true; }
                catch (Exception error) when (!succeeds && error is InvalidDataException or System.Text.Json.JsonException or HttpRequestException) { }
                Checks.Require(accepted == succeeds, "The warm-up response was counted incorrectly.");
            });

        foreach (string type in new[] { "error", "speech.error", "speech.audio.error" })
            await checks.RunAsync("Warm-up response: SSE " + type + " overrides completion", async () =>
            {
                using var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Delta + Done + "data: {\"type\":\"" + type + "\"}\n\n", Encoding.UTF8, "text/event-stream")
                };
                try { await validate(response, true, CancellationToken.None); }
                catch (InvalidDataException) { return; }
                throw new InvalidOperationException("An SSE error was accepted as a completed warm-up.");
            });

        await checks.RunAsync("Warm-up response: cancellation interrupts reading", async () =>
        {
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Delta + Done, Encoding.UTF8, "text/event-stream")
            };
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await validate(response, true, cancellation.Token); }
            catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("Canceled response validation completed successfully.");
        });

        foreach (var (firstFails, secondCompletes, expected) in new[]
        {
            (false, true, 2), (false, false, 1), (true, false, 0)
        })
            await checks.RunAsync($"HTTP warm-up counts {expected}/2 completed responses", async () =>
            {
                // Exercise RunAsync through real HTTP so a successful header cannot mask failed synthesis.
                var builder = WebApplication.CreateSlimBuilder();
                // The fixture must not inherit the engine's configured Kestrel endpoint.
                builder.Configuration.Sources.Clear();
                builder.Configuration.AddInMemoryCollection();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                await using var app = builder.Build();
                int requests = 0;
                app.MapPost("/tsbk/audio/speech", async (HttpContext context) =>
                {
                    int pass = Interlocked.Increment(ref requests);
                    context.Response.StatusCode = pass == 1 && firstFails ? 500 : 200;
                    context.Response.ContentType = pass == 1 ? "audio/pcm" : "text/event-stream";
                    string body = pass == 1 ? (firstFails ? "generation failed" : "audio") : Delta + (secondCompletes ? Done : "");
                    await context.Response.WriteAsync(body);
                });
                await app.StartAsync();
                object state = Activator.CreateInstance(stateType)!;
                using var services = new ServiceCollection().AddSingleton(new ClonerSettings { EnableCloning = false }).BuildServiceProvider();
                using var output = new StringWriter();
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                TextWriter previous = Console.Out;
                try
                {
                    Console.SetOut(output);
                    var task = (Task)runnerType.GetMethod("RunAsync", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, [app.Urls.Single(), services, false, state, cancellation.Token])!;
                    await task;
                }
                finally
                {
                    Console.SetOut(previous);
                    await app.StopAsync();
                }

                string log = output.ToString();
                Checks.Require(requests == 2, $"Warm-up attempted {requests} requests instead of both passes. {log}");
                Checks.Require(log.Contains($"Pipeline warm-up finished: {expected}/2 successful requests.", StringComparison.Ordinal), "Warm-up counted an incomplete HTTP response as successful.");
                Checks.Require(secondCompletes || !log.Contains("Warm-up 2/2 completed.", StringComparison.Ordinal), "An incomplete SSE response was logged as completed.");
            });
    }
}
