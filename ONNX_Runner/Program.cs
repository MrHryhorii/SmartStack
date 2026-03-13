using Microsoft.AspNetCore.Mvc;
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
                            // Генеруємо новий зліпок
                            Console.ForegroundColor = ConsoleColor.DarkYellow;
                            Console.WriteLine($"[VOICE] Fingerprinting new voice: {voiceName}...");

                            float[] rawSamples = audioProc.LoadWav(wavPath);
                            // Автоматичний ресемплінг під 22050 Hz (якщо треба)
                            float[] readySamples = audioProc.Resample(rawSamples, 22050, toneConfig.Data.SamplingRate);

                            var spec = audioProc.GetMagnitudeSpectrogram(readySamples);
                            var fingerprint = openVoice.ExtractToneColor(spec);

                            openVoice.SaveVoiceFingerprint(fingerprintPath, fingerprint);
                            openVoice.VoiceLibrary[voiceName] = fingerprint;
                        }
                    }

                    // РОЗВАНТАЖУЄМО ЕКСТРАКТОР З ВІДЕОПАМ'ЯТІ
                    openVoice.UnloadExtractor();

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

// ЕНДПОІНТ 1: Генерація голосу (Асинхронний)
app.MapPost("/v1/audio/speech", async (
    [FromBody] OpenAiSpeechRequest request,
    [FromServices] UnifiedPhonemizer unifiedPhonemizer,
    [FromServices] PiperRunner piperRunner) =>
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

            // Генеруємо аудіо
            byte[] bytes = piperRunner.SynthesizeAudio(
                phonemes,
                request.Speed,
                request.NoiseScale,
                request.NoiseW
            );

            return (phonemes, bytes);
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