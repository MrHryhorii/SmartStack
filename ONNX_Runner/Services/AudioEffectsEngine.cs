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
    private const float GainTelephone = 1.8f;
    private const float GainVintageRadio = 1.6f;
    private const float GainVinylRecord = 1.2f;
    private const float GainRobot = 1.4f;
    private const float GainAlien = 1.4f;
    private const float GainMegaphone = 1.5f;
    private const float GainOverdrive = 0.85f;
    private const float GainWhisper = 1.6f;
    private const float GainArcade = 1.0f;

    // =================================================================
    // ЗМІННІ СТАНУ (DSP Пам'ять)
    // =================================================================
    private float _lfoPhase;
    private float _noiseState;
    private float _crackleDecay;
    private readonly float[] _delayBuffer = new float[2048];
    private int _delayPos;

    // =================================================================
    // ВБУДОВАНІ ФІЛЬТРИ (EQ)
    // =================================================================
    private BiQuadFilter? _highPass;
    private BiQuadFilter? _lowPass;
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

        // Ініціалізуємо еквалайзер тільки один раз при першому виклику стріму
        if (!_eqInitialized)
        {
            SetupEqualizer(effectType);
            _eqInitialized = true;
            _currentEffect = effectType;
        }

        // Головний цикл обробки семплів
        for (int i = 0; i < buffer.Length; i++)
        {
            float dry = buffer[i];

            // Спочатку працює еквалайзер (як у реальній студії)
            float eqSignal = dry;
            if (_highPass != null) eqSignal = _highPass.Transform(eqSignal);
            if (_lowPass != null) eqSignal = _lowPass.Transform(eqSignal);

            // Застосування художнього ефекту (сатурація, модуляція, шум)
            float wet = Process(_currentEffect, eqSignal);

            // Стрічкова сатурація для "склеювання" (Tape Saturation)
            wet = Saturation.Tape(wet);

            // Лінійна інтерполяція між чистим та обробленим сигналом
            buffer[i] = Mix(dry, wet, intensity);
        }
    }

    // =================================================================
    // НАЛАШТУВАННЯ ЕКВАЛАЙЗЕРІВ
    // =================================================================
    private void SetupEqualizer(VoiceEffectType type)
    {
        float nyquist = _sampleRate / 2.0f * 0.98f;
        float SafeFreq(float target) => Math.Min(target, nyquist);

        switch (type)
        {
            case VoiceEffectType.Telephone:
                _highPass = BiQuadFilter.HighPassFilter(_sampleRate, Math.Min(300f, nyquist - 100f), 1.2f);
                _lowPass = BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(3400f), 1.2f);
                break;
            case VoiceEffectType.VintageRadio:
                _highPass = BiQuadFilter.HighPassFilter(_sampleRate, Math.Min(400f, nyquist - 100f), 1.5f);
                _lowPass = BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(3500f), 1.5f);
                break;
            case VoiceEffectType.Megaphone:
                _highPass = BiQuadFilter.HighPassFilter(_sampleRate, Math.Min(500f, nyquist - 100f), 2.0f);
                _lowPass = BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(3000f), 2.0f);
                break;
            case VoiceEffectType.Overdrive:
                _highPass = BiQuadFilter.HighPassFilter(_sampleRate, Math.Min(600f, nyquist - 100f), 1.5f);
                _lowPass = BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(2800f), 1.5f);
                break;
            case VoiceEffectType.VinylRecord:
                _highPass = BiQuadFilter.HighPassFilter(_sampleRate, 80f, 0.707f);
                _lowPass = BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(12000f), 0.5f);
                break;
            case VoiceEffectType.Arcade:
                _highPass = BiQuadFilter.HighPassFilter(_sampleRate, Math.Min(300f, nyquist - 100f), 1.0f);
                _lowPass = BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(4000f), 1.0f);
                break;
            case VoiceEffectType.Whisper:
                _highPass = BiQuadFilter.HighPassFilter(_sampleRate, 250f, 0.707f);
                break;
            case VoiceEffectType.Robot:
                _highPass = BiQuadFilter.HighPassFilter(_sampleRate, 150f, 0.707f);
                _lowPass = BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(6000f), 0.707f);
                break;
            case VoiceEffectType.Alien:
                _lowPass = BiQuadFilter.LowPassFilter(_sampleRate, SafeFreq(8000f), 0.707f);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Mix(float dry, float wet, float t) => dry + (wet - dry) * t;

    // =================================================================
    // EFFECT CORE (Диспетчер ефектів з компенсацією гучності)
    // =================================================================
    private float Process(VoiceEffectType type, float x)
    {
        return type switch
        {
            VoiceEffectType.Telephone => Saturation.Transformer(x * 3.0f) * GainTelephone,
            VoiceEffectType.VintageRadio => Radio(x) * GainVintageRadio,
            VoiceEffectType.VinylRecord => Vinyl(x) * GainVinylRecord,
            VoiceEffectType.Megaphone => Saturation.Transformer(x * 4.0f) * GainMegaphone,
            VoiceEffectType.Robot => Robot(x) * GainRobot,
            VoiceEffectType.Overdrive => Saturation.Tube(x * 10.0f) * GainOverdrive,
            VoiceEffectType.Alien => Alien(x) * GainAlien,
            VoiceEffectType.Whisper => Whisper(x) * GainWhisper,
            VoiceEffectType.Arcade => Arcade(x) * GainArcade,
            _ => x
        };
    }

    // =================================================================
    // ІНДИВІДУАЛЬНІ АЛГОРИТМИ
    // =================================================================

    private float Radio(float x)
    {
        float sat = Saturation.Tube(x * 5f);

        float humStep = MathF.PI * 2f * 50f / _sampleRate;
        _lfoPhase += humStep;
        if (_lfoPhase > MathF.PI * 2f) _lfoPhase -= MathF.PI * 2f;
        float hum = MathF.Sin(_lfoPhase) * 0.015f;

        // Сайдчейн-компресія: шум стає тихішим, коли лунає голос (Ducking)
        float staticNoise = Noise.White(ref _noiseState) * 0.03f;
        float duckedNoise = Noise.Duck(sat, staticNoise);

        return (sat * 0.8f) + hum + duckedNoise;
    }

    private float Vinyl(float x)
    {
        // Аналогова теплота (Парні гармоніки: x + x^2)
        float drive = x * 1.2f;
        float warmth = drive + (drive * drive * 0.15f);
        float sat = Saturation.Tape(warmth);

        // Фізичний тріск голки
        if (Random.Shared.NextSingle() > 0.9994f)
            _crackleDecay = 1f;

        float click = (Random.Shared.NextSingle() * 2f - 1f) * _crackleDecay;
        _crackleDecay *= MathF.Exp(-1.0f / (_sampleRate * 0.004f)); // Згасання 4мс

        // Фонове тертя (Рожевий шум)
        float surfaceNoise = Noise.White(ref _noiseState) * 0.02f; // Low-pass вбудовано в Noise.White

        return (sat * 0.9f) + (click * 0.2f) + surfaceNoise;
    }

    private float Robot(float x)
    {
        float step = MathF.PI * 2f * 30f / _sampleRate;
        _lfoPhase += step;
        if (_lfoPhase > MathF.PI * 2f) _lfoPhase -= MathF.PI * 2f;

        float mod = MathF.Sin(_lfoPhase);
        return (x * 0.6f) + (x * mod * 0.6f);
    }

    private float Alien(float x)
    {
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

    private float Whisper(float x)
    {
        float white = Random.Shared.NextSingle() * 2f - 1f;

        // Високочастотний фільтр для імітації дихання
        _noiseState = 0.85f * _noiseState + 0.15f * white;
        float breath = white - _noiseState;

        // Шум дихання множимо на гучність голосу (динамічна огинаюча)
        return (x * 0.7f) + (breath * MathF.Abs(x) * 1.0f);
    }

    private static float Arcade(float x)
    {
        float crushed = MathF.Round(x * 3f) / 3f;
        return crushed;
    }
}

// =================================================================
// ДОПОМІЖНІ МОДУЛІ (HELPERS)
// =================================================================

file static class Saturation
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Tube(float x)
    {
        // Асиметричне лампове спотворення (Tube Asymmetry)
        return x > 0 ? MathF.Tanh(x) : (MathF.Exp(x) - 1.0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Tape(float x)
    {
        // М'яка стрічкова сатурація для згладжування піків
        return x / (1f + MathF.Abs(x));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Transformer(float x)
    {
        // Жорстке симетричне спотворення (Телефони, Мегафони)
        return x > 0 ? MathF.Tanh(x) : MathF.Max(x, -0.8f);
    }
}

file static class Noise
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float White(ref float state)
    {
        // Генерація теплого шуму з простим Low-Pass фільтром
        float w = Random.Shared.NextSingle() * 2f - 1f;
        state = 0.95f * state + 0.05f * w;
        return state;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Duck(float sample, float noise)
    {
        // Ducking: зменшуємо рівень шуму, коли голос звучить голосно
        float duck = MathF.Max(0f, 1f - MathF.Abs(sample) * 1.5f);
        return noise * duck;
    }
}