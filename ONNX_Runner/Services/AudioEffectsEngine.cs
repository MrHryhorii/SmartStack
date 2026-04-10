using ONNX_Runner.Models;
using System.Runtime.CompilerServices;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

public class AudioEffectsEngine(EffectsSettings config, int sampleRate)
{
    private readonly EffectsSettings _config = config;
    private readonly int _sampleRate = sampleRate;

    // =================================================================
    // КОНСТАНТИ БАЛАНСУВАННЯ ГУЧНОСТІ (MAKEUP GAIN)
    // =================================================================
    private const float GainTelephone = 1.5f;
    private const float GainVintageRadio = 1.6f;
    private const float GainVinylRecord = 1.1f;
    private const float GainMegaphone = 1.4f;
    private const float GainRobot = 1.2f;
    private const float GainOverdrive = 0.85f;
    private const float GainAlien = 1.3f;
    private const float GainWhisper = 1.8f;
    private const float GainArcade = 1.2f;

    // =================================================================
    // СТАН DSP (Пам'ять системи)
    // =================================================================
    private float _lfoPhase;
    private float _lfoPhase2;       // Для вторинної модуляції (Flutter)
    private float _whistlePhase;    // Фаза для гетеродинного AM-свисту
    private NoiseState _noiseState;
    private float _crackleDecay;

    // Єдиний буфер для всіх просторових ефектів (Вініл, Прибулець)
    private readonly float[] _delayBuffer = new float[4096];
    private int _delayPos;

    // =================================================================
    // КАСКАД ФІЛЬТРІВ (EQ)
    // =================================================================
    private readonly List<BiQuadFilter> _filters = new();
    private VoiceEffectType _currentEffect = VoiceEffectType.None;
    private bool _eqInitialized = false;

    public void ApplyEffect(Span<float> buffer, string? requestedEffect = null, float? requestedIntensity = null)
    {
        if (!_config.EnableGlobalEffects) return;

        string effectString = requestedEffect ?? _config.DefaultEffect;
        if (!Enum.TryParse(effectString, true, out VoiceEffectType effectType) || effectType == VoiceEffectType.None)
            return;

        float intensity = requestedIntensity ?? _config.DefaultIntensity;
        intensity = Math.Clamp(intensity, 0f, 1f);

        if (intensity <= 0.001f) return;

        // Ініціалізація або перемикання еквалайзера
        if (!_eqInitialized || _currentEffect != effectType)
        {
            SetupEqualizer(effectType);
            _eqInitialized = true;
            _currentEffect = effectType;
        }

        // Обробка кожного семплу
        for (int i = 0; i < buffer.Length; i++)
        {
            float dry = buffer[i];
            float eqSignal = dry;

            // Каскадна обробка фільтрами (Формування АЦХ як у фізичному корпусі)
            foreach (var filter in _filters)
            {
                eqSignal = filter.Transform(eqSignal);
            }

            // Генерація художнього спотворення та модуляцій
            float wet = Process(_currentEffect, eqSignal);

            // Захисна магнітна сатурація (Склеює мікс і запобігає кліппінгу)
            wet = Saturation.Tape(wet);

            // Змішування (Dry/Wet)
            buffer[i] = Mix(dry, wet, intensity);
        }
    }

