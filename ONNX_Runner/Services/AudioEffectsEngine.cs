using ONNX_Runner.Models;
using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services;

public class AudioEffectsEngine(EffectsSettings config)
{
    private readonly EffectsSettings _globalConfig = config;

    public void ApplyEffect(Span<float> buffer, string? requestedEffect = null, float? requestedIntensity = null)
    {
        // Перевірка глобального налаштування: якщо ефекти вимкнені адміністратором - вихід
        if (!_globalConfig.EnableGlobalEffects) return;

        // Визначаємо тип ефекту: пріоритет у запиту користувача, інакше - значення за замовчуванням
        string effectString = requestedEffect ?? _globalConfig.DefaultEffect;
        if (!Enum.TryParse(effectString, true, out VoiceEffectType effectType) || effectType == VoiceEffectType.None)
        {
            return;
        }

        // Розрахунок сили ефекту (Dry/Wet mix) від 0.0 до 1.0
        float intensity = requestedIntensity ?? _globalConfig.DefaultIntensity;
        intensity = Math.Clamp(intensity, 0.0f, 1.0f);

        // Якщо інтенсивність нульова - не витрачаємо ресурси на цикл
        if (intensity <= 0.001f) return;

        // Процесор один раз обирає гілку і далі виконує чистий цикл без розгалужень
        switch (effectType)
        {
            case VoiceEffectType.VintageRadio:
                for (int i = 0; i < buffer.Length; i++)
                    buffer[i] = Mix(buffer[i], ProcessVintageRadio(buffer[i]), intensity);
                break;

            case VoiceEffectType.Telephone:
                for (int i = 0; i < buffer.Length; i++)
                    buffer[i] = Mix(buffer[i], ProcessTelephone(buffer[i]), intensity);
                break;

            case VoiceEffectType.VinylRecord:
                for (int i = 0; i < buffer.Length; i++)
                    buffer[i] = Mix(buffer[i], ProcessVinylRecord(buffer[i]), intensity);
                break;

            case VoiceEffectType.Megaphone:
                for (int i = 0; i < buffer.Length; i++)
                    buffer[i] = Mix(buffer[i], ProcessMegaphone(buffer[i]), intensity);
                break;

            case VoiceEffectType.Robot:
                for (int i = 0; i < buffer.Length; i++)
                    buffer[i] = Mix(buffer[i], ProcessRobot(buffer[i]), intensity);
                break;

            case VoiceEffectType.Overdrive:
                for (int i = 0; i < buffer.Length; i++)
                    buffer[i] = Mix(buffer[i], ProcessOverdrive(buffer[i]), intensity);
                break;

            case VoiceEffectType.Alien:
                for (int i = 0; i < buffer.Length; i++)
                    buffer[i] = Mix(buffer[i], ProcessAlien(buffer[i]), intensity);
                break;

            case VoiceEffectType.Whisper:
                for (int i = 0; i < buffer.Length; i++)
                    buffer[i] = Mix(buffer[i], ProcessWhisper(buffer[i]), intensity);
                break;

            case VoiceEffectType.Arcade:
                for (int i = 0; i < buffer.Length; i++)
                    buffer[i] = Mix(buffer[i], ProcessArcade(buffer[i]), intensity);
                break;
        }
    }

    // =================================================================
    // АЛГОРИТМИ ЕФЕКТІВ (DSP логіка)
    // =================================================================

