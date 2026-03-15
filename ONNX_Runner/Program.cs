using Microsoft.AspNetCore.Mvc;
using NAudio.Wave;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

var builder = WebApplication.CreateBuilder(args);

// Додаємо підтримку Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// --- ЗАВАНТАЖЕННЯ МОДЕЛІ ТА ЛОГУВАННЯ ---
string modelDirectory = "Model";
PiperConfig? piperConfig = null;
string? piperModelPath = null;

try
{
    // Створюємо папку, якщо її ще немає, щоб програма не падала при першому запуску
    if (!Directory.Exists(modelDirectory))
    {
        Directory.CreateDirectory(modelDirectory);
        Console.WriteLine($"[WARNING] Directory '{modelDirectory}' was created. Please put your .onnx and .json files there.");
    }
    else
    {
        var (onnxPath, config) = ModelLoader.LoadFromDirectory(modelDirectory);
        piperModelPath = onnxPath;
        piperConfig = config;

        // Вивід даних у консоль
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("=========================================");
        Console.WriteLine("        MODEL LOADED SUCCESSFULLY        ");
        Console.WriteLine("=========================================");
        Console.ResetColor();
        Console.WriteLine($"Model Path:      {onnxPath}");
        Console.WriteLine($"Sample Rate:     {config.Audio.SampleRate} Hz");
        Console.WriteLine($"Espeak Voice:    {config.Espeak.Voice}");
        Console.WriteLine($"Noise Scale:     {config.Inference.NoiseScale}");
        Console.WriteLine($"Length Scale:    {config.Inference.LengthScale}");
        Console.WriteLine($"Noise W:         {config.Inference.NoiseW}");
        Console.WriteLine($"Total Phonemes:  {config.PhonemeIdMap.Count} unique sounds mapped");
        Console.WriteLine("=========================================\n");
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[ERROR] Failed to load model: {ex.Message}");
    Console.ResetColor();
}

// Читаємо налаштування з appsettings.json
var phonemizerConfig = builder.Configuration.GetSection("PhonemizerSettings").Get<PhonemizerSettings>();

// --- РЕЄСТРАЦІЯ СЕРВІСІВ ---
if (piperConfig != null && piperModelPath != null)
{
    var phonemizer = new PiperPhonemizer(piperConfig);
    builder.Services.AddSingleton<IPhonemizer>(phonemizer);

    // Реєструємо чанкер для розбиття довгих флнетичних рядків на більш короткі
    var chunker = new PhonemeChunker();
    builder.Services.AddSingleton(chunker);

    var runner = new PiperRunner(piperModelPath, piperConfig, phonemizer, chunker);
    builder.Services.AddSingleton(runner);

    // Реєструємо мапер пунктуації, який вивчив словник поточної моделі
    var punctuationMapper = new DynamicPunctuationMapper(piperConfig);
    builder.Services.AddSingleton(punctuationMapper);

    string dataPath = Path.GetFullPath("PiperNative");
    var mixedEspeak = new EspeakWrapper(dataPath, piperConfig.Espeak.Voice ?? "en");
    builder.Services.AddSingleton(mixedEspeak);

    // --- ЗАВАНТАЖЕННЯ OPENVOICE (CLONER) ---
    string clonerDirectory = "Cloner";
    string voicesDirectory = "Voices";

    if (Directory.Exists(clonerDirectory))
    {
        try
        {
            string extractPath = Path.Combine(clonerDirectory, "tone_extract.onnx");
            string colorPath = Path.Combine(clonerDirectory, "tone_color.onnx");
            string toneJsonPath = Path.Combine(clonerDirectory, "tone_config.json");

            if (File.Exists(extractPath) && File.Exists(colorPath) && File.Exists(toneJsonPath))
            {
                string toneJsonContent = File.ReadAllText(toneJsonPath);
                var toneConfig = System.Text.Json.JsonSerializer.Deserialize<ToneConfig>(toneJsonContent);

                if (toneConfig != null)
                {
                    var openVoice = new OpenVoiceRunner(extractPath, colorPath, toneConfig);
                    var audioProc = new AudioProcessor(toneConfig);

                    // --- СКАНУВАННЯ ТА ГЕНЕРАЦІЯ ГОЛОСІВ ---
                    if (!Directory.Exists(voicesDirectory)) Directory.CreateDirectory(voicesDirectory);

                    var wavFiles = Directory.GetFiles(voicesDirectory, "*.wav");
                    foreach (var wavPath in wavFiles)
                    {
                        string voiceName = Path.GetFileNameWithoutExtension(wavPath);
                        string fingerprintPath = Path.Combine(voicesDirectory, voiceName + ".voice");

                        if (File.Exists(fingerprintPath))
                        {
                            // Завантажуємо готовий зліпок з кешу
                            var fingerprint = openVoice.LoadVoiceFingerprint(fingerprintPath);
                            openVoice.VoiceLibrary[voiceName] = fingerprint;
                            Console.ForegroundColor = ConsoleColor.DarkGreen;
                            Console.WriteLine($"[VOICE] Loaded from cache: {voiceName}");
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.DarkYellow;
                            Console.WriteLine($"\n[VOICE] processing: {voiceName}...");

                            // ТОЧКА 1: Читання
                            var (rawSamples, fileRate) = audioProc.LoadWav(wavPath);
                            Console.WriteLine($"   -> Step 1: Read {rawSamples.Length} samples at {fileRate}Hz");

                            if (rawSamples.Length == 0)
                            {
                                Console.WriteLine("   [ERROR] File is empty!");
                                continue;
                            }

                            // ТОЧКА 2: Ресемплінг
                            // Важливо: передаємо fileRate, а не жорсткі 22050!
                            float[] readySamples = audioProc.Resample(rawSamples, fileRate, toneConfig.Data.SamplingRate);
                            Console.WriteLine($"   -> Step 2: Resampled to {toneConfig.Data.SamplingRate}Hz. New count: {readySamples.Length}");

                            // ТОЧКА 3: Спектрограма
                            var spec = audioProc.GetMagnitudeSpectrogram(readySamples);
                            int frames = spec.GetLength(0);
                            Console.WriteLine($"   -> Step 3: Spectrogram frames: {frames}");

                            if (frames == 0)
                            {
                                Console.WriteLine("   [ERROR] Audio too short for the model! Need at least 1024 samples.");
                                continue;
                            }

                            // ТОЧКА 4: Екстракція
                            var fingerprint = openVoice.ExtractToneColor(spec);
                            Console.WriteLine($"   -> Step 4: Fingerprint extracted (Size: {fingerprint.Length})");

                            openVoice.SaveVoiceFingerprint(fingerprintPath, fingerprint);
                            openVoice.VoiceLibrary[voiceName] = fingerprint;

                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine($"   [SUCCESS] Zlipok saved: {voiceName}.voice");
                            Console.ResetColor();
                        }
                    }

                    // РОЗВАНТАЖУЄМО ЕКСТРАКТОР З ВІДЕОПАМ'ЯТІ
                    //openVoice.UnloadExtractor();

                    builder.Services.AddSingleton(openVoice);
                    builder.Services.AddSingleton(audioProc);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to load OpenVoice/Voices: {ex.Message}");
        }
    }

    // Змінні для передачі в UnifiedPhonemizer
    MixedLanguagePhonemizer? mixedPhonemizer = null;
    PhonemeFallbackMapper? fallbackMapper = null;

    // Ініціалізуємо змішаний фонемізатор з конфігу (якщо детектор увімкнено)
    if (phonemizerConfig != null && phonemizerConfig.UseLanguageDetector)
    {
        // Спочатку завантажуємо базу PHOIBLE
        string phoibleDirectory = "PHOIBLE";
        string phoiblePath = Path.Combine(phoibleDirectory, "phoible.csv");

        // Створюємо папку, якщо її ще немає (щоб підказати користувачу)
        if (!Directory.Exists(phoibleDirectory))
        {
            Directory.CreateDirectory(phoibleDirectory);
            Console.WriteLine($"[WARNING] Directory '{phoibleDirectory}' was created. Please put your 'phoible.csv' file there.");
        }

        fallbackMapper = new PhonemeFallbackMapper(phoiblePath, piperConfig);
        builder.Services.AddSingleton(fallbackMapper);

        // Ініціалізуємо детектор
        mixedPhonemizer = new MixedLanguagePhonemizer(
            phonemizerConfig,
            piperConfig.Espeak.Voice ?? "en"
        );
        builder.Services.AddSingleton(mixedPhonemizer);
    }

    // Створюємо та реєструємо центральний сервіс фонемізації
    var unifiedPhonemizer = new UnifiedPhonemizer(
        mixedEspeak,
        punctuationMapper,
        piperConfig,
        mixedPhonemizer,
        fallbackMapper
    );
    builder.Services.AddSingleton(unifiedPhonemizer);
}

// Збираємо застосунок
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// =================================================================
// АВТОМАТИЧНА ГЕНЕРАЦІЯ БАЗОВОГО ЗЛІПКУ PIPER
// =================================================================
using (var scope = app.Services.CreateScope())
{
    var openVoiceSvc = scope.ServiceProvider.GetService<OpenVoiceRunner>();
    var audioProcSvc = scope.ServiceProvider.GetService<AudioProcessor>();

    var unifiedPhonemizerSvc = scope.ServiceProvider.GetService<UnifiedPhonemizer>();
    var piperRunnerSvc = scope.ServiceProvider.GetService<PiperRunner>();

    // Перевірка на null
    if (openVoiceSvc != null && audioProcSvc != null && unifiedPhonemizerSvc != null && piperRunnerSvc != null && piperConfig != null)
    {
        var baseGenerator = new BaseVoiceGenerator(
            unifiedPhonemizerSvc, // Передаємо розумний фонемізатор
            piperRunnerSvc,
            audioProcSvc,
            openVoiceSvc,
            piperConfig
        );

        // Генеруємо piper_base
        baseGenerator.GenerateAndCacheBaseFingerprint();

        // Вивантажуємо екстрактор з відеопам'яті
        openVoiceSvc.UnloadExtractor();
    }
}

// ЕНДПОІНТ 1: Генерація голосу (Асинхронний)
app.MapPost("/v1/audio/speech", async (
    [FromBody] OpenAiSpeechRequest request,
    [FromServices] UnifiedPhonemizer unifiedPhonemizer,
    [FromServices] PiperRunner piperRunner,
    [FromServices] IServiceProvider services) =>
{
    if (string.IsNullOrWhiteSpace(request.Input))
    {
        return Results.BadRequest(new { error = "Input text cannot be empty." });
    }

    if (piperConfig == null)
    {
        return Results.Problem("Model is not loaded properly.", statusCode: 500);
    }

    try
    {
        // Відправляємо важку процесорну роботу у фоновий потік, щоб не блокувати сервер
        var (finalPhonemes, audioBytes) = await Task.Run(() =>
        {
            // Отримуємо фонеми
            string phonemes = unifiedPhonemizer.GetPhonemes(request.Input);

            // Генеруємо базове аудіо Piper
            byte[] piperWav = piperRunner.SynthesizeAudio(
                phonemes,
                request.Speed,
                request.NoiseScale,
                request.NoiseW
            );

            // ==========================================
            // МАГІЯ КЛОНУВАННЯ (ЯКЩО ВКАЗАНО ГОЛОС)
            // ==========================================
            if (!string.IsNullOrEmpty(request.Voice))
            {
                var openVoice = services.GetService<OpenVoiceRunner>();
                var audioProc = services.GetService<AudioProcessor>();

                // ЛОГИ:
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[API] Requested voice clone for: '{request.Voice}'");

                if (openVoice != null && audioProc != null)
                {
                    if (openVoice.VoiceLibrary.TryGetValue(request.Voice, out var targetFingerprint) &&
                        openVoice.VoiceLibrary.TryGetValue("piper_base", out var sourceFingerprint))
                    {
                        Console.WriteLine($"[CLONER] Found fingerprints for '{request.Voice}' and 'piper_base'. Starting conversion...");

                        // БЕЗПЕЧНЕ ЧИТАННЯ (ігноруємо системний заголовок WAV)
                        using var ms = new MemoryStream(piperWav);
                        using var reader = new WaveFileReader(ms);
                        var provider = reader.ToSampleProvider();

                        var samplesList = new List<float>();
                        float[] buffer = new float[8192];
                        int read;
                        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            samplesList.AddRange(buffer.Take(read));
                        }
                        var samples = samplesList.ToArray();

                        Console.WriteLine($"[CLONER] Base audio decoded. Samples count: {samples.Length}");

                        // Створюємо спектрограму
                        var spec = audioProc.GetMagnitudeSpectrogram(samples);
                        Console.WriteLine($"[CLONER] Spectrogram created. Shape: {spec.GetLength(0)} frames x {spec.GetLength(1)} bins");

                        // ==========================================
                        // РЕНТГЕН ДАНИХ (ДІАГНОСТИКА ТИШІ)
                        // ==========================================
                        float spMax = 0; int spNaN = 0;
                        foreach (var s in spec) { if (float.IsNaN(s)) spNaN++; else if (s > spMax) spMax = s; }
                        Console.WriteLine($"[DIAGNOSTIC] Spectrogram -> MaxVal: {spMax:F4}, NaNs: {spNaN}");

                        float f1Max = 0; int f1NaN = 0;
                        foreach (var s in sourceFingerprint) { if (float.IsNaN(s)) f1NaN++; else if (Math.Abs(s) > f1Max) f1Max = Math.Abs(s); }
                        Console.WriteLine($"[DIAGNOSTIC] Source Voice (piper_base) -> MaxVal: {f1Max:F4}, NaNs: {f1NaN}");

                        float f2Max = 0; int f2NaN = 0;
                        foreach (var s in targetFingerprint) { if (float.IsNaN(s)) f2NaN++; else if (Math.Abs(s) > f2Max) f2Max = Math.Abs(s); }
                        Console.WriteLine($"[DIAGNOSTIC] Target Voice ({request.Voice}) -> MaxVal: {f2Max:F4}, NaNs: {f2NaN}");

                        // Накладаємо тембр
                        float[] convertedSamples = openVoice.ApplyToneColor(spec, sourceFingerprint, targetFingerprint);
                        Console.WriteLine($"[CLONER] Tone color applied successfully! New samples count: {convertedSamples.Length}");

                        float cMax = 0; int cNaN = 0;
                        foreach (var s in convertedSamples) { if (float.IsNaN(s)) cNaN++; else if (Math.Abs(s) > cMax) cMax = Math.Abs(s); }
                        Console.WriteLine($"[DIAGNOSTIC] Converted Audio -> MaxVal: {cMax:F4}, NaNs: {cNaN}");
                        // ==========================================

                        // Конвертуємо змінений масив назад у байтовий WAV файл
                        piperWav = piperRunner.ConvertToWav(convertedSamples);
                        Console.WriteLine($"[CLONER] Packed back to WAV. Final size: {piperWav.Length} bytes.");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[CLONER-SKIP] Voice '{request.Voice}' OR 'piper_base' was NOT found in memory!");
                    }
                }
                Console.ResetColor();
            }

            return (phonemes, piperWav);
        });

        return Results.File(audioBytes, "audio/wav", "speech.wav");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
})
.WithName("CreateSpeech")
.WithOpenApi();

// ЕНДПОІНТ 2: Отримання фонем (Асинхронний)
app.MapPost("/v1/audio/phonemize", async (
    [FromBody] PhonemizeRequest request,
    [FromServices] UnifiedPhonemizer unifiedPhonemizer) =>
{
    if (string.IsNullOrWhiteSpace(request.Input))
    {
        return Results.BadRequest(new { error = "Input text cannot be empty." });
    }

    try
    {
        // Хоча фонемізація швидша за генерацію аудіо, вона теж використовує CPU.
        // Переносимо її в Task.Run для абсолютної стабільності при 1000+ запитах/сек.
        string phonemes = await Task.Run(() => unifiedPhonemizer.GetPhonemes(request.Input));

        return Results.Ok(new
        {
            text = request.Input,
            phonemes
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
})
.WithName("GetPhonemes")
.WithOpenApi();

app.Run();