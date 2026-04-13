using ONNX_Runner.Models;
using System.Runtime.CompilerServices;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Серверний рушій аудіоефектів.
/// Оптимізований для високих навантажень (Zero-Allocation): обробляє аудіо без виділення нової пам'яті.
/// </summary>
public class AudioEffectsEngine(EffectsSettings config, int sampleRate)
{
    private readonly EffectsSettings _config = config;
    private readonly int _sampleRate = sampleRate;

    // Компенсація гучності (Makeup Gain).
    // Ефекти часто "з'їдають" частину звуку, тому після обробки ми трохи підсилюємо результат.
    private const float GainTelephone = 1.6f;
    private const float GainOverdrive = 0.9f;
    private const float GainBitcrusher = 1.2f;
    private const float GainRingModulator = 1.2f;
    private const float GainFlanger = 1.05f;
    private const float GainChorus = 1.05f;

    // Внутрішні стани генераторів хвиль (LFO), які керують рухом ефектів у часі.
    private float _ringPhase;
    private float _lfoPhase2;
    private float _flangerPhase;
    private float _chorusPhase;

    // Стани для ефекту Bitcrusher
    private float _zohPhase;
    private float _zohHold;
    private uint _prngState = 12345; // Простий генератор випадкових чисел для "аналогового" шуму

    // Стан для Flanger (зберігає частину попереднього звуку для ефекту резонансу)
    private float _flangerFeedbackState;

    // "Пам'ять" ефектів. Зберігає останні ~90 мілісекунд звуку для створення відлуння/хору.
    private readonly float[] _delayBuffer = new float[4096];
    private int _delayWritePos;

    // Еквалайзери для попередньої підготовки звуку (наприклад, зріз басів для "Телефону")
    private readonly List<BiQuadFilter> _filters = new(4);
    private BiQuadFilter? _odPostFilter;

    private VoiceEffectType _currentEffect = VoiceEffectType.None;
    private bool _eqInitialized = false;

    /// <summary>
    /// Очищує всю історію та пам'ять рушія. 
    /// Обов'язково викликати перед обробкою нового голосового повідомлення, щоб уникнути "хвостів".
    /// </summary>
    public void Reset()
    {
        Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
        _ringPhase = 0;
        _lfoPhase2 = 0;
        _flangerPhase = 0;
        _chorusPhase = 0;
        _zohPhase = 0;
        _zohHold = 0;
        _prngState = 12345;
        _flangerFeedbackState = 0f;
        _delayWritePos = 0;
        _eqInitialized = false;
    }

    public void ApplyEffect(Span<float> buffer, string? requestedEffect = null, float? requestedIntensity = null)
    {
        if (!_config.EnableGlobalEffects) return;

        string effectString = requestedEffect ?? _config.DefaultEffect;
        if (!Enum.TryParse(effectString, true, out VoiceEffectType effectType) || effectType == VoiceEffectType.None)
            return;

        float intensity = Math.Clamp(requestedIntensity ?? _config.DefaultIntensity, 0f, 1f);
        if (intensity <= 0.001f) return;

        // Якщо ефект змінився, переналаштовуємо еквалайзери та чистимо пам'ять
        if (!_eqInitialized || _currentEffect != effectType)
        {
            Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            SetupEqualizer(effectType);
            _eqInitialized = true;
            _currentEffect = effectType;
        }

        // Головний цикл обробки: семпл за семплом
        for (int i = 0; i < buffer.Length; i++)
        {
            float dry = buffer[i];    // Оригінальний звук
            float eqSignal = dry;

            // Пропускаємо через еквалайзер
            foreach (var filter in _filters)
                eqSignal = filter.Transform(eqSignal);

            // Накладаємо основний художній ефект
            float wet = Process(_currentEffect, eqSignal, intensity);

            // Змішуємо оригінал з ефектом залежно від інтенсивності
            buffer[i] = dry + (wet - dry) * intensity;
        }
    }

