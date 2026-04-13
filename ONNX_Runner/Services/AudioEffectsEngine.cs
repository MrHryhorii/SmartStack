using ONNX_Runner.Models;
using System.Runtime.CompilerServices;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Серверний рушій аудіоефектів. 
/// Працює за принципом Zero-Allocation (не створює нових об'єктів у пам'яті під час обробки звуку).
/// </summary>
public class AudioEffectsEngine(EffectsSettings config, int sampleRate)
{
    private readonly EffectsSettings _config = config;
    private readonly int _sampleRate = sampleRate;

    // Компенсація гучності. 
    // Ефекти (особливо еквалайзери) "з'їдають" частину звуку, тому ми підсилюємо результат у кінці.
    private const float GainTelephone = 1.6f;
    private const float GainOverdrive = 0.9f;
    private const float GainBitcrusher = 1.2f;
    private const float GainRingModulator = 1.2f;
    private const float GainFlanger = 1.05f;
    private const float GainChorus = 1.05f;

    // Внутрішні таймери (фази) для ефектів, що рухаються (наприклад, звук, що плаває)
    private float _ringPhase;
    private float _lfoPhase2;
    private float _flangerPhase;
    private float _chorusPhase;

    // Змінні для 8-бітного ефекту (Arcade) та імітації "аналогового" хаосу
    private float _zohPhase;
    private float _zohHold;
    private uint _prngState = 12345;  // Легкий генератор випадкових чисел
    private float _chorusDrift;       // Плавне плавання звуку для Хоруса

    // Пам'ять для "літака" (Flanger)
    private float _flangerFeedbackState;

    // Кільцевий буфер пам'яті. Зберігає останні частки секунди звуку для створення відлуння.
    private readonly float[] _delayBuffer = new float[4096];
    private int _delayWritePos;

    // Список фільтрів частот (еквалайзерів)
    private readonly List<BiQuadFilter> _filters = new(4);
    private BiQuadFilter? _odPostFilter;

    private VoiceEffectType _currentEffect = VoiceEffectType.None;
    private bool _eqInitialized = false;

