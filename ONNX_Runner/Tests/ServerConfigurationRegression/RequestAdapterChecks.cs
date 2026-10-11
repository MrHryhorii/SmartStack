using System.Text.Json;
using ONNX_Runner.Endpoints;
using ONNX_Runner.Models;

internal static class RequestAdapterChecks
{
    internal static void Run(Action<string, Action> check)
    {
        foreach (var (name, expected) in new (string, AudioFormat)[]
        {
            ("wav", AudioFormat.Wav), ("mp3", AudioFormat.Mp3), ("opus", AudioFormat.Opus),
            ("aac", AudioFormat.Aac), ("flac", AudioFormat.Flac), ("pcm", AudioFormat.Pcm),
            ("b64_json", AudioFormat.B64Json)
        })
        {
            check($"Both adapters accept named format {name}", () =>
            {
                string json = JsonSerializer.Serialize(new { input = "The file is ready.", response_format = $" {name.ToUpperInvariant()} " });
                var openAi = OpenAiRequestAdapter.ToSynthesisRequest(JsonSerializer.Deserialize<OpenAiSpeechRequest>(json)!);
                var tsubaki = TsubakiRequestAdapter.ToSynthesisRequest(JsonSerializer.Deserialize<TsubakiSpeechRequest>(json)!);
                Require(openAi.ValidationError == null && openAi.Request?.Format == expected &&
                    tsubaki.ValidationError == null && tsubaki.Request?.Format == expected, "Named format was rejected or misrouted.");
            });
        }

        foreach (string format in new[] { "99", "0", "-1", "wav, mp3", "unknown", "" })
        {
            check($"Both adapters reject unsupported format '{format}'", () =>
            {
                var openAi = OpenAiRequestAdapter.ToSynthesisRequest(new OpenAiSpeechRequest { Input = "Test.", ResponseFormat = format });
                var tsubaki = TsubakiRequestAdapter.ToSynthesisRequest(new TsubakiSpeechRequest { Input = "Test.", ResponseFormat = format });
                Require(openAi.Request == null && openAi.ValidationError != null &&
                    tsubaki.Request == null && tsubaki.ValidationError != null, "Unsupported enum value reached synthesis.");
            });
        }

        check("Tsubaki JSON preserves all explicit synthesis overrides", () =>
        {
            var dto = JsonSerializer.Deserialize<TsubakiSpeechRequest>("""
                { "input": "The file is ready.", "voice": "female", "speed": 1.5,
                  "response_format": "wav", "stream": false, "stream_format": "sse",
                  "early_split": false, "noise_scale": 0.3, "noise_w": 0.4,
                  "effect": "Telephone", "effect_intensity": 0.6,
                  "environment": "Cave", "environment_intensity": 0.25,
                  "pitch": 1.2, "volume": 0.5, "language": " FR-CA ",
                  "clone_intensity": 0.75, "tone_temperature": 0.7,
                  "low_pass_q_factor": 0.577, "extend_reverb_tail": false }
                """)!;
            var (request, error) = TsubakiRequestAdapter.ToSynthesisRequest(dto);
            Require(error == null && request is not null, "Valid request was rejected.");
            Require(request!.Voice == "female" && request.Speed == 1.5f && request.Format == AudioFormat.Wav &&
                request.Stream == false && request.StreamFormat == SpeechStreamFormat.Sse && request.EarlySplit == false &&
                request.NoiseScale == .3f && request.NoiseW == .4f && request.Effect == "Telephone" && request.EffectIntensity == .6f &&
                request.Environment == "Cave" && request.EnvironmentIntensity == .25f && request.Pitch == 1.2f && request.Volume == .5f &&
                request.Language == "fr-ca" && request.CloneIntensity == .75f && request.ToneTemperature == .7f &&
                request.LowPassQFactor == .577f && request.ExtendReverbTail == false, "An explicit override was dropped or changed.");
        });

        check("Omitted optional overrides remain null for configured defaults", () =>
        {
            var (request, error) = TsubakiRequestAdapter.ToSynthesisRequest(new TsubakiSpeechRequest { Input = "Test." });
            Require(error == null && request is not null && request.Format == AudioFormat.Mp3 &&
                request.Stream == null && request.EarlySplit == null && request.NoiseScale == null && request.NoiseW == null &&
                request.Effect == null && request.EffectIntensity == null && request.Environment == null && request.EnvironmentIntensity == null &&
                request.Pitch == null && request.Volume == null && request.CloneIntensity == null && request.ToneTemperature == null &&
                request.LowPassQFactor == null && request.ExtendReverbTail == null, "Missing fields overrode server configuration.");
        });

        foreach (var (field, low, high, property) in new (string, float, float, string)[]
        {
            ("speed", .25f, 4, nameof(SynthesisRequest.Speed)),
            ("noise_scale", 0, 1, nameof(SynthesisRequest.NoiseScale)), ("noise_w", 0, 1, nameof(SynthesisRequest.NoiseW)),
            ("effect_intensity", 0, 1, nameof(SynthesisRequest.EffectIntensity)),
            ("environment_intensity", 0, 1, nameof(SynthesisRequest.EnvironmentIntensity)),
            ("pitch", .5f, 2, nameof(SynthesisRequest.Pitch)), ("volume", 0, 4, nameof(SynthesisRequest.Volume)),
            ("clone_intensity", 0, 2, nameof(SynthesisRequest.CloneIntensity)),
            ("tone_temperature", .1f, 2, nameof(SynthesisRequest.ToneTemperature)),
            ("low_pass_q_factor", .1f, 1, nameof(SynthesisRequest.LowPassQFactor))
        })
        {
            foreach (var (value, expected) in new[] { (-5f, low), (8f, high) })
            {
                check($"JSON clamps {field}={value} to its declared range", () =>
                {
                    string json = JsonSerializer.Serialize(new Dictionary<string, object> { ["input"] = "Test.", [field] = value });
                    var (request, error) = TsubakiRequestAdapter.ToSynthesisRequest(JsonSerializer.Deserialize<TsubakiSpeechRequest>(json)!);
                    object? actual = typeof(SynthesisRequest).GetProperty(property)!.GetValue(request);
                    Require(error == null && actual is float number && number == expected, "JSON range contract was not preserved by the adapter.");
                });
            }
        }

        foreach (string code in new[] { "auto", " AUTO ", "  " })
        {
            check($"Automatic language selector '{code}' retains configured routing", () =>
            {
                var (request, error) = TsubakiRequestAdapter.ToSynthesisRequest(new TsubakiSpeechRequest { Input = "Test.", Language = code });
                Require(error == null && request?.Language == null, "Automatic routing was replaced with a forced language.");
            });
        }

        foreach (string json in new[] { "\" female \"", "{\"id\":\" female \"}" })
        {
            check($"OpenAI voice JSON {json} resolves the same local voice", () =>
            {
                var dto = JsonSerializer.Deserialize<OpenAiSpeechRequest>($"{{\"input\":\"Test.\",\"voice\":{json}}}")!;
                var (request, error) = OpenAiRequestAdapter.ToSynthesisRequest(dto);
                Require(error == null && request?.Voice == "female", "Voice reference was not normalized.");
            });
        }

        foreach (int limit in new[] { 1, 8, 36, 50 })
        {
            check($"Positive emergency chunk limit {limit} is honored", () =>
            {
                const string text = "A long ordinary sentence contains words and continues through the final clause.";
                var chunks = new ONNX_Runner.Services.TextChunker(new ChunkerSettings { MaxChunkLength = limit }).Split(text);
                Require(chunks.Count > 1 && chunks.All(chunk => chunk.Text.TrimEnd('-').Length <= limit), "Positive limit was silently replaced.");
                Require(chunks.Count(chunk => chunk.IsSentenceFinished) == 1 && chunks[^1].IsSentenceFinished, "Emergency cuts became sentence boundaries.");
            });
        }
        foreach (int limit in new[] { 0, -5 })
        {
            check($"Invalid chunk limit {limit} falls back to the declared default", () =>
            {
                string text = string.Join(' ', Enumerable.Repeat("An ordinary word", 18)) + ".";
                var fallback = new ONNX_Runner.Services.TextChunker(new ChunkerSettings { MaxChunkLength = limit }).Split(text);
                var declared = new ONNX_Runner.Services.TextChunker(new ChunkerSettings()).Split(text);
                Require(fallback.SequenceEqual(declared), "Invalid limit used a different undocumented default.");
            });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