    /// <summary>
    /// Налаштовує еквалайзери для формування базового "профілю" звуку.
    /// </summary>
    private void SetupEqualizer(VoiceEffectType type)
    {
        _filters.Clear();
        float nyquist = _sampleRate / 2.0f * 0.95f;
        float SafeFreq(float target) => Math.Min(target, nyquist);

        switch (type)
        {
            case VoiceEffectType.Telephone:
                // Імітація старої трубки: зрізаємо баси і верхи, додаємо "носовий" резонанс на 2 кГц.
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 300f, 0.707f));
                _filters.Add(BiQuadFilter.PeakingEQ(_sampleRate, SafeFreq(2000f), 3.0f, 5f));
                _filters.Add(BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(4000f), 1.2f));
                break;

            case VoiceEffectType.Overdrive:
                // Профіль для дисторшну: зрізаємо зайвий гул внизу і підготовлюємо верхи.
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 400f, 1.0f));
                _filters.Add(BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(6500f), 0.707f));
                // Фільтр, який прибере неприємний цифровий писк ПІСЛЯ дисторшну.
                _odPostFilter = BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(8500f), 0.707f);
                break;

            case VoiceEffectType.Bitcrusher:
            case VoiceEffectType.RingModulator:
                // Легке очищення від гулу перед обробкою
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 150f, 0.707f));
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Process(VoiceEffectType type, float x, float intensity)
    {
        return type switch
        {
            // Сатурація = плавне стиснення хвилі для "утеплення" звуку
            VoiceEffectType.Telephone => Saturation.Tape(Saturation.AsymmetricTube(x * 2.5f)) * GainTelephone,
            VoiceEffectType.Overdrive => Overdrive(x, intensity) * GainOverdrive,
            VoiceEffectType.Bitcrusher => Bitcrusher(x) * GainBitcrusher,
            VoiceEffectType.RingModulator => RingModulator(x) * GainRingModulator,
            VoiceEffectType.Flanger => Flanger(x) * GainFlanger,
            VoiceEffectType.Chorus => Chorus(x) * GainChorus,
            _ => x
        };
    }

    /// <summary>
    /// Перевантаження (Рація/Гучномовець). Робить звук гучним і агресивним.
    /// </summary>
    private float Overdrive(float x, float intensity)
    {
        // Рівень спотворення залежить від налаштування intensity
        float drive = 2.0f + (intensity * 6.0f);
        float distorted = Saturation.Cubic(x * drive);

        // Зрізаємо ріжучі цифрові артефакти, залишаючи м'яке "аналогове" тепло
        if (_odPostFilter != null)
            distorted = _odPostFilter.Transform(distorted);

        return Saturation.Tape(distorted); // Захист від піків
    }

    /// <summary>
    /// Ефект "Робот/Далек". Створює металевий дзвін, множачи звук на синусоїду.
    /// </summary>
    private float RingModulator(float x)
    {
        // Щоб робот звучав цікавіше, частота модуляції плавно "плаває" від 400 до 600 Гц
        _lfoPhase2 += MathF.PI * 2f * 0.5f / _sampleRate;
        if (_lfoPhase2 > MathF.PI * 2f) _lfoPhase2 -= MathF.PI * 2f;
        float currentFreq = 500f + MathF.Sin(_lfoPhase2) * 100f;

        // Множимо голос на згенеровану частоту
        _ringPhase += MathF.PI * 2f * currentFreq / _sampleRate;
        if (_ringPhase > MathF.PI * 2f) _ringPhase -= MathF.PI * 2f;

        return x * MathF.Sin(_ringPhase);
    }

    /// <summary>
    /// Ефект "Ретро-приставка / 8-біт". Грубо руйнує якість звуку.
    /// </summary>
    private float Bitcrusher(float x)
    {
        // Штучно знижуємо частоту дискретизації до вінтажних 8 кГц
        _zohPhase += 8000f / _sampleRate;
        if (_zohPhase >= 1.0f)
        {
            _zohHold = x;
            _zohPhase -= 1.0f;
        }

        // Додаємо ледь помітний шум (Dither), щоб прибрати ідеальну комп'ютерну стерильність
        _prngState = 1664525 * _prngState + 1013904223;
        float dither = (((float)_prngState / uint.MaxValue) - 0.5f) * 0.03f;

        // Знижуємо бітність: округлюємо звук всього до 16 рівнів гучності
        float levels = 16f;
        return MathF.Round((_zohHold + dither) * levels) / levels;
    }

    /// <summary>
    /// Ефект "Літака/Труби". Накладає на звук його ж копію з крихітною змінною затримкою.
    /// </summary>
    private float Flanger(float x)
    {
        // Розраховуємо затримку, яка постійно плаває туди-сюди (від 1 до 5 мс)
        _flangerPhase += MathF.PI * 2f * 0.5f / _sampleRate;
        if (_flangerPhase > MathF.PI * 2f) _flangerPhase -= MathF.PI * 2f;
        float delayMs = 3.0f + MathF.Sin(_flangerPhase) * 2.0f;
        float delayed = ReadFractionalDelay(delayMs * _sampleRate / 1000f);

        // "Резонанс": повертаємо частину затриманого звуку назад у буфер, трохи його "замилюючи"
        _flangerFeedbackState = 0.5f * _flangerFeedbackState + 0.5f * delayed;
        float feedback = 0.7f;

        _delayBuffer[_delayWritePos] = x + (_flangerFeedbackState * feedback);
        _delayWritePos = (_delayWritePos + 1) & 4095;

        // Змішуємо оригінал і затримку. Clamp потрібен, щоб сума не "вибухнула".
        float wet = (x * 0.45f) + (delayed * 0.55f);
        return Math.Clamp(wet, -1.0f, 1.0f);
    }

    /// <summary>
    /// Ефект "Хорус". Розмиває звук, створюючи ілюзію багатоголосся.
    /// </summary>
    private float Chorus(float x)
    {
        _chorusPhase += MathF.PI * 2f * 0.8f / _sampleRate;
        if (_chorusPhase > MathF.PI * 2f) _chorusPhase -= MathF.PI * 2f;

        // Створюємо крихітний хаос, щоб голоси не звучали ідеально синхронно (як у живого хору)
        _prngState = 1664525 * _prngState + 1013904223;
        float analogDrift = (((float)_prngState / uint.MaxValue) - 0.5f) * 0.04f;

        // Читаємо два віртуальних "голоси" з різними затримками
        float delayMs1 = 20.0f + MathF.Sin(_chorusPhase) * 6.0f;
        float delayed1 = ReadFractionalDelay(delayMs1 * _sampleRate / 1000f);

        float delayMs2 = 25.0f + MathF.Cos(_chorusPhase + analogDrift) * 7.0f;
        float delayed2 = ReadFractionalDelay(delayMs2 * _sampleRate / 1000f);

        _delayBuffer[_delayWritePos] = x;
        _delayWritePos = (_delayWritePos + 1) & 4095;

        // Змішуємо: 50% оригіналу та по 25% на кожен додатковий голос
        float wet = (x * 0.5f) + (delayed1 * 0.25f) + (delayed2 * 0.25f);
        return Math.Clamp(wet, -1.0f, 1.0f);
    }

    /// <summary>
    /// Допоміжний метод для просторових ефектів. 
    /// Читає звук з пам'яті (буфера) з інтерполяцією, щоб він не "хрустів" при зміні часу затримки.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ReadFractionalDelay(float delaySamples)
    {
        float readPos = _delayWritePos - delaySamples;
        if (readPos < 0) readPos += 4096;

        int p1 = (int)readPos & 4095;
        int p2 = (p1 + 1) & 4095;
        float frac = readPos - (int)readPos;

        // Плавно змішуємо два сусідні семпли
        return (_delayBuffer[p1] * (1.0f - frac)) + (_delayBuffer[p2] * frac);
    }
}

/// <summary>
/// Інструменти для математичного викривлення форми звукової хвилі.
/// Додають "утеплення", характер або жорсткий дисторшн.
/// </summary>
file static class Saturation
{
    // Імітація магнітної стрічки. М'яко згладжує піки (безпечний лімітер).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Tape(float x) => MathF.Tanh(x);

    // Імітація старої лампи. Спотворює звук асиметрично, роблячи його теплим.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AsymmetricTube(float x) => x > 0 ? MathF.Tanh(x) : (MathF.Exp(x) - 1.0f);

    // Кубічний дисторшн. Жорстке і агресивне перевантаження звуку.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Cubic(float x)
    {
        float clamped = x > 1.0f ? 1.0f : (x < -1.0f ? -1.0f : x);
        return clamped - clamped * clamped * clamped / 3.0f;
    }
}