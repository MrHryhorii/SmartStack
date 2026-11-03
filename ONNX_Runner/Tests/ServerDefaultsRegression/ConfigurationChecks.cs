using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using ONNX_Runner.Endpoints;
using ONNX_Runner.Models.Audio;
using ONNX_Runner.Models.Configuration;
using ONNX_Runner.Models.Requests;
using static TestRun;

internal static class ConfigurationChecks
{
    private static readonly (string Json, string Property, float Min, float Max)[] Floats =
    [
        ("speed", nameof(SynthesisRequest.Speed), .25f, 4),
        ("noise_scale", nameof(SynthesisRequest.NoiseScale), 0, 1),
        ("noise_w", nameof(SynthesisRequest.NoiseW), 0, 1),
        ("effect_intensity", nameof(SynthesisRequest.EffectIntensity), 0, 1),
        ("environment_intensity", nameof(SynthesisRequest.EnvironmentIntensity), 0, 1),
        ("pitch", nameof(SynthesisRequest.Pitch), .5f, 2),
        ("volume", nameof(SynthesisRequest.Volume), 0, 4),
        ("clone_intensity", nameof(SynthesisRequest.CloneIntensity), 0, 2),
        ("tone_temperature", nameof(SynthesisRequest.ToneTemperature), .1f, 2),
        ("low_pass_q_factor", nameof(SynthesisRequest.LowPassQFactor), .1f, 1)
    ];
    private static readonly Type[] SettingsTypes =
    [
        typeof(ModelSettings), typeof(ApiSettings), typeof(StartupSettings), typeof(CorsSettings),
        typeof(PhonemizerSettings), typeof(ChunkerSettings), typeof(HardwareSettings), typeof(OnnxSettings),
        typeof(DspSettings), typeof(StreamSettings), typeof(EffectsSettings), typeof(ClonerSettings), typeof(RateLimitSettings)
    ];

