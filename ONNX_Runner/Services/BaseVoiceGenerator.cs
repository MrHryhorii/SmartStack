using NAudio.Wave;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

public class BaseVoiceGenerator(
    UnifiedPhonemizer phonemizer,
    PiperRunner piperRunner,
    AudioProcessor audioProcessor,
    OpenVoiceRunner openVoice,
    PiperConfig piperConfig)
{
    private readonly UnifiedPhonemizer _phonemizer = phonemizer;
    private readonly PiperRunner _piperRunner = piperRunner;
    private readonly AudioProcessor _audioProcessor = audioProcessor;
    private readonly OpenVoiceRunner _openVoice = openVoice;
    private readonly PiperConfig _piperConfig = piperConfig;

    // Словник еталонних текстів (2-3 речення, багаті на фонеми)
    private readonly Dictionary<string, string> _referenceTexts = new(StringComparer.OrdinalIgnoreCase)
    {
        { "en", "The quick brown fox jumps over the lazy dog. A wizard's job is to vex chumps quickly in fog. Pack my box with five dozen liquor jugs." },
        { "uk", "Чуєш їх, доцю, га? Кумедна ж ти, прощайся без ґольфів! Жебракують філософи при ґанку церкви в Гадячі, ще й шатро їхнє п'яне знаємо." },
        { "nb", "Vår særnorske guttøks slår ned på den fete, jålete og late zombien. Johan, ærlig og snill, prøvde å hjelpe til med å fikse den ødelagte båten. C, Q, W og X er også med." },
        { "de", "Falsches Üben von Xylophonmusik quält jeden größeren Zwerg. Victor jagt zwölf Boxkämpfer quer über den großen Sylter Deich. Heizölrückstoßabdämpfung ist ein schönes Wort." },
        { "fr", "Portez ce vieux whisky au juge blond qui fume. Voix ambiguë d'un cœur qui au zéphyr préfère les jattes de kiwis. Dès Noël où un zéphyr haï me vêt de glaçons würmiens." },
        { "es", "El pingüino Wenceslao hizo kilómetros bajo exhaustiva lluvia y frío, añoraba a su querido cachorro. Jovencillo emponzoñado de whisky, qué figurota exhibe." },
        { "pl", "Pchnąć w tę łódź jeża lub ośm skrzyń fig. Pójdźże, kiń tę chmurność w głąb flaszy." }
    };

    // Фолбек: просто англійський алфавіт через кому
    private const string FallbackText = "A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z.";

    public void GenerateAndCacheBaseFingerprint()
    {
        // Витягуємо базовий код мови (наприклад, з "en-us" беремо "en")
        string langCode = _piperConfig.Espeak.Voice?.Split('-')[0].ToLower() ?? "en";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[AUTO-BASE] Generating baseline footprint for model language: '{langCode}'...");

        if (!_referenceTexts.TryGetValue(langCode, out string? textToSpeak))
        {
            Console.WriteLine($"[AUTO-BASE] Exact match not found for '{langCode}'. Using alphabet fallback.");

            // Тут ми одразу перепризначаємо null на наш безпечний FallbackText
            textToSpeak = FallbackText;
        }
        else
        {
            Console.WriteLine($"[AUTO-BASE] Using native phonetically rich sentences for '{langCode}'.");
        }

        // Отримуємо фонеми та генеруємо аудіо (базовий голос Piper)
        string phonemes = _phonemizer.GetPhonemes(textToSpeak);
        byte[] baseAudioBytes = _piperRunner.SynthesizeAudio(phonemes, 1.0f);

        // Читаємо в float[] через NAudio прямо в оперативній пам'яті
        using var ms = new MemoryStream(baseAudioBytes);
        using var reader = new WaveFileReader(ms);

        // Перетворюємо 16-bit PCM у float через SampleProvider
        var provider = reader.ToSampleProvider();

        // 16 біт = 2 байти на семпл
        var samples = new float[reader.Length / 2];
        provider.Read(samples, 0, samples.Length);

        // Робимо спектрограму і витягуємо зліпок кольору голосу
        var spec = _audioProcessor.GetMagnitudeSpectrogram(samples);
        var baseFingerprint = _openVoice.ExtractToneColor(spec);

        // Зберігаємо в пам'ять під ключем "piper_base"
        _openVoice.VoiceLibrary["piper_base"] = baseFingerprint;

        Console.WriteLine("[AUTO-BASE] Dynamic base footprint successfully calculated and stored in memory.");
        Console.ResetColor();
    }
}