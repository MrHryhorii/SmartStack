using System.Text;
using System.Text.Json;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Requests;
using ONNX_Runner.Services.Cloning;

namespace ONNX_Runner.Services.Infrastructure;

/// <summary>Primes synthesis, available cloning, DSP and response delivery with two HTTP requests.</summary>
internal static class StartupWarmup
{
    internal sealed record Pass(string Route, object Payload);

    internal static Pass[] CreatePasses(bool mp3Available, IEnumerable<string> voices)
    {
        string format = mp3Available ? "mp3" : "pcm";
        string[] clones = [.. voices.Where(voice => !string.Equals(voice, "piper_base", StringComparison.OrdinalIgnoreCase)).OrderBy(voice => voice, StringComparer.OrdinalIgnoreCase)];
        string clone = clones.FirstOrDefault(voice => string.Equals(voice, "female", StringComparison.OrdinalIgnoreCase))
            ?? clones.FirstOrDefault() ?? "piper_base";
        Pass CreatePass(string text, bool secondPass) => new("/tsbk/audio/speech", new TsubakiSpeechRequest
        {
            Model = "tts-1",
            Input = text,
            Voice = clone,
            ResponseFormat = format,
            SampleRate = secondPass ? 32000 : 24000,
            StreamFormat = secondPass ? "sse" : "audio",
            Instructions = "Read the warm-up passage.",
            Stream = secondPass,
            EarlySplit = !secondPass,
            Speed = secondPass ? .95f : 1.05f,
            NoiseScale = .667f,
            NoiseW = .8f,
            Equalizer = ["handset-class-device", "telephony-class-application"],
            Effect = secondPass ? "FmRadio" : "Telephone",
            EffectIntensity = .5f,
            Environment = secondPass ? "InnerVoice" : "Room",
            EnvironmentIntensity = .35f,
            Pitch = secondPass ? .95f : 1.05f,
            Volume = secondPass ? 1.1f : .8f,
            Language = secondPass ? "auto" : "en",
            CloneIntensity = 1,
            ToneTemperature = .7f,
            LowPassQFactor = .7f,
            ExtendReverbTail = !secondPass
        });
        return
        [
            CreatePass("""Maya checked config.prod.json—twice—and whispered, "No... esto no funciona; but maybe, demain, ça ira?", then added (almost laughing):""", false),
            CreatePass("""Я перевірю ще раз — pero, please, don't restart https://example.com/api?v=2.3.1&mode=fast; if Dr. Smith replies, say "sí, d'accord", otherwise... wait.""", true)
        ];
    }

    internal static async Task RunAsync(string baseUrl, IServiceProvider services, bool mp3Available,
        StartupWarmupState state, CancellationToken cancellationToken)
    {
        int completed = 0;
        try
        {
            var cloner = services.GetRequiredService<ClonerSettings>();
            var openVoice = cloner.EnableCloning ? services.GetService<OpenVoiceRunner>() : null;
            Pass[] passes = CreatePasses(mp3Available, openVoice?.VoiceLibrary.Keys ?? Enumerable.Empty<string>());
            using var client = new HttpClient();
            Console.WriteLine("\n[SYSTEM] Starting two pipeline warm-up requests...");
            for (int pass = 0; pass < passes.Length; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + passes[pass].Route);
                    request.Headers.Add(StartupWarmupState.HeaderName, state.HeaderForPass(pass));
                    request.Content = new StringContent(JsonSerializer.Serialize(passes[pass].Payload), Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    await response.Content.CopyToAsync(Stream.Null, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[WARNING] Warm-up {pass + 1}/2 returned HTTP {(int)response.StatusCode}.");
                        continue;
                    }
                    completed++;
                    Console.WriteLine($"[SYSTEM] Warm-up {pass + 1}/2 completed.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) { Console.WriteLine($"[WARNING] Warm-up {pass + 1}/2 failed: {error.Message}"); }
            }
            Console.WriteLine($"[SYSTEM] Pipeline warm-up finished: {completed}/2 successful requests.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { Console.WriteLine($"[WARNING] Pipeline warm-up skipped: {error.Message}"); }
        finally { state.Complete(); }
    }
}