    /// <summary>
    /// Повністю очищує історію звуку.
    /// Важливо викликати перед новим аудіо-потоком, щоб шматки старого звуку не потрапили у новий.
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
        _chorusDrift = 0f;
        _flangerFeedbackState = 0f;
        _delayWritePos = 0;
        _eqInitialized = false;
    }

    /// <summary>
    /// Головний метод, який застосовує вибраний ефект до масиву звуку.
    /// </summary>
    public void ApplyEffect(Span<float> buffer, string? requestedEffect = null, float? requestedIntensity = null)
    {
        if (!_config.EnableGlobalEffects) return;

        string effectString = requestedEffect ?? _config.DefaultEffect;
        if (!Enum.TryParse(effectString, true, out VoiceEffectType effectType) || effectType == VoiceEffectType.None)
            return;

        float intensity = Math.Clamp(requestedIntensity ?? _config.DefaultIntensity, 0f, 1f);
        if (intensity <= 0.001f) return;

        // Якщо ефект змінився "на льоту", переналаштовуємо фільтри і чистимо буфер
        if (!_eqInitialized || _currentEffect != effectType)
        {
            Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            SetupEqualizer(effectType);
            _eqInitialized = true;
            _currentEffect = effectType;
        }

        // Обробляємо звук семпл за семплом
        for (int i = 0; i < buffer.Length; i++)
        {
            float dry = buffer[i];    // Оригінальний "сухий" звук
            float eqSignal = dry;

            // Еквалізація (наприклад, зрізаємо баси)
            foreach (var filter in _filters)
                eqSignal = filter.Transform(eqSignal);

            // Художня обробка (робот, рація, 8-біт тощо)
            float wet = Process(_currentEffect, eqSignal, intensity);

            // Змішуємо оригінал з ефектом відповідно до вказаної інтенсивності
            buffer[i] = dry + (wet - dry) * intensity;
        }
    }

    /// <summary>
    /// Налаштовує еквалайзери для формування базового "профілю" звуку.
    /// </summary>
    private void SetupEqualizer(VoiceEffectType type)
    {
        _filters.Clear();
        float nyquist = _sampleRate / 2.0f * 0.95f; // Максимально можлива частота
        float SafeFreq(float target) => Math.Min(target, nyquist);

        switch (type)
        {
            case VoiceEffectType.Telephone:
                // Імітація дешевого динаміка: зрізаємо все зайве, залишаємо лише середину
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 300f, 0.707f));
                _filters.Add(BiQuadFilter.PeakingEQ(_sampleRate, SafeFreq(2000f), 3.0f, 5f));
                _filters.Add(BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(4000f), 1.2f));
                break;

            case VoiceEffectType.Overdrive:
                // Підготовка для дисторшну та фільтр для зрізу неприємного цифрового "піску" в кінці
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 400f, 1.0f));
                _filters.Add(BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(6500f), 0.707f));
                _odPostFilter = BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(8500f), 0.707f);
                break;

            case VoiceEffectType.Bitcrusher:
            case VoiceEffectType.RingModulator:
                // Легке очищення від гулу (басів) перед обробкою
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 150f, 0.707f));
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Process(VoiceEffectType type, float x, float intensity)
    {
        return type switch
        {
            // Saturation.Tape запобігає перевантаженню (хрускоту) звуку
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
    /// Перевантаження (Гучномовець/Рація). Жорстко спотворює звук.
    /// </summary>
    private float Overdrive(float x, float intensity)
    {
        // Чим вища інтенсивність від користувача - тим сильніший дисторшн
        float drive = 2.0f + (intensity * 6.0f);
        float distorted = Saturation.Cubic(x * drive);

        // Згладжуємо звук, щоб він не різав вуха високими частотами
        if (_odPostFilter != null)
            distorted = _odPostFilter.Transform(distorted);

        return Saturation.Tape(distorted);
    }

    /// <summary>
    /// Ефект "Робот / Далек". Множить голос на швидку пульсацію.
    /// </summary>
    private float RingModulator(float x)
    {
        // Частота модуляції злегка "плаває" (500 ± 100 Гц), щоб робот не звучав надто монотонно
        _lfoPhase2 += MathF.PI * 2f * 0.5f / _sampleRate;
        if (_lfoPhase2 > MathF.PI * 2f) _lfoPhase2 -= MathF.PI * 2f;

        float currentFreq = 500f + MathF.Sin(_lfoPhase2) * 100f;

        _ringPhase += MathF.PI * 2f * currentFreq / _sampleRate;
        if (_ringPhase > MathF.PI * 2f) _ringPhase -= MathF.PI * 2f;

        return x * MathF.Sin(_ringPhase);
    }

    /// <summary>
    /// Ефект 8-біт (Аркада). Штучно погіршує якість звуку.
    /// </summary>
    private float Bitcrusher(float x)
    {
        // Пропускаємо частину звуку (робимо частоту 8 кГц)
        _zohPhase += 8000f / _sampleRate;
        if (_zohPhase >= 1.0f)
        {
            _zohHold = x;
            _zohPhase -= 1.0f;
        }

        // Генеруємо мікро-шум, щоб звук мав "текстуру" справжньої старої приставки
        _prngState = 1664525 * _prngState + 1013904223;
        float dither = (((float)_prngState / uint.MaxValue) - 0.5f) * 0.03f;

        // Знижуємо кількість рівнів гучності до 16
        float levels = 16f;
        return MathF.Round((_zohHold + dither) * levels) / levels;
    }

    /// <summary>
    /// Ефект "Космічна труба / Літак". Накладає звук сам на себе із затримкою, яка постійно змінюється.
    /// </summary>
    private float Flanger(float x)
    {
        _flangerPhase += MathF.PI * 2f * 0.5f / _sampleRate;
        if (_flangerPhase > MathF.PI * 2f) _flangerPhase -= MathF.PI * 2f;

        float delayMs = 3.0f + MathF.Sin(_flangerPhase) * 2.0f;
        float delayed = ReadFractionalDelay(delayMs * _sampleRate / 1000f);

        // Імітуємо стару аналогову схему: замилюємо звук, який повертається назад у пам'ять
        float feedback = 0.7f;
        float fbInput = delayed * feedback;
        _flangerFeedbackState = 0.5f * _flangerFeedbackState + 0.5f * fbInput;

        // Запобігає накопиченню енергії та "цифровим вибухам" при крику в мікрофон.
        float input = x + _flangerFeedbackState;
        _delayBuffer[_delayWritePos] = Math.Clamp(input, -1.0f, 1.0f);
        _delayWritePos = (_delayWritePos + 1) & 4095;

        // Мікс для ефекту "літака"
        float wet = (x * 0.7f) + (delayed * 0.7f);
        return Math.Clamp(wet, -1.0f, 1.0f);
    }

    /// <summary>
    /// Ефект "Хорус". Розмиває звук, створюючи ілюзію багатоголосся.
    /// </summary>
    private float Chorus(float x)
    {
        _chorusPhase += MathF.PI * 2f * 0.8f / _sampleRate;
        if (_chorusPhase > MathF.PI * 2f) _chorusPhase -= MathF.PI * 2f;

        // Створюємо плавне "плавання" звуку (швидше і жвавіше)
        _prngState = 1664525 * _prngState + 1013904223;
        float analogDrift = (((float)_prngState / uint.MaxValue) - 0.5f) * 0.04f;
        _chorusDrift = 0.998f * _chorusDrift + 0.002f * analogDrift;

        // Отримуємо два додаткових "голоси" з буфера пам'яті
        float delayMs1 = 20.0f + MathF.Sin(_chorusPhase) * 6.0f;
        float delayed1 = ReadFractionalDelay(delayMs1 * _sampleRate / 1000f);

        float delayMs2 = 25.0f + MathF.Cos(_chorusPhase + _chorusDrift) * 7.0f;
        float delayed2 = ReadFractionalDelay(delayMs2 * _sampleRate / 1000f);

        _delayBuffer[_delayWritePos] = x;
        _delayWritePos = (_delayWritePos + 1) & 4095;

        // Менше оригінального голосу, більше відлуння
        float wet = (x * 0.4f) + (delayed1 * 0.3f) + (delayed2 * 0.3f);
        return Math.Clamp(wet, -1.0f, 1.0f);
    }

    /// <summary>
    /// Безпечно читає минулий звук із кільцевого буфера.
    /// Використовує інтерполяцію, щоб зміна часу затримки не створювала тріску.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ReadFractionalDelay(float delaySamples)
    {
        float readPos = _delayWritePos - delaySamples;
        if (readPos < 0) readPos += 4096;

        int p1 = (int)readPos & 4095;
        int p2 = (p1 + 1) & 4095;
        float frac = readPos - (int)readPos;

        return (_delayBuffer[p1] * (1.0f - frac)) + (_delayBuffer[p2] * frac);
    }
}

/// <summary>
/// Інструменти для математичного викривлення форми хвилі (робить звук "аналоговим").
/// </summary>
file static class Saturation
{
    // М'яке обмеження гучності (як на касетній стрічці)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Tape(float x) => MathF.Tanh(x);

    // Асиметричне спотворення (теплий ламповий звук)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AsymmetricTube(float x) => x > 0 ? MathF.Tanh(x) : (MathF.Exp(x) - 1.0f);

    // Кубічне перевантаження (жорсткий дисторшн)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Cubic(float x)
    {
        float clamped = x > 1.0f ? 1.0f : (x < -1.0f ? -1.0f : x);
        return clamped - clamped * clamped * clamped / 3.0f;
    }
}