using System.Text.RegularExpressions;
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

        var fallbackMapper = new PhonemeFallbackMapper(phoiblePath, piperConfig);
        builder.Services.AddSingleton(fallbackMapper);

        // Ініціалізуємо детектор
        var mixedPhonemizer = new MixedLanguagePhonemizer(
            phonemizerConfig,
            piperConfig.Espeak.Voice ?? "en"
        );
        builder.Services.AddSingleton(mixedPhonemizer);
    }
}

// Збираємо застосунок (після цього моменту builder.Services змінювати не можна)
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapPost("/v1/audio/speech", (
    [FromBody] OpenAiSpeechRequest request,
    [FromServices] PiperRunner piperRunner) => // Інжектимо наш Runner
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
        // Передаємо і текст, і швидкість, і наші нові параметри емоційності
        byte[] audioBytes = piperRunner.SynthesizeAudio(
            request.Input,
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

app.MapPost("/v1/audio/tokenize", (
    [FromBody] OpenAiSpeechRequest request,
    [FromServices] MixedLanguagePhonemizer mixedPhonemizer,
    [FromServices] EspeakWrapper espeakWrapper,
    [FromServices] DynamicPunctuationMapper punctuationMapper) =>
{
    if (string.IsNullOrWhiteSpace(request.Input))
    {
        return Results.BadRequest(new { error = "Input text cannot be empty." });
    }

    try
    {
        // Отримуємо розбиті мовні чанки
        var tokens = mixedPhonemizer.ProcessTextToLanguageTokens(request.Input);

        var finalPhonemes = new System.Text.StringBuilder();

        // Проходимося по кожному чанку і генеруємо фонеми
        foreach (var chunk in tokens)
        {
            if (chunk.IsPunctuationOrSpace)
            {
                // Для пунктуації e-speak не викликаємо, просто проганяємо через мапер
                string normalized = punctuationMapper.Normalize(chunk.Text);
                finalPhonemes.Append(normalized);
            }
            else
            {
                // Намагаємося встановити голос для знайденої мови
                try
                {
                    espeakWrapper.SetVoice(chunk.DetectedLanguage);
                }
                catch
                {
                    if (piperConfig != null)
                    {
                        espeakWrapper.SetVoice(piperConfig.Espeak.Voice ?? "en");
                    }
                }

                // Нормалізуємо весь текст чанку одразу
                string normalizedChunk = punctuationMapper.Normalize(chunk.Text);

                // Ізолюємо нелітерні знаки ТІЛЬКИ на краях (щоб e-speak їх не "з'їв")
                // Core - це слова та всі знаки/пробіли ВСЕРЕДИНІ фрази
                var match = MyRegex().Match(normalizedChunk);

                string prefix = match.Groups[1].Value; // Напр. початковий пробіл перед " Let's"
                string core = match.Groups[2].Value;   // Напр. "Let's see how it works" або "Hei"
                string suffix = match.Groups[3].Value; // Напр. ", " після "Hei"

                // Додаємо префікс
                finalPhonemes.Append(prefix);

                // Отримуємо фонеми тільки для ядра (e-speak чудово впорається з усім, що всередині)
                if (!string.IsNullOrEmpty(core))
                {
                    string phonemes = espeakWrapper.GetIpaPhonemes(core);
                    finalPhonemes.Append(phonemes);
                }

                // Додаємо суфікс (Повертаємо вкрадену кому і пробіл!)
                finalPhonemes.Append(suffix);
            }
        }

        // Повертаємо оригінальний текст і фінальний рядок фонем
        return Results.Ok(new
        {
            text = request.Input,
            phonemes = finalPhonemes.ToString()
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
})
.WithName("TokenizeText")
.WithOpenApi();

app.Run();

partial class Program
{
    [GeneratedRegex(@"^([^\p{L}\p{Nd}\p{M}]*)(.*?)([^\p{L}\p{Nd}\p{M}]*)$", RegexOptions.Singleline)]
    private static partial Regex MyRegex();
}