    // Лінійна інтерполяція (Lerp) між оригінальним і обробленим сигналом
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Mix(float dry, float wet, float intensity) => (dry * (1.0f - intensity)) + (wet * intensity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessVintageRadio(float sample)
    {
        // Сатурація (MathF.Tanh): М'яко загинає піки хвилі, створюючи "лампове" тепло.
        float processed = MathF.Tanh(sample * 3.0f) * 0.8f;

        // Адитивний шум: Додаємо високочастотне ефірне шипіння постійної амплітуди.
        float noise = (Random.Shared.NextSingle() * 2f - 1f) * 0.02f;

        return processed + noise;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessTelephone(float sample)
    {
        // Hard Clipping: Жорстко обрізаємо амплітуду. Створює різкі транзисторні спотворення.
        float processed = sample * 5.0f;
        if (processed > 1.0f) processed = 1.0f;
        else if (processed < -1.0f) processed = -1.0f;

        // Bitcrushing: Квантування (округлення) сигналу до 8-бітних рівнів. Дає "цифровий пісок".
        float bitDepth = 8f;
        return MathF.Round(processed * bitDepth) / bitDepth * 0.7f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessVinylRecord(float sample)
    {
        float processed = sample;

        // Імпульсний шум: Штучний сплеск високої амплітуди (тріск голки об пилинку).
        if (Random.Shared.NextSingle() > 0.9995f)
        {
            processed += (Random.Shared.NextSingle() * 2f - 1f) * 0.6f;
        }

        // Шумовий фон: Імітація тертя голки об вінілову масу.
        float vinylHiss = (Random.Shared.NextSingle() * 2f - 1f) * 0.015f;
        processed += vinylHiss;

        // Компресія: Утримуємо різкі імпульси в межах -1..1.
        return MathF.Tanh(processed * 1.5f) * 0.9f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessMegaphone(float sample)
    {
        // Асиметричний кліппінг: Позитивні і негативні півхвилі спотворюються по-різному.
        // Це імітує фізичну властивість мембрани дешевого рупора.
        float processed = sample * 4.0f;
        if (processed > 0) processed = MathF.Tanh(processed);
        else processed = MathF.Max(processed, -0.6f); // Жорсткий зріз знизу

        return processed * 0.8f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessRobot(float sample)
    {
        // Foldback-спотворення: Замість зрізання піку (Clipping), хвиля "відбивається" всередину.
        // Це створює сильний металевий, синтетичний тембр.
        float processed = sample * 3.0f;
        if (processed > 1.0f) processed = 2.0f - processed;
        else if (processed < -1.0f) processed = -2.0f - processed;

        // Екстремальне квантування: Робить звук ще більш штучним.
        float bitDepth = 4f;
        return MathF.Round(processed * bitDepth) / bitDepth;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessOverdrive(float sample)
    {
        // Екстремальний гейн (Gain): Підсилюємо сигнал у 10 разів.
        float processed = sample * 10.0f;

        // Кубічне спотворення: Класична формула гітарного овердрайву (x - x^3 / 3).
        // Додає дуже багато непарних гармонік (відчуття "агресії" та рації).
        if (processed > 1.0f) processed = 1.0f;
        else if (processed < -1.0f) processed = -1.0f;
        else processed -= MathF.Pow(processed, 3) / 3.0f;

        return processed * 1.2f; // Компенсація втрати гучності
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessAlien(float sample)
    {
        // Phase Distortion (Фазове спотворення): Пропускаємо амплітуду через синусоїду.
        // Чим гучніший звук, тим сильніше він "загортається", створюючи лазерний тембр.
        float processed = MathF.Sin(sample * 15.0f);

        return processed * 0.8f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessWhisper(float sample)
    {
        // Амплітудна модуляція шуму (AM Noise): Генеруємо шум, що залежить від гучності голосу.
        float noise = Random.Shared.NextSingle() * 2f - 1f;

        // Множимо шум на абсолютну гучність голосу. Це "приклеює" шипіння до фонем.
        float raspy = sample + (noise * MathF.Abs(sample) * 2.0f);

        // Згладжуємо результат через Tanh для запобігання цифрового перевантаження.
        return MathF.Tanh(raspy) * 0.9f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessArcade(float sample)
    {
        // Hard Square Clipping: Екстремально посилюємо і жорстко зрізаємо хвилю.
        float processed = sample * 6.0f;
        if (processed > 1.0f) processed = 1.0f;
        else if (processed < -1.0f) processed = -1.0f;

        // Знищення точності: Квантування до 3 рівнів гучності (8-бітний ретро-стиль).
        float bitDepth = 3f;
        return MathF.Round(processed * bitDepth) / bitDepth * 0.8f;
    }
}