    private void SetupEqualizer(VoiceEffectType type)
    {
        _filters.Clear();
        // Межа Найквіста (максимальна частота, яку може відтворити поточний SampleRate)
        float nyquist = _sampleRate / 2.0f * 0.98f;
        float SafeFreq(float target) => Math.Min(target, nyquist);

        switch (type)
        {
            case VoiceEffectType.Telephone:
                // Вугільний мікрофон Type II: різкий зріз низів і подвійний резонанс на середині
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 300f, 0.707f));
                _filters.Add(BiQuadFilter.PeakingEQ(_sampleRate, SafeFreq(2000f), 3.0f, 15f));
                _filters.Add(BiQuadFilter.PeakingEQ(_sampleRate, SafeFreq(3900f), 2.5f, 21f));
                _filters.Add(BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(4000f), 1.2f));
                break;

            case VoiceEffectType.VintageRadio:
                // Стандарт AM-трансляції 1940-х (дуже вузька смуга 60-4500 Гц)
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 60f, 0.707f));
                _filters.Add(BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(4500f), 2.0f));
                _filters.Add(BiQuadFilter.PeakingEQ(_sampleRate, SafeFreq(2500f), 1.0f, 4f));
                break;

            case VoiceEffectType.VinylRecord:
                // RIAA крива (спрощена): зріз інфрабасу (від гулу мотора) та згладжування верхів
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 80f, 0.5f));
                _filters.Add(BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(10000f), 0.707f));
                break;

            case VoiceEffectType.Megaphone:
                // Металевий рупор: величезний резонанс на 1-3 кГц, відсутність басу
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 500f, 2.0f));
                _filters.Add(BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(3000f), 2.0f));
                break;

            case VoiceEffectType.Overdrive:
                // Рація піхоти: смуга обмежена для максимальної розбірливості в шумі бою
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 600f, 1.5f));
                _filters.Add(BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(2800f), 1.5f));
                break;

            case VoiceEffectType.Arcade:
                // Імітація дешевого п'єзодинаміка (GameBoy/NES)
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 300f, 1.0f));
                _filters.Add(BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(4000f), 2.0f));
                break;

            case VoiceEffectType.Whisper:
                // Зріз низьких частот грудної клітини (залишаємо лише повітря)
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 250f, 0.707f));
                break;

            case VoiceEffectType.Robot:
                _filters.Add(BiQuadFilter.HighPassFilter(_sampleRate, 150f, 0.707f));
                _filters.Add(BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(6000f), 0.707f));
                break;

            case VoiceEffectType.Alien:
                _filters.Add(BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(8000f), 0.707f));
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Mix(float dry, float wet, float t) => dry + (wet - dry) * t;

    // =================================================================
    // ДИСПЕТЧЕР ЕФЕКТІВ
    // =================================================================
    private float Process(VoiceEffectType type, float x)
    {
        return type switch
        {
            VoiceEffectType.Telephone => Telephone(x) * GainTelephone,
            VoiceEffectType.VintageRadio => Radio(x) * GainVintageRadio,
            VoiceEffectType.VinylRecord => Vinyl(x) * GainVinylRecord,
            VoiceEffectType.Megaphone => Saturation.Transformer(x * 4.0f) * GainMegaphone,
            VoiceEffectType.Overdrive => Saturation.Tube(x * 10.0f) * GainOverdrive,
            VoiceEffectType.Robot => Robot(x) * GainRobot,
            VoiceEffectType.Alien => Alien(x) * GainAlien,
            VoiceEffectType.Whisper => Whisper(x) * GainWhisper,
            VoiceEffectType.Arcade => Arcade(x) * GainArcade,
            _ => x
        };
    }

    // =================================================================
    // ФІЗИЧНІ МОДЕЛІ АПАРАТУРИ
    // =================================================================

    private static float Telephone(float x)
    {
        // Carbon Mic (Вугільний мікрофон) асиметрично реагує на тиск.
        // Замість Чебишева (який дає зміщення DC), використовуємо безпечну асиметрію.
        float carbon = x > 0 ? MathF.Tanh(x * 2.5f) : MathF.Max(x * 2.5f, -0.8f);
        return carbon;
    }

    private float Radio(float x)
    {
        // Гетеродинний свист (Interference) від сусідніх AM-станцій (10 kHz)
        float whistleFreq = 10000f;
        _whistlePhase += MathF.PI * 2f * whistleFreq / _sampleRate;
        float whistle = MathF.Sin(_whistlePhase) * 0.005f;

        // Іоносферне завмирання (Fading) - сигнал "плаває" через відбиття від атмосфери
        _lfoPhase += MathF.PI * 2f * 0.05f / _sampleRate;
        float fading = 0.8f + MathF.Sin(_lfoPhase) * 0.2f;

        float sat = Saturation.AsymmetricTube(x * fading * 4f);

        // AM-ефір має рожевий шум, який приглушується (Ducking), коли диктор говорить голосно
        float noise = Noise.Pink(ref _noiseState) * 0.04f;
        float duckedNoise = Noise.Duck(sat, noise);

        return sat + whistle + duckedNoise;
    }

    private float Vinyl(float x)
    {
        // Механічна детонація мотора програвача
        // Wow (повільне плавання) 0.5Hz + Flutter (швидка вібрація) 5Hz
        _lfoPhase += MathF.PI * 2f * 0.5f / _sampleRate;
        _lfoPhase2 += MathF.PI * 2f * 5.0f / _sampleRate;

        float wow = MathF.Sin(_lfoPhase) * 15f;
        float flutter = MathF.Sin(_lfoPhase2) * 5f;
        float totalDelay = 20f + wow + flutter; // Затримка в семплах

        // ЗАПИС у кільцевий буфер
        _delayBuffer[_delayPos] = x;

        // ЧИТАННЯ з дробовою затримкою (Лінійна Інтерполяція)
        // Це запобігає ефекту "цифрового піску" при плаванні тону
        float readPos = _delayPos - totalDelay;
        if (readPos < 0) readPos += _delayBuffer.Length;

        int pos1 = (int)readPos;
        int pos2 = (pos1 + 1) % _delayBuffer.Length;
        float frac = readPos - pos1; // Дробова частина

        float pitchShifted = (_delayBuffer[pos1] * (1 - frac)) + (_delayBuffer[pos2] * frac);

        _delayPos = (_delayPos + 1) % _delayBuffer.Length;

        // Тріск пилинок (Модель імпульсів Пуассона)
        if (Random.Shared.NextSingle() > 0.9998f)
            _crackleDecay = (Random.Shared.NextSingle() > 0.5f ? 1f : -1f) * 0.5f;

        float pop = _crackleDecay;
        _crackleDecay *= 0.1f; // Дуже швидке згасання ("сухий" клац)

        // Тепла лампова сатурація + низькочастотний гул мотора (Brown noise)
        return Saturation.Tape(pitchShifted * 1.2f) + pop + (Noise.Brown(ref _noiseState) * 0.02f);
    }

    private float Robot(float x)
    {
        // Класична кільцева модуляція (Ring Modulation 30Hz) - ефект Далеків
        float step = MathF.PI * 2f * 30f / _sampleRate;
        _lfoPhase += step;
        if (_lfoPhase > MathF.PI * 2f) _lfoPhase -= MathF.PI * 2f;

        float mod = MathF.Sin(_lfoPhase);
        return (x * 0.6f) + (x * mod * 0.6f);
    }

    private float Alien(float x)
    {
        // Просторовий Flanger (Металева труба)
        _delayBuffer[_delayPos] = x;

        float step = MathF.PI * 2f * 2f / _sampleRate;
        _lfoPhase += step;
        if (_lfoPhase > MathF.PI * 2f) _lfoPhase -= MathF.PI * 2f;

        float delayMs = 15f + MathF.Sin(_lfoPhase) * 10f;
        int samples = (int)(delayMs * _sampleRate / 1000f);

        int read = _delayPos - samples;
        if (read < 0) read += _delayBuffer.Length;

        float delayed = _delayBuffer[read];
        _delayPos = (_delayPos + 1) % _delayBuffer.Length;

        return (x * 0.6f) + (delayed * 0.6f);
    }

    private static float Whisper(float x)
    {
        // Source-filter: Шум пропускаємо через динаміку голосу (Vocoding ефект)
        float noise = Noise.White() * 0.3f;
        return (x * 0.3f) + (noise * MathF.Abs(x) * 2.0f);
    }

    private static float Arcade(float x)
    {
        // Квантування до 4-х біт (16 рівнів) для збереження розбірливості мови
        float levels = 16f;
        return MathF.Round(x * levels) / levels;
    }
}