    internal static void Run(TestRun run)
    {
        foreach (var field in Floats)
        {
            foreach (float value in new[] { -10, field.Min, (field.Min + field.Max) / 2, field.Max, 100 })
                run.Check($"HTTP JSON finite range {field.Json}={value}", () =>
                {
                    var request = Adapt($"{{\"input\":\"Test.\",\"{field.Json}\":{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
                    Require((float)typeof(SynthesisRequest).GetProperty(field.Property)!.GetValue(request)! == Math.Clamp(value, field.Min, field.Max), "Finite request range changed.");
                });
            run.Check($"HTTP JSON numeric string compatibility: {field.Json}", () =>
            {
                var request = Adapt($"{{\"input\":\"Test.\",\"{field.Json}\":\"0.5\"}}");
                Require((float)typeof(SynthesisRequest).GetProperty(field.Property)!.GetValue(request)! == Math.Clamp(.5f, field.Min, field.Max), "A finite numeric string lost HTTP compatibility.");
            });
            foreach (string value in new[] { "\"NaN\"", "\"Infinity\"", "\"-Infinity\"", "1e999", "-1e999", "\"invalid\"", "true", "{}", "[1,2]", "null" })
                run.Check($"HTTP JSON ignores unusable optional {field.Json}={value}", () =>
                {
                    var request = Adapt($"{{\"input\":\"Test.\",\"voice\":\"alloy\",\"stream\":false,\"{field.Json}\":{value}}}");
                    object? actual = typeof(SynthesisRequest).GetProperty(field.Property)!.GetValue(request);
                    Require(field.Json == "speed" ? actual is 1f : actual == null, "An unusable optional number displaced its default.");
                    Require(request.Voice == "alloy" && request.Stream == false, "Ignoring one field discarded another valid override.");
                });
            if (field.Json == "speed") continue;
            foreach (string suffix in new[] { "", $",\"{field.Json}\":null" })
                run.Check($"HTTP JSON preserves inheritance: {field.Json}, explicitNull={suffix.Length != 0}", () =>
                    Require(typeof(SynthesisRequest).GetProperty(field.Property)!.GetValue(Adapt("{\"input\":\"Test.\"" + suffix + "}")) == null, "An omitted/null override became a concrete value."));
        }
        foreach (string value in new[] { "\"NaN\"", "\"Infinity\"", "\"-Infinity\"", "1e999", "-1e999", "{}", "null" })
            run.Check($"OpenAI HTTP JSON ignores unusable speed={value}", () =>
                Require(Read<OpenAiSpeechRequest>($"{{\"input\":\"Test.\",\"speed\":{value}}}").Speed == 1, "Invalid speed did not retain the normal rate."));
        foreach (string field in new[] { "stream", "early_split", "extend_reverb_tail" })
        foreach (string value in new[] { "false", "true", "null" })
            run.Check($"HTTP JSON boolean override {field}={value}", () =>
            {
                string property = field switch { "stream" => nameof(SynthesisRequest.Stream), "early_split" => nameof(SynthesisRequest.EarlySplit), _ => nameof(SynthesisRequest.ExtendReverbTail) };
                object? actual = typeof(SynthesisRequest).GetProperty(property)!.GetValue(Adapt($"{{\"input\":\"Test.\",\"{field}\":{value}}}"));
                Require(value == "null" ? actual == null : actual is bool flag && flag == (value == "true"), "Boolean null/false/true collapsed together.");
            });
        foreach (string json in new[] { "\"8.5\"", "8.5", "true", "1e999" })
            run.Check("Output rate ignores non-integer JSON: " + json, () => Require(Adapt($"{{\"input\":\"Test.\",\"samplerate\":{json}}}").SampleRate == null, "Invalid output rate replaced the configured default."));
        foreach (var (value, expected) in new (int, int)[] { (-1, 8000), (0, 0), (1, 8000), (22050, 22050), (96000, 48000) })
            run.Check($"Output rate explicit boundary {value}", () => Require(Adapt($"{{\"input\":\"Test.\",\"samplerate\":{value}}}").SampleRate == expected, "Output rate sentinel or clamp changed."));
        run.Check("OpenAI extensions cannot override server-only controls", () =>
        {
            var dto = Read<OpenAiSpeechRequest>("""
                {"input":"Test.","volume":0,"effect":"None","environment":"None","samplerate":8000,
                 "early_split":false,"clone_intensity":0,"enable_global_effects":true}
                """);
            var request = OpenAiRequestAdapter.ToSynthesisRequest(dto).Request!;
            Require(request.Volume == null && request.Effect == null && request.Environment == null && request.SampleRate == null && request.EarlySplit == null && request.CloneIntensity == null, "An unsupported extension bypassed the OpenAI adapter boundary.");
        });
        foreach (string voice in new[] { " alloy ", "  ", "piper_base" })
            run.Check("Tsubaki voice normalization: '" + voice + "'", () =>
                Require(Adapt(JsonSerializer.Serialize(new { input = "Test.", voice })).Voice == (string.IsNullOrWhiteSpace(voice) ? "piper_base" : voice.Trim()), "Tsubaki and OpenAI disagree on the normalized voice ID."));

        using ConfigurationRoot shipped = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(Path.GetFullPath("appsettings.json")).Build();
        using ConfigurationRoot missing = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(new MemoryStream("{}"u8.ToArray())).Build();
        foreach (Type type in SettingsTypes)
        {
            run.Check("Absent configuration section retains declared defaults: " + type.Name, () =>
            {
                object declared = Activator.CreateInstance(type)!;
                object actual = missing.GetSection(type.Name).Get(type) ?? Activator.CreateInstance(type)!;
                Require(JsonSerializer.Serialize(actual, type) == JsonSerializer.Serialize(declared, type), "Missing section changed constructor defaults.");
            });
            object defaults = Activator.CreateInstance(type)!;
            object resolved = shipped.GetSection(type.Name).Get(type) ?? Activator.CreateInstance(type)!;
            foreach (PropertyInfo property in type.GetProperties().Where(property => property.CanWrite))
            {
                object? fallback = property.GetValue(defaults), preset = property.GetValue(resolved);
                run.Evidence.Add(new { Setting = type.Name + ":" + property.Name, DeclaredFallback = fallback, ShippedPreset = preset });
            }
        }
        foreach (string json in new[] { "{}", "{\"OnnxSettings\":{\"Cpu\":{}}}", "{\"OnnxSettings\":{\"Cpu\":null,\"Gpu\":null}}", "{\"OnnxSettings\":{\"Gpu\":{\"InterOpNumThreads\":2}}}" })
            run.Check("ONNX profiles retain CPU/GPU role defaults: " + json, () =>
            {
                using var config = JsonConfig(json);
                var settings = config.GetSection("OnnxSettings").Get<OnnxSettings>() ?? new();
                Require(settings.Cpu is { IntraOpNumThreads: 4, EnableCpuMemArena: true } && settings.Gpu is { IntraOpNumThreads: 1, EnableCpuMemArena: false }, "Partial/null profile reset role-specific defaults.");
            });
        run.Check("Shipped preset remains distinct from no-file fallback", () =>
        {
            var dsp = shipped.GetSection("DspSettings").Get<DspSettings>()!;
            var cloner = shipped.GetSection("ClonerSettings").Get<ClonerSettings>()!;
            Require(dsp.LowPassCutoffFrequency == 11000 && dsp.LowPassQFactor == .577f && dsp.VolumeBoosterDb == 8 && cloner.ToneTemperature == .7f, "Accepted shipped defaults changed.");
            Require(new DspSettings() is { LowPassCutoffFrequency: 7500, LowPassQFactor: .707f, VolumeBoosterDb: 0 } && new ClonerSettings().ToneTemperature == 1, "Constructor fallback changed silently.");
        });
        ProviderPriority(run);
        ResponseDecisions(run);
        ValidationContracts(run, shipped);
        OptionalFallbacks(run);
        TextAndScalarConversions(run);
    }

    private static void OptionalFallbacks(TestRun run)
    {
        foreach (string field in new[] { "effect", "environment" })
        foreach (string value in new[] { "\"unknown\"", "\"999\"", "\"1\"", "true", "{}", "[]", "\"\"" })
            run.Check($"Unusable {field} inherits its configured selection: {value}", () =>
            {
                var request = Adapt($"{{\"input\":\"Test.\",\"{field}\":{value},\"volume\":0.5}}");
                Require((field == "effect" ? request.Effect : request.Environment) == null && request.Volume == .5f, "An invalid effect bypassed its default or lost another valid field.");
            });
        foreach (string field in new[] { "effect", "environment" })
            run.Check("Explicit None still disables " + field, () =>
            {
                var request = Adapt($"{{\"input\":\"Test.\",\"{field}\":\" none \"}}");
                Require((field == "effect" ? request.Effect : request.Environment) == "None", "Explicit bypass became inheritance.");
            });
        foreach (string field in new[] { "stream", "early_split", "extend_reverb_tail" })
        foreach (string value in new[] { "\"invalid\"", "0", "{}", "[]" })
            run.Check($"Unusable boolean inherits {field}: {value}", () =>
            {
                var request = Adapt($"{{\"input\":\"Test.\",\"{field}\":{value}}}");
                Require((field == "stream" ? request.Stream : field == "early_split" ? request.EarlySplit : request.ExtendReverbTail) == null, "An unusable boolean displaced its default.");
            });
        foreach (string value in new[] { "\"unknown\"", "{}", "[]", "false" })
            run.Check("Invalid output choices retain MP3/audio defaults: " + value, () =>
            {
                var request = Adapt($"{{\"input\":\"Test.\",\"response_format\":{value},\"stream_format\":{value}}}");
                Require(request.Format == AudioFormat.Mp3 && request.StreamFormat == SpeechStreamFormat.Audio, "Invalid format/framing did not retain wire defaults.");
            });
        foreach (string value in new[] { "{}", "false", "3", "\"headphone-class-device\"", "[false]", "[\"unknown\"]", "[null]" })
            run.Check("Unusable profile list inherits configured profiles: " + value, () => Require(Adapt($"{{\"input\":\"Test.\",\"equalizer\":{value}}}").Equalizer == null, "Unusable profiles bypassed configured profiles."));
        run.Check("Empty profile array remains an explicit bypass", () => Require(Adapt("{\"input\":\"Test.\",\"equalizer\":[]}").Equalizer is { Length: 0 }, "Empty array became inheritance."));
        foreach (string value in new[] { "false", "3", "[]", "{}", "{\"id\":3}" })
            run.Check("Unusable OpenAI voice retains the base voice: " + value, () =>
            {
                var dto = Read<OpenAiSpeechRequest>($"{{\"input\":\"Test.\",\"voice\":{value}}}");
                Require(OpenAiRequestAdapter.ToSynthesisRequest(dto).Request?.Voice == "piper_base", "Invalid custom voice displaced its default.");
            });
        foreach (string json in new[] { "{}", "{\"input\":null}", "{\"input\":\" \"}", "{\"input\":\"Test.\",\"pitch\":[}" })
            run.Check("Required text/JSON remains readable: " + json, () => RejectJson(() => Adapt(json)));
    }

    private static void TextAndScalarConversions(TestRun run)
    {
        foreach (string value in new[] { "123", "-2.5", "true", "false", "0", "[]", "{}", "[\"hello\",2]", "{\"message\":\"hello\"}", "\"Line one\\nLine two\"" })
            run.Check("Input normalizes to text in both APIs: " + value, () =>
            {
                using var document = JsonDocument.Parse(value);
                string expected = document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString()! : value;
                string json = "{\"input\":" + value + ",\"volume\":0.5}";
                Require(Adapt(json).Input == expected && Read<OpenAiSpeechRequest>(json).Input == expected, "Input text conversion changed the value.");
                Require(Adapt(json).Volume == .5f, "Text conversion discarded a valid optional field.");
            });
        foreach (string field in new[] { "stream", "early_split", "extend_reverb_tail" })
        foreach (string value in new[] { "true", "FALSE" })
            run.Check($"Boolean string parses in its format: {field}={value}", () =>
            {
                var request = Adapt($"{{\"input\":\"Test.\",\"{field}\":\"{value}\"}}");
                bool? actual = field == "stream" ? request.Stream : field == "early_split" ? request.EarlySplit : request.ExtendReverbTail;
                Require(actual == bool.Parse(value), "A parsable boolean string was ignored.");
            });
        foreach (int rate in new[] { 0, 8000, 16000, 96000 })
            run.Check("Integer string retains rate clamp and zero: " + rate, () => Require(Adapt($"{{\"input\":\"Test.\",\"samplerate\":\"{rate}\"}}").SampleRate == (rate == 0 ? 0 : Math.Clamp(rate, 8000, 48000)), "A parsable integer string was ignored."));
        run.Check("Property casing and last duplicate remain predictable", () =>
        {
            var request = Adapt("{\"INPUT\":42,\"VOLUME\":0.25,\"Volume\":0.5,\"STREAM\":\"false\"}");
            Require(request.Input == "42" && request.Volume == .5f && request.Stream == false, "Case-insensitive field lookup or last-value priority changed.");
        });
    }

    private static void ProviderPriority(TestRun run)
    {
        string prefix = "TSUBAKI_DEFAULTS_TEST_" + Guid.NewGuid().ToString("N") + "_";
        string variable = prefix + "DspSettings__DefaultVolume";
        try
        {
            System.Environment.SetEnvironmentVariable(variable, "0.5");
            using var file = JsonConfig("{\"DspSettings\":{\"DefaultVolume\":0.75}}", prefix);
            run.Check("Environment values override JSON configuration", () => Require(file.GetSection("DspSettings").Get<DspSettings>()!.DefaultVolume == .5f, "Environment provider priority changed."));
            using var command = JsonConfig("{\"DspSettings\":{\"DefaultVolume\":0.75}}", prefix, ["--DspSettings:DefaultVolume", "0"]);
            run.Check("Command line overrides environment and keeps explicit zero", () => Require(command.GetSection("DspSettings").Get<DspSettings>()!.DefaultVolume == 0, "Command line lost priority or explicit zero."));
        }
        finally { System.Environment.SetEnvironmentVariable(variable, null); }
    }

    private static void ResponseDecisions(TestRun run)
    {
        Assembly engine = typeof(SynthesisRequest).Assembly;
        Type contextType = engine.GetType("ONNX_Runner.Services.Synthesis.SynthesisContext")!;
        MethodInfo resolve = engine.GetType("ONNX_Runner.Services.Synthesis.ResponsePipeline")!.GetMethod("Resolve")!;
        foreach (AudioFormat format in Enum.GetValues<AudioFormat>())
        foreach (bool configured in new[] { false, true })
        foreach (bool? requested in new bool?[] { null, false, true })
        foreach (bool flush in new[] { false, true })
        foreach (SpeechStreamFormat framing in Enum.GetValues<SpeechStreamFormat>())
            run.Check($"Response policy {format}, default={configured}, override={requested}, flush={flush}, framing={framing}", () =>
            {
                object context = Activator.CreateInstance(contextType)!;
                contextType.GetField("AudioFormat")!.SetValue(context, format == AudioFormat.B64Json ? AudioFormat.Mp3 : format);
                var request = new SynthesisRequest { Input = "Test.", Voice = "piper_base", Speed = 1, Format = format, Stream = requested, StreamFormat = framing };
                object plan = resolve.Invoke(null, [request, context, new StreamSettings { EnableStreaming = configured, FlushAfterEachSentence = flush, MinChunkSizeKb = 8 }])!;
                bool expected = format != AudioFormat.Wav && requested switch { false => false, true => true, null => configured };
                object Value(string name) => plan.GetType().GetField(name)!.GetValue(plan)!;
                Require((bool)Value("UseStreaming") == expected && (bool)Value("FlushAfterEachSentence") == (expected && flush) && (int)Value("ChunkSize") == 8192 && (SpeechStreamFormat)Value("StreamFormat") == framing, "Transport priority, WAV buffering or framing changed.");
            });
    }

    private static void ValidationContracts(TestRun run, IConfiguration configuration)
    {
        Type? validator = typeof(SynthesisRequest).Assembly.GetType("ONNX_Runner.Services.Infrastructure.ServerConfigurationValidator");
        run.Check("Startup validates shipped settings before constructing inference sessions", () =>
        {
            Require(validator != null, "Startup configuration validation is missing.");
            Validate(validator!, configuration);
        });
        if (validator == null) return;
        foreach (var (path, value) in new (string, string)[]
        {
            ("DspSettings:DefaultPitch", "NaN"), ("DspSettings:DefaultPitch", "-1"),
            ("DspSettings:DefaultVolume", "Infinity"), ("DspSettings:DefaultVolume", "-1"),
            ("DspSettings:VolumeBoosterDb", "NaN"), ("DspSettings:VolumeBoosterDb", "10000"),
            ("DspSettings:LowPassCutoffFrequency", "0"), ("DspSettings:LowPassQFactor", "0"),
            ("EffectsSettings:DefaultIntensity", "NaN"), ("EffectsSettings:DefaultEnvironmentIntensity", "Infinity"),
            ("EffectsSettings:ReverbTailSilenceFloor", "-0.1"),
            ("ClonerSettings:CloneIntensity", "NaN"), ("ClonerSettings:ToneTemperature", "Infinity"),
            ("ClonerSettings:ReferenceAudioTargetLufs", "NaN"),
            ("ChunkerSettings:SentencePauseSeconds", "-1"), ("ChunkerSettings:SentencePauseSeconds", "NaN"),
            ("StreamSettings:MinChunkSizeKb", "-1"), ("StreamSettings:MinChunkSizeKb", int.MaxValue.ToString()),
            ("RateLimitSettings:PermitLimit", "0"), ("RateLimitSettings:WindowSeconds", "0"), ("RateLimitSettings:QueueLimit", "-1"),
            ("PhonemizerSettings:LocalWinnerProbabilityFloor", "NaN"), ("PhonemizerSettings:LocalWinnerMarginFloor", "Infinity"),
            ("PhonemizerSettings:ReliabilityProbabilityThreshold", "NaN"), ("PhonemizerSettings:MaxBonusMultiplier", "NaN"),
            ("PhonemizerSettings:MixedLanguageOverrideThreshold", "Infinity")
        })
            run.Check("Invalid startup setting identifies its path: " + path + "=" + value, () =>
            {
                using var invalid = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [path] = value }).Build();
                Exception? rejection = null;
                try { Validate(validator, invalid); }
                catch (TargetInvocationException error) { rejection = error.InnerException; }
                Require(rejection is InvalidOperationException && rejection.Message.Contains(path, StringComparison.Ordinal), "Invalid server setting was accepted or its diagnostic lost the setting path.");
            });
        foreach (var (path, value) in new (string, string)[]
        {
            ("DspSettings:DefaultVolume", "0"), ("StreamSettings:MinChunkSizeKb", "0"),
            ("ChunkerSettings:SentencePauseSeconds", "0"), ("ChunkerSettings:MaxChunkLength", "0"),
            ("PhonemizerSettings:LocalWinnerProbabilityFloor", "1.01"), ("PhonemizerSettings:ReliabilityProbabilityThreshold", "1.01"),
            ("EffectsSettings:ReverbTailSilenceFloor", "0"), ("RateLimitSettings:QueueLimit", "0")
        })
            run.Check("Startup preserves valid sentinel/tuning " + path + "=" + value, () =>
            {
                using var valid = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [path] = value }).Build();
                Validate(validator, valid);
            });
    }

    private static void Validate(Type validator, IConfiguration configuration)
    {
        object Bound(Type type) => configuration.GetSection(type.Name).Get(type) ?? Activator.CreateInstance(type)!;
        Type[] types = [typeof(DspSettings), typeof(StreamSettings), typeof(EffectsSettings), typeof(ClonerSettings), typeof(ChunkerSettings), typeof(RateLimitSettings), typeof(PhonemizerSettings)];
        validator.GetMethod("Validate")!.Invoke(null, types.Select(Bound).ToArray());
    }

    private static ConfigurationRoot JsonConfig(string json, string? environmentPrefix = null, string[]? args = null)
    {
        var builder = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        if (environmentPrefix != null) builder.AddEnvironmentVariables(environmentPrefix);
        if (args != null) builder.AddCommandLine(args);
        return (ConfigurationRoot)builder.Build();
    }

    private static SynthesisRequest Adapt(string json)
    {
        var (request, error) = TsubakiRequestAdapter.ToSynthesisRequest(Read<TsubakiSpeechRequest>(json));
        return request ?? throw new InvalidOperationException(error);
    }

    private static T Read<T>(string json) where T : class
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = body;
        var read = typeof(SynthesisRequest).Assembly.GetType("ONNX_Runner.Endpoints.SpeechRequestJson")!
            .GetMethod("ReadAsync", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(T))
            .CreateDelegate<Func<HttpRequest, CancellationToken, Task<(T? Request, IResult? Error)>>>();
        var (request, error) = read(context.Request, CancellationToken.None).GetAwaiter().GetResult();
        return error == null && request != null ? request : throw new JsonException("The request body could not be read.");
    }

    private static void RejectJson(Func<object?> parse)
    {
        try { parse(); }
        catch (JsonException) { return; }
        throw new InvalidOperationException("Non-finite or incorrectly typed JSON reached request execution.");
    }
}
