using Microsoft.AspNetCore.Mvc;
using ONNX_Runner;
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

    var runner = new PiperRunner(piperModelPath, piperConfig, phonemizer);
    builder.Services.AddSingleton(runner);

    // Реєструємо мапер пунктуації, який вивчив словник поточної моделі
    var punctuationMapper = new DynamicPunctuationMapper(piperConfig);
    builder.Services.AddSingleton(punctuationMapper);

    string dataPath = Path.GetFullPath("PiperNative");
    var mixedEspeak = new EspeakWrapper(dataPath, piperConfig.Espeak.Voice ?? "en");
    builder.Services.AddSingleton(mixedEspeak);

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

// ЕНДПОІНТ 1: Генерація голосу
app.MapPost("/v1/audio/speech", (
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
        // Отримуємо ідеальні змішані фонеми через наш центральний сервіс
        string finalPhonemes = unifiedPhonemizer.GetPhonemes(request.Input);

        // Передаємо готові ФОНЕМИ у PiperRunner
        // УВАГА: Переконайтеся, що ваш PiperRunner.SynthesizeAudio готовий 
        // приймати саме фонеми, а не пропускати їх через e-speak ще раз!
        byte[] audioBytes = piperRunner.SynthesizeAudio(
            finalPhonemes,
            request.Speed,
            request.NoiseScale,
            request.NoiseW
        );

        return Results.File(audioBytes, "audio/wav", "speech.wav");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
})
.WithName("CreateSpeech")
.WithOpenApi();

// ЕНДПОІНТ 2: Отримання фонем
app.MapPost("/v1/audio/phonemize", (
    [FromBody] PhonemizeRequest request,
    [FromServices] UnifiedPhonemizer unifiedPhonemizer) =>
{
    if (string.IsNullOrWhiteSpace(request.Input))
    {
        return Results.BadRequest(new { error = "Input text cannot be empty." });
    }

    try
    {
        // Вся складна логіка фонемізвції
        string phonemes = unifiedPhonemizer.GetPhonemes(request.Input);

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