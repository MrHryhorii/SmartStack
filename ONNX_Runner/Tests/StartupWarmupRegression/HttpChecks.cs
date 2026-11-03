using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ONNX_Runner.Services;

internal static class HttpChecks
{
    internal static async Task Run(Checks checks, string results)
    {
        Directory.CreateDirectory(results);
        foreach (string profile in new[] { "base-only", "cloning", "cloning-zero-default", "modules-disabled", "missing-model" })
            await checks.RunAsync("HTTP startup, request numbering and rate limit: " + profile, async () =>
            {
                await using var host = await Host.Start(profile, results);
                string startup = host.Text;
                bool missing = profile == "missing-model";
                Checks.Require(startup.Contains($"Pipeline warm-up finished: {(missing ? 0 : 2)}/2 successful requests.", StringComparison.Ordinal), "Both warm-up passes were not attempted/completed as expected.");
                Checks.Require(host.RequestIds.SequenceEqual(new long[] { -1, 0 }), "Startup IDs were not exactly -1 and 0.");
                string expectedVoice = profile.StartsWith("cloning", StringComparison.Ordinal) ? "female" : "piper_base";
                Checks.Require(startup.Contains("[R-0001] Request received", StringComparison.Ordinal) && startup.Contains("[R0000] Request received", StringComparison.Ordinal), "The reserved IDs were not logged.");
                Checks.Require(host.RequestLines.Take(2).All(line => line.Contains("voice=" + expectedVoice, StringComparison.Ordinal)), "Both warm-up passes must reach synthesis with the available cloned voice or base fallback.");
                Checks.Require(missing || Regex.Matches(startup, "Generation completed").Count == 2, "Warm-up success did not complete synthesis.");
                if (!missing)
                {
                    string[] started = host.Lines.Where(line => line.Contains("Generation started |", StringComparison.Ordinal)).ToArray();
                    string clone = profile.StartsWith("cloning", StringComparison.Ordinal) ? "yes" : "no";
                    bool effects = profile != "modules-disabled";
                    Checks.Require(started.Length == 2 && started.All(line => line.Contains("clone=" + clone + " |", StringComparison.Ordinal)), "Warm-up cloning did not honor availability/global settings.");
                    Checks.Require(started[0].Contains("stream=no | stream_format=Audio", StringComparison.Ordinal) && started[1].Contains("stream=yes | stream_format=Sse", StringComparison.Ordinal), "Buffered audio or streaming SSE did not execute.");
                    Checks.Require(started[0].Contains("pitch=1.05", StringComparison.Ordinal) && started[1].Contains("pitch=0.95", StringComparison.Ordinal) && started[0].Contains("sample_rate=24000 Hz", StringComparison.Ordinal) && started[1].Contains("sample_rate=32000 Hz", StringComparison.Ordinal), "Pitch shifting or output-rate overrides did not execute.");
                    Checks.Require(started[0].Contains(effects ? "effect=Telephone | environment=Room" : "effect=None | environment=None", StringComparison.Ordinal) && started[1].Contains(effects ? "effect=FmRadio | environment=InnerVoice" : "effect=None | environment=None", StringComparison.Ordinal), "Active DSP or the effects master switch was lost.");
                }
                if (profile == "cloning")
                {
                    bool Detected(string line) => line.Contains("[LANG-DEBUG]", StringComparison.Ordinal) && (line.Contains("LOCAL DETECTOR", StringComparison.Ordinal) || line.Contains("AMBIGUOUS LOCAL", StringComparison.Ordinal));
                    Checks.Require(host.Lines.Any(line => line.Contains("[R0000]", StringComparison.Ordinal) && Detected(line)), "The automatic warm-up did not exercise Lingua detection.");
                    Checks.Require(!host.Lines.Any(line => line.Contains("[R-0001]", StringComparison.Ordinal) && Detected(line)), "The English warm-up unexpectedly used statistical detection.");
                }
                using var first = await host.Send();
                Checks.Require((int)first.StatusCode == (missing ? 500 : 200), "Warm-up consumed the only public rate-limit permit.");
                byte[] body = await first.Content.ReadAsByteArrayAsync();
                if (!missing)
                    Checks.Require(body.Length > 1024 && first.Content.Headers.ContentType?.MediaType == "audio/pcm" && first.Headers.Contains("X-Audio-Sample-Rate"), "The first user request did not return PCM audio.");
                using var second = await host.Send();
                Checks.Require((int)second.StatusCode == 429, "The public rate limiter was bypassed after startup.");
                await host.WaitFor(() => host.RequestIds.Length == 3, TimeSpan.FromSeconds(5));
                Checks.Require(host.RequestIds.SequenceEqual(new long[] { -1, 0, 1 }), "Failed/successful warm-up changed the first public request ID.");
                await host.Stop();
                Checks.Require(OperatingSystem.IsWindows() || host.ExitCode == 0, "The server did not shut down cleanly.");
                checks.Evidence.Add(new { HttpProfile = profile, WarmupIds = new long[] { -1, 0 }, WarmupVoices = host.RequestLines.Take(2).Select(line => Regex.Match(line, @"voice=(\S+)").Groups[1].Value).ToArray(), GenerationPlans = host.Lines.Where(line => line.Contains("Generation started |", StringComparison.Ordinal)).Take(2).ToArray(), LanguageDetectorExercised = profile == "cloning", WarmupSuccesses = missing ? 0 : 2, FirstUserId = 1, FirstUserStatus = (int)first.StatusCode, SecondUserStatus = (int)second.StatusCode, RateLimitPermits = 1, host.ExitCode, host.LogPath });
            });
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly HttpClient _client;
        private readonly ConcurrentQueue<string> _lines = new();
        private readonly StreamWriter _log;
        private readonly object _logLock = new();
        internal string LogPath { get; }
        internal string Text => string.Join('\n', _lines);
        internal string[] Lines => _lines.ToArray();
        internal string[] RequestLines => _lines.Where(line => line.Contains("Request received |", StringComparison.Ordinal)).ToArray();
        internal long[] RequestIds => RequestLines.Select(line => long.Parse(Regex.Match(line, @"\[R(-?\d+)\]").Groups[1].Value)).ToArray();
        internal int ExitCode => _process.ExitCode;

        private Host(Process process, string address, string logPath)
        {
            _process = process; _client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(90) };
            LogPath = logPath; _log = new StreamWriter(logPath) { AutoFlush = true };
            void Log(object _, DataReceivedEventArgs args)
            {
                if (args.Data == null) return;
                _lines.Enqueue(args.Data);
                lock (_logLock) _log.WriteLine(args.Data);
            }
            process.OutputDataReceived += Log; process.ErrorDataReceived += Log;
        }