// =================================================================
// ДОПОМІЖНІ DSP-МОДУЛІ
// =================================================================

public struct NoiseState
{
    public float B0, B1, B2, B3, B4, B5, B6, Brown;
}

file static class Saturation
{
    // Симетрична лампа (Кубічний шейпер): ідеально для жорсткого Овердрайву/Рації
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Tube(float x)
    {
        // Безпечний кліппінг перед кубічною формулою, щоб хвиля не "вибухнула"
        float clamped = x > 1.0f ? 1.0f : (x < -1.0f ? -1.0f : x);
        return clamped - clamped * clamped * clamped / 3.0f;
    }
    // Асиметрична лампа класу А: "утеплює" звук парними гармоніками
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AsymmetricTube(float x) => x > 0 ? MathF.Tanh(x) : (MathF.Exp(x) - 1.0f);

    // Симетрична стрічка: згладжує піки (захист від кліппінгу)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Tape(float x) => MathF.Tanh(x);

    // Трансформатор (Діодний кліппінг): агресивний зріз для телефонів і рацій
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Transformer(float x) => x > 0 ? MathF.Tanh(x * 2f) : MathF.Max(x * 2f, -1f);
}

file static class Noise
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float White() => Random.Shared.NextSingle() * 2f - 1f;

    // Рожевий шум (спадає -3 дБ/окт). Ідеальний для ефіру та тертя.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Pink(ref NoiseState state)
    {
        float white = White();
        state.B0 = 0.99886f * state.B0 + white * 0.0555179f;
        state.B1 = 0.99332f * state.B1 + white * 0.0750759f;
        state.B2 = 0.96900f * state.B2 + white * 0.1538520f;
        state.B3 = 0.86650f * state.B3 + white * 0.3104856f;
        state.B4 = 0.55000f * state.B4 + white * 0.5329522f;
        state.B5 = -0.7616f * state.B5 - white * 0.0168980f;
        float pink = state.B0 + state.B1 + state.B2 + state.B3 + state.B4 + state.B5 + state.B6 + white * 0.5362f;
        state.B6 = white * 0.115926f;
        return pink * 0.11f;
    }

    // Коричневий шум (спадає -6 дБ/окт). Ідеально для гулу мотора програвача.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Brown(ref NoiseState state)
    {
        float white = White();
        state.Brown = (state.Brown + (0.02f * white)) / 1.02f;
        return state.Brown * 3.5f;
    }

    // Ducking (Приглушення): Зменшує гучність шуму, коли голос звучить голосно
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Duck(float sample, float noise)
    {
        float duck = MathF.Max(0f, 1f - MathF.Abs(sample) * 1.5f);
        return noise * duck;
    }
}