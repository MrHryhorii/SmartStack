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

// --- РЕЄСТРАЦІЯ СЕРВІСІВ ---
// УВАГА: Це ОБОВ'ЯЗКОВО має бути ДО виклику builder.Build()
if (piperConfig != null && piperModelPath != null)
{
    var phonemizer = new PiperPhonemizer(piperConfig);
    builder.Services.AddSingleton<IPhonemizer>(phonemizer);

    var runner = new PiperRunner(piperModelPath, piperConfig, phonemizer);
    builder.Services.AddSingleton(runner);
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
        // Передаємо і текст, і швидкість із запиту
        byte[] audioBytes = piperRunner.SynthesizeAudio(request.Input, request.Speed);

        return Results.File(audioBytes, "audio/wav", "speech.wav");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
})
.WithName("CreateSpeech")
.WithOpenApi();

app.Run();