        internal static async Task<Host> Start(string profile, string results)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            string address = $"http://127.0.0.1:{port}";
            string server = typeof(SpeechSynthesisService).Assembly.Location;
            string dotnet = Environment.GetEnvironmentVariable("DOTNET_ROOT") is string root ? Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet") : "dotnet";
            var start = new ProcessStartInfo(dotnet) { WorkingDirectory = Path.GetDirectoryName(server)!, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(server);
            var settings = new Dictionary<string, string>
            {
                ["Kestrel:Endpoints:Http:Url"] = address, ["StartupSettings:OpenBrowserOnStart"] = "false",
                ["HardwareSettings:ForcePiperToCpu"] = "true", ["OnnxSettings:Cpu:IntraOpNumThreads"] = "1",
                ["PhonemizerSettings:UseLanguageDetector"] = (profile == "cloning").ToString(), ["ClonerSettings:EnableCloning"] = profile.StartsWith("cloning", StringComparison.Ordinal).ToString(),
                ["ClonerSettings:CloneIntensity"] = profile == "cloning-zero-default" ? "0" : "1",
                ["EffectsSettings:EnableGlobalEffects"] = (profile != "modules-disabled").ToString(),
                ["RateLimitSettings:PermitLimit"] = "1", ["RateLimitSettings:WindowSeconds"] = "600", ["RateLimitSettings:QueueLimit"] = "0",
                ["Logging:LogLevel:Default"] = "Warning", ["Logging:LogLevel:ONNX_Runner.Services.SpeechSynthesisService"] = "Debug",
                ["Logging:LogLevel:ONNX_Runner.Services.Text.Phonemization.MixedLanguagePhonemizer"] = profile == "cloning" ? "Debug" : "Warning"
            };
            if (profile == "missing-model")
            {
                string empty = Path.Combine(results, "empty-models"); Directory.CreateDirectory(empty);
                settings["ModelSettings:ModelDirectory"] = empty;
                settings["ModelSettings:ExactModelFilePath"] = Path.Combine(empty, "missing.onnx");
                settings["ModelSettings:ExactConfigFilePath"] = Path.Combine(empty, "missing.onnx.json");
                settings["ModelSettings:AutoDownloadFallbackModel"] = "false";
            }
            foreach (var (key, value) in settings) { start.ArgumentList.Add("--" + key); start.ArgumentList.Add(value); }
            var process = new Process { StartInfo = start };
            var host = new Host(process, address, Path.Combine(results, profile + ".log"));
            try
            {
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                await host.WaitFor(() => host.Text.Contains("TSUBAKI TTS ENGINE IS READY", StringComparison.Ordinal), TimeSpan.FromSeconds(180));
                Console.WriteLine("HTTP warm-up ready: " + profile);
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }

        internal async Task WaitFor(Func<bool> ready, TimeSpan timeout)
        {
            var elapsed = Stopwatch.StartNew();
            while (!ready())
            {
                if (_process.HasExited) throw new InvalidOperationException("Server exited before the expected event; see " + LogPath);
                if (elapsed.Elapsed > timeout) throw new TimeoutException("Server event timed out; see " + LogPath);
                await Task.Delay(50);
            }
        }

        internal async Task<HttpResponseMessage> Send()
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { input = "The first ordinary speech request.", voice = "piper_base", response_format = "pcm", stream = false, language = "en" }), Encoding.UTF8, "application/json");
            return await _client.PostAsync("/tsbk/audio/speech", content);
        }

        internal async Task Stop()
        {
            if (!_process.HasExited)
            {
                if (OperatingSystem.IsWindows()) _process.Kill(entireProcessTree: true);
                else
                {
                    using var signal = Process.Start(new ProcessStartInfo("/bin/kill") { ArgumentList = { "-INT", _process.Id.ToString() } })!;
                    await signal.WaitForExitAsync();
                }
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (TimeoutException) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); throw; }
            }
            _process.WaitForExit();
        }

        public async ValueTask DisposeAsync()
        {
            try { await Stop(); }
            finally { _client.Dispose(); _process.Dispose(); _log.Dispose(); }
        }
    }
}
