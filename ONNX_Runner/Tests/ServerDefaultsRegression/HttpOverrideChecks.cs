using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ONNX_Runner.Models.Requests;
using static TestRun;

internal static class HttpOverrideChecks
{
    private const string Text = "The server reads this short sentence.";

    internal static async Task Run(TestRun run, string results)
    {
        Directory.CreateDirectory(results);
        string serverDll = typeof(SynthesisRequest).Assembly.Location;
        string configPath = Path.Combine(results, "deterministic-model.onnx.json");
        JsonObject model = JsonNode.Parse(File.ReadAllText("Model/en_US-hfc_female-medium.onnx.json"))!.AsObject();
        model["inference"]!["noise_scale"] = 0;
        model["inference"]!["noise_w"] = 0;
        File.WriteAllText(configPath, model.ToJsonString());

        await using (var host = await Host.Start("shipped", serverDll, configPath, results, new()))
        {
            await run.CheckAsync("HTTP shipped preset is bound without changing fallback classes", async () =>
            {
                JsonElement status = await host.Status();
                Require(status.GetProperty("dsp").GetProperty("defaultVolume").GetSingle() == 1 && status.GetProperty("dsp").GetProperty("volumeBoosterDb").GetSingle() == 8 && status.GetProperty("streaming").GetProperty("enabled").GetBoolean(), "The shipped preset was not active.");
            });
            Outcome dry = await host.Send(Payload());
            await run.CheckAsync("HTTP omitted stream uses the shipped delivery default", async () =>
            {
                var payload = Payload(); payload.Remove("stream");
                Outcome response = await host.Send(payload);
                Require(response.Chunked && response.Audio.SequenceEqual(dry.Audio), "Omission lost the enabled streaming default.");
            });
            await run.CheckAsync("HTTP explicit shipped defaults preserve PCM", async () =>
            {
                var payload = Payload();
                payload["pitch"] = 1; payload["volume"] = 1; payload["samplerate"] = 0;
                payload["effect"] = "None"; payload["environment"] = "None"; payload["equalizer"] = Array.Empty<string>();
                Require((await host.Send(payload)).Audio.SequenceEqual(dry.Audio), "Explicit valid defaults changed audio.");
            });
            await run.CheckAsync("HTTP OpenAI ignores Tsubaki-only controls and uses configured defaults", async () =>
            {
                var payload = Payload(); payload["volume"] = 0; payload["samplerate"] = 8000; payload["effect"] = "Telephone";
                Require((await host.Send(payload, "/v1/audio/speech")).Audio.SequenceEqual(dry.Audio), "OpenAI extensions bypassed its adapter boundary.");
            });
            foreach (string format in new[] { "mp3", "wav", "pcm", "opus", "aac", "flac", "b64_json" })
                await run.CheckAsync("HTTP response format overrides MP3 default: " + format, async () =>
                {
                    var payload = Payload(); payload["response_format"] = format;
                    Outcome response = await host.Send(payload);
                    Require(response.Audio.Length > 40 && response.Rate > 0 && response.Codec.Length > 0, "Response codec/rate metadata or audio is missing.");
                    if (format == "wav") Require(response.Audio.AsSpan(0, 4).SequenceEqual("RIFF"u8), "WAV was not selected.");
                    if (format == "pcm") Require(response.Audio.SequenceEqual(dry.Audio), "PCM representation changed audio.");
                });
            foreach (bool streaming in new[] { false, true })
            foreach (string framing in new[] { "audio", "sse" })
                await run.CheckAsync($"HTTP framing/timing override: stream={streaming}, framing={framing}", async () =>
                {
                    var payload = Payload(); payload["stream"] = streaming; payload["stream_format"] = framing;
                    Outcome response = await host.Send(payload);
                    Require(response.Audio.SequenceEqual(dry.Audio), "Framing or streaming changed PCM.");
                    if (framing == "sse") Require(response.ContentType == "text/event-stream", "SSE content type was lost.");
                    if (framing == "audio") Require(response.Chunked == streaming, "Explicit streaming choice did not reach HTTP transport.");
                });
            await run.CheckAsync("HTTP omitted response_format retains MP3", async () =>
            {
                var payload = Payload(); payload.Remove("response_format");
                Outcome response = await host.Send(payload);
                Require(response.ContentType == "audio/mpeg", "Omission lost the MP3 wire default.");
            });
            await run.CheckAsync("HTTP invalid format/framing still returns its default representation", async () =>
            {
                var expected = Payload(); expected["response_format"] = "mp3";
                Outcome mp3 = await host.Send(expected);
                var invalid = Payload(); invalid["response_format"] = new { invalid = true }; invalid["stream_format"] = "unknown";
                Outcome fallback = await host.Send(invalid);
                Require(fallback.ContentType == "audio/mpeg" && fallback.Audio.SequenceEqual(mp3.Audio), "Unusable format or framing prevented default speech.");
            });
            await run.CheckAsync("HTTP schema preserves typed controls and required input", async () =>
            {
                JsonElement document = await host.Document();
                JsonElement Schema(string route)
                {
                    JsonElement schema = document.GetProperty("paths").GetProperty(route).GetProperty("post").GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
                    if (schema.TryGetProperty("$ref", out JsonElement reference)) schema = document.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1]);
                    return schema;
                }
                JsonElement extended = Schema("/tsbk/audio/speech"), openAi = Schema("/v1/audio/speech");
                Require(extended.GetProperty("required").EnumerateArray().Any(value => value.GetString() == "input") && extended.GetProperty("properties").GetProperty("volume").GetRawText().Contains("number", StringComparison.Ordinal), "Tolerant body reading damaged the documented input/number schema.");
                Require(!openAi.GetProperty("properties").TryGetProperty("volume", out _), "The OpenAI schema exposed engine extensions.");
            });
            foreach (string json in new[] { "{}", "{\"input\":null}", "{\"input\":\" \"}", "{\"input\":42}", "{\"input\":\"Test.\",\"pitch\":[}" })
                await run.CheckAsync("HTTP cannot synthesize unreadable required input: " + json, async () => Require((await host.Raw(json)).Status == 400, "Missing text or malformed JSON was not rejected."));
            run.Evidence.Add(new { HttpProfile = "shipped", host.Successes, host.RejectedInput, host.Requests });
        }

        var configured = new Dictionary<string, string>
        {
            ["DspSettings:DefaultPitch"] = "1.15", ["DspSettings:DefaultVolume"] = "0.35", ["DspSettings:VolumeBoosterDb"] = "0", ["DspSettings:DefaultSampleRate"] = "16000",
            ["EffectsSettings:DefaultEffect"] = "Telephone", ["EffectsSettings:DefaultIntensity"] = "0.4", ["EffectsSettings:DefaultEnvironment"] = "Muffled", ["EffectsSettings:DefaultEnvironmentIntensity"] = "0.6",
            ["EffectsSettings:DefaultEqualizer:0"] = "handset-class-device", ["EffectsSettings:ExtendReverbTailOnFinish"] = "false",
            ["StreamSettings:EnableStreaming"] = "false", ["ChunkerSettings:EarlySplit"] = "false", ["ChunkerSettings:SentencePauseSeconds"] = "0.15"
        };
        await using (var host = await Host.Start("configured", serverDll, configPath, results, configured))
        {
            string before = (await host.Status()).GetRawText();
            Outcome defaults = await host.Send(Payload());
            await run.CheckAsync("HTTP omitted stream uses the configured delivery default", async () =>
            {
                var payload = Payload(); payload.Remove("stream");
                Outcome response = await host.Send(payload);
                Require(!response.Chunked && response.Audio.SequenceEqual(defaults.Audio), "Omission lost the disabled streaming default.");
            });
            await run.CheckAsync("HTTP CLI overrides environment and JSON defaults", async () =>
            {
                JsonElement status = await host.Status();
                Require(status.GetProperty("dsp").GetProperty("defaultVolume").GetSingle() == .35f && !status.GetProperty("streaming").GetProperty("enabled").GetBoolean() && defaults.Rate == 16000, "Provider precedence or configured output rate was lost.");
            });
            await run.CheckAsync("HTTP inherited custom settings equal explicit valid settings", async () =>
            {
                var payload = Payload(); payload["pitch"] = 1.15; payload["volume"] = .35; payload["samplerate"] = 16000;
                payload["effect"] = "Telephone"; payload["effect_intensity"] = .4; payload["environment"] = "Muffled"; payload["environment_intensity"] = .6;
                payload["equalizer"] = new[] { "handset-class-device" }; payload["extend_reverb_tail"] = false; payload["early_split"] = false;
                Require((await host.Send(payload)).Audio.SequenceEqual(defaults.Audio), "Configured values did not reach the same native pipeline as request overrides.");
            });
            foreach (string field in new[] { "pitch", "volume", "speed", "noise_scale", "noise_w", "effect_intensity", "environment_intensity", "clone_intensity", "tone_temperature", "low_pass_q_factor" })
            foreach (object value in new object[] { "NaN", "Infinity", "invalid", true, new { bad = new[] { 1, 2 } } })
                await run.CheckAsync($"HTTP ignores one invalid field and still speaks: {field}={JsonSerializer.Serialize(value)}", async () =>
                {
                    var payload = Payload(); payload[field] = value;
                    Require((await host.Send(payload)).Audio.SequenceEqual(defaults.Audio), "Invalid optional value did not retain the effective defaults.");
                });
            foreach (var (field, value) in new (string, object)[]
            {
                ("samplerate", "8000"), ("samplerate", 8.5), ("stream", "false"), ("early_split", 0),
                ("extend_reverb_tail", "true"), ("effect", "unknown"), ("environment", "999"),
                ("equalizer", new[] { "unknown" }), ("equalizer", new { bad = true }), ("voice", new { bad = true })
            })
                await run.CheckAsync($"HTTP optional shape/name fallback: {field}", async () =>
                {
                    var payload = Payload(); payload[field] = value;
                    Require((await host.Send(payload)).Audio.SequenceEqual(defaults.Audio), "Unusable field displaced another control or its configured default.");
                });
            await run.CheckAsync("HTTP invalid field preserves independent valid override", async () =>
            {
                var payload = Payload(); payload["volume"] = 0; payload["pitch"] = new { invalid = true };
                Require((await host.Send(payload)).Audio.All(sample => sample == 0), "Bad pitch discarded explicit mute.");
            });
            foreach (int rate in new[] { 0, 8000, 48000 })
                await run.CheckAsync("HTTP explicit output clock overrides server 16000: " + rate, async () =>
                {
                    var payload = Payload(); payload["samplerate"] = rate;
                    Require((await host.Send(payload)).Rate == (rate == 0 ? 22050 : rate), "Output clock override or zero bypass was lost.");
                });
            await run.CheckAsync("HTTP true overrides disabled streaming default", async () =>
            {
                var payload = Payload(); payload["stream"] = true;
                Outcome response = await host.Send(payload);
                Require(response.Chunked && response.Audio.SequenceEqual(defaults.Audio), "Explicit streaming did not override the disabled delivery default.");
            });
            await run.CheckAsync("HTTP parallel overrides remain request-local", async () =>
            {
                var mute = Payload(); mute["volume"] = 0;
                Outcome silent = await host.Send(mute);
                Outcome[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => host.Send(index % 2 == 0 ? mute : Payload())));
                Require(results.Select((result, index) => result.Audio.SequenceEqual(index % 2 == 0 ? silent.Audio : defaults.Audio)).All(value => value), "Concurrent requests shared override state.");
                Require((await host.Status()).GetRawText() == before, "Request overrides changed stored server settings.");
            });
            run.Evidence.Add(new { HttpProfile = "configured", host.Successes, host.RejectedInput, host.Requests });
        }

        configured = new()
        {
            ["ClonerSettings:EnableCloning"] = "true", ["ClonerSettings:CloneIntensity"] = "0.65", ["ClonerSettings:ToneTemperature"] = "0.6",
            ["EffectsSettings:EnableGlobalEffects"] = "false", ["EffectsSettings:DefaultEffect"] = "Telephone", ["EffectsSettings:DefaultEnvironment"] = "Echo", ["EffectsSettings:DefaultEqualizer:0"] = "handset-class-device",
            ["DspSettings:DefaultVolume"] = "0.5", ["DspSettings:VolumeBoosterDb"] = "0", ["DspSettings:DefaultSampleRate"] = "16000", ["ChunkerSettings:SentencePauseSeconds"] = "0"
        };
        await using (var host = await Host.Start("cloning-and-gates", serverDll, configPath, results, configured))
        {
            var clean = Payload(); clean["samplerate"] = 0;
            Outcome dry = await host.Send(clean);
            await run.CheckAsync("HTTP disabled effects block request layers while cloning stays independent", async () =>
            {
                var payload = Payload(); payload["samplerate"] = 0; payload["effect"] = "Overdrive"; payload["environment"] = "ConcreteHall";
                payload["equalizer"] = new[] { "telephony-class-application" }; payload["extend_reverb_tail"] = true; payload["enable_global_effects"] = true;
                Require((await host.Send(payload)).Audio.SequenceEqual(dry.Audio), "A request bypassed the effects master switch.");
                payload = Payload(); payload["voice"] = " alloy "; payload["samplerate"] = 0;
                Outcome cloned = await host.Send(payload);
                Require(!cloned.Audio.SequenceEqual(dry.Audio) && cloned.Audio.Length > 1000, "Voice normalization, cloning default or independent cloning was lost.");
            });
            foreach (object value in new object[] { 0, "NaN", new { invalid = true } })
                await run.CheckAsync("HTTP clone-intensity zero/fallback control: " + JsonSerializer.Serialize(value), async () =>
                {
                    var payload = Payload(); payload["voice"] = "alloy"; payload["samplerate"] = 0; payload["clone_intensity"] = value;
                    Outcome result = await host.Send(payload);
                    Require(value is int ? result.Audio.SequenceEqual(dry.Audio) : !result.Audio.SequenceEqual(dry.Audio), "Zero bypass or inherited cloning intensity was lost.");
                });
            await run.CheckAsync("HTTP invalid OpenAI custom voice shape still returns base speech", async () =>
            {
                var payload = Payload(); payload["voice"] = new { id = 4 }; payload["samplerate"] = 0;
                var baseAtDefaultRate = Payload();
                Outcome fallback = await host.Send(payload, "/v1/audio/speech");
                Outcome expected = await host.Send(baseAtDefaultRate);
                Require(fallback.Audio.SequenceEqual(expected.Audio), "Invalid OpenAI voice prevented base speech.");
            });
            run.Evidence.Add(new { HttpProfile = "cloning-and-gates", host.Successes, host.RejectedInput, host.Requests });
        }
        foreach (var (path, value) in new[] { ("DspSettings:DefaultVolume", "NaN"), ("RateLimitSettings:PermitLimit", "0"), ("StreamSettings:MinChunkSizeKb", int.MaxValue.ToString()), ("ChunkerSettings:SentencePauseSeconds", "-1") })
            await run.CheckAsync("Invalid server configuration fails before listening: " + path, async () =>
            {
                await using var host = await Host.Start("invalid-" + path.Replace(':', '-'), serverDll, configPath, results, new() { [path] = value }, expectFailure: true);
                Require(host.ExitCode != 0 && File.ReadAllText(host.LogPath).Contains(path, StringComparison.Ordinal), "Invalid server defaults did not produce an actionable startup error.");
            });
    }

    private static Dictionary<string, object?> Payload() => new() { ["input"] = Text, ["response_format"] = "pcm", ["stream"] = false };
    private sealed record Outcome(int Status, byte[] Audio, int Rate, string Codec, string ContentType, bool Chunked);

    private sealed class Host : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly HttpClient _client;
        private readonly StreamWriter _log;
        private readonly object _logLock = new();
        internal string LogPath { get; }
        private int _successes, _rejectedInput, _requests;
        internal int Successes => Volatile.Read(ref _successes);
        internal int RejectedInput => Volatile.Read(ref _rejectedInput);
        internal int Requests => Volatile.Read(ref _requests);
        internal int ExitCode => _process.ExitCode;

        private Host(Process process, HttpClient client, string logPath)
        {
            _process = process; _client = client; LogPath = logPath; _log = new StreamWriter(logPath) { AutoFlush = true };
            void Log(object _, DataReceivedEventArgs args) { if (args.Data != null) lock (_logLock) _log.WriteLine(args.Data); }
            process.OutputDataReceived += Log; process.ErrorDataReceived += Log;
        }

        internal static async Task<Host> Start(string name, string serverDll, string modelConfig, string results, Dictionary<string, string> overrides, bool expectFailure = false)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            string address = $"http://127.0.0.1:{port}";
            string dotnet = System.Environment.GetEnvironmentVariable("DOTNET_ROOT") is string root ? Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet") : "dotnet";
            var start = new ProcessStartInfo(dotnet) { WorkingDirectory = Path.GetDirectoryName(serverDll)!, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(serverDll);
            var settings = new Dictionary<string, string>
            {
                ["Kestrel:Endpoints:Http:Url"] = address, ["StartupSettings:OpenBrowserOnStart"] = "false",
                ["HardwareSettings:ForcePiperToCpu"] = "true", ["OnnxSettings:Cpu:IntraOpNumThreads"] = "1",
                ["PhonemizerSettings:UseLanguageDetector"] = "false", ["ClonerSettings:EnableCloning"] = "false", ["RateLimitSettings:PermitLimit"] = "100000",
                ["Logging:LogLevel:Default"] = "Warning", ["ModelSettings:ExactModelFilePath"] = Path.GetFullPath("Model/en_US-hfc_female-medium.onnx"), ["ModelSettings:ExactConfigFilePath"] = modelConfig
            };
            settings["environment"] = name == "shipped" ? "Development" : "Production";
            foreach (var setting in overrides) settings[setting.Key] = setting.Value;
            foreach (var (key, value) in settings) { start.ArgumentList.Add("--" + key); start.ArgumentList.Add(value); }
            if (name == "configured") { start.Environment["DspSettings__DefaultVolume"] = "0.9"; start.Environment["StreamSettings__EnableStreaming"] = "true"; }
            var process = new Process { StartInfo = start };
            var host = new Host(process, new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(90) }, Path.Combine(results, name + ".log"));
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            try
            {
                if (expectFailure) { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); return host; }
                for (int attempt = 0; attempt < 900; attempt++)
                {
                    if (process.HasExited) throw new InvalidOperationException("The HTTP fixture exited before readiness: " + name);
                    try { if ((await host.Status()).GetProperty("synthesisReady").GetBoolean()) { Console.WriteLine("HTTP ready: " + name); return host; } }
                    catch (HttpRequestException) { }
                    await Task.Delay(100);
                }
                throw new TimeoutException("HTTP fixture readiness timed out: " + name);
            }
            catch { await host.DisposeAsync(); throw; }
        }

        internal async Task<JsonElement> Status()
        {
            using var response = await _client.GetAsync("/tsbk/server/status"); response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync()); return json.RootElement.Clone();
        }
        internal async Task<JsonElement> Document()
        {
            using var response = await _client.GetAsync("/openapi/v1.json"); response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync()); return json.RootElement.Clone();
        }
        internal async Task<Outcome> Send(Dictionary<string, object?> payload, string route = "/tsbk/audio/speech")
        {
            Outcome result = await Raw(JsonSerializer.Serialize(payload), route);
            string detail = result.Status == 200 ? "" : Encoding.UTF8.GetString(result.Audio);
            Require(result.Status == 200 && result.Audio.Length > 0, $"Expected speech, got HTTP {result.Status}: {detail[..Math.Min(200, detail.Length)]}");
            return result;
        }
        internal async Task<Outcome> Raw(string json, string route = "/tsbk/audio/speech")
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _client.PostAsync(route, content); Interlocked.Increment(ref _requests);
            byte[] audio = await response.Content.ReadAsByteArrayAsync();
            string type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _successes);
                if (type == "text/event-stream")
                {
                    using var decoded = new MemoryStream(); bool done = false;
                    foreach (string line in Encoding.UTF8.GetString(audio).Split('\n'))
                    {
                        if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                        using var item = JsonDocument.Parse(line.AsMemory(6)); JsonElement root = item.RootElement;
                        if (root.GetProperty("type").GetString() == "speech.audio.done") { done = true; continue; }
                        string field = root.TryGetProperty("audio", out _) ? "audio" : "audioContent";
                        decoded.Write(Convert.FromBase64String(root.GetProperty(field).GetString()!));
                    }
                    Require(done, "SSE did not complete."); audio = decoded.ToArray();
                }
                else if (type == "application/json")
                {
                    using var item = JsonDocument.Parse(audio); audio = Convert.FromBase64String(item.RootElement.GetProperty("audioContent").GetString()!);
                }
            }
            else if ((int)response.StatusCode == 400) Interlocked.Increment(ref _rejectedInput);
            int rate = response.Headers.TryGetValues("X-Audio-Sample-Rate", out var rates) && int.TryParse(rates.First(), out int value) ? value : 0;
            string codec = response.Headers.TryGetValues("X-Audio-Codec", out var codecs) ? codecs.First() : "";
            return new((int)response.StatusCode, audio, rate, codec, type, response.Headers.TransferEncodingChunked == true);
        }
        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                if (OperatingSystem.IsWindows()) _process.Kill(entireProcessTree: true);
                else
                {
                    using var signal = Process.Start(new ProcessStartInfo("/bin/kill") { ArgumentList = { "-INT", _process.Id.ToString() } })!;
                    await signal.WaitForExitAsync();
                }
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
            }
            _process.WaitForExit(); _process.Dispose(); _client.Dispose(); _log.Dispose();
        }
    }
}
