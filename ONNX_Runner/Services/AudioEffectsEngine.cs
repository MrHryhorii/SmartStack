using ONNX_Runner.Models;
using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services;

public class AudioEffectsEngine(EffectsSettings config, int sampleRate)
{
    private readonly EffectsSettings _globalConfig = config;
    private readonly int _sampleRate = sampleRate;

    // =================================================================
    // КОНСТАНТИ БАЛАНСУВАННЯ ГУЧНОСТІ (MAKEUP GAIN)
    // =================================================================
    // Компенсують втрату (або надлишок) енергії після фільтрації та спотворень, 
    // щоб усі ефекти звучали на одному рівні гучності (Unity Gain).

    private const float GainTelephone = 1.8f;
    private const float GainVintageRadio = 1.6f;
    private const float GainVinylRecord = 1.2f;
    private const float GainRobot = 1.4f;
    private const float GainAlien = 1.4f;
    private const float GainMegaphone = 1.5f;
    private const float GainOverdrive = 0.85f; // Зменшуємо, бо перегруз занадто гучний
    private const float GainWhisper = 1.6f;
    private const float GainArcade = 1.0f;

    // =================================================================
    // ЗМІННІ СТАНУ (DSP Меморі)
    // =================================================================

    // --- VINYL RECORD (Аналогова пластинка) ---
    private float _crackleDecay = 0f;
    private float _pinkNoiseState = 0f; // Для генерації м'якого тертя

    // --- VINTAGE RADIO (Ламповий приймач) ---
    private float _acHumPhase = 0f; // Для гудіння трансформатора
    private float _radioStaticState = 0f;

    // --- СИНТЕТИКА (LFO Генератори) ---
    private float _lfoPhase = 0f;

    // --- WHISPER (Шепіт) ---
    private float _breathNoiseState = 0f; // Фільтр для перетворення радіошуму на звук дихання

    // --- ALIEN (Прибулець) ---
    private readonly float[] _alienBuffer = new float[2048];
    private int _alienWritePos = 0;

    public void ApplyEffect(Span<float> buffer, string? requestedEffect = null, float? requestedIntensity = null)
    {
        if (!_globalConfig.EnableGlobalEffects) return;

        string effectString = requestedEffect ?? _globalConfig.DefaultEffect;
        if (!Enum.TryParse(effectString, true, out VoiceEffectType effectType) || effectType == VoiceEffectType.None)
        {
            return;
        }

        float intensity = requestedIntensity ?? _globalConfig.DefaultIntensity;
        intensity = Math.Clamp(intensity, 0.0f, 1.0f);

        if (intensity <= 0.001f) return;

        switch (effectType)
        {
            case VoiceEffectType.Telephone:
                for (int i = 0; i < buffer.Length; i++) buffer[i] = Mix(buffer[i], ProcessTelephone(buffer[i]), intensity);
                break;
            case VoiceEffectType.VintageRadio:
                for (int i = 0; i < buffer.Length; i++) buffer[i] = Mix(buffer[i], ProcessVintageRadio(buffer[i]), intensity);
                break;
            case VoiceEffectType.VinylRecord:
                for (int i = 0; i < buffer.Length; i++) buffer[i] = Mix(buffer[i], ProcessVinylRecord(buffer[i]), intensity);
                break;
            case VoiceEffectType.Megaphone:
                for (int i = 0; i < buffer.Length; i++) buffer[i] = Mix(buffer[i], ProcessMegaphone(buffer[i]), intensity);
                break;
            case VoiceEffectType.Robot:
                for (int i = 0; i < buffer.Length; i++) buffer[i] = Mix(buffer[i], ProcessRobot(buffer[i]), intensity);
                break;
            case VoiceEffectType.Overdrive:
                for (int i = 0; i < buffer.Length; i++) buffer[i] = Mix(buffer[i], ProcessOverdrive(buffer[i]), intensity);
                break;
            case VoiceEffectType.Alien:
                for (int i = 0; i < buffer.Length; i++) buffer[i] = Mix(buffer[i], ProcessAlien(buffer[i]), intensity);
                break;
            case VoiceEffectType.Whisper:
                for (int i = 0; i < buffer.Length; i++) buffer[i] = Mix(buffer[i], ProcessWhisper(buffer[i]), intensity);
                break;
            case VoiceEffectType.Arcade:
                for (int i = 0; i < buffer.Length; i++) buffer[i] = Mix(buffer[i], ProcessArcade(buffer[i]), intensity);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Mix(float dry, float wet, float intensity) => (dry * (1.0f - intensity)) + (wet * intensity);

    // =================================================================
    // АЛГОРИТМИ ЕФЕКТІВ
    // =================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessTelephone(float sample)
    {
        float carbon = sample > 0 ? MathF.Tanh(sample * 3.0f) : MathF.Max(sample * 3.0f, -0.8f);
        return MathF.Tanh(carbon) * GainTelephone;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ProcessVintageRadio(float sample)
    {
        float drive = sample * 5.0f;
        float processed = drive > 0 ? MathF.Tanh(drive) : (MathF.Exp(drive) - 1.0f);

        float humPhaseStep = MathF.PI * 2.0f * 50.0f / _sampleRate;
        _acHumPhase += humPhaseStep;
        if (_acHumPhase > MathF.PI * 2.0f) _acHumPhase -= MathF.PI * 2.0f;
        float hum = MathF.Sin(_acHumPhase) * 0.015f;

        float white = Random.Shared.NextSingle() * 2f - 1f;
        _radioStaticState = 0.9f * _radioStaticState + 0.1f * white;

        return MathF.Tanh((processed * 0.8f) + hum + (_radioStaticState * 0.02f)) * GainVintageRadio;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ProcessVinylRecord(float sample)
    {
        // АНАЛОГОВА ТЕПЛОТА (Warmth - 2nd Harmonic Distortion)
        float drive = sample * 1.2f;
        float warmth = drive + (drive * drive * 0.15f);
        float processed = MathF.Tanh(warmth);

        // ФІЗИЧНИЙ ТРІСК (Pops & Clicks)
        if (Random.Shared.NextSingle() > 0.9994f)
        {
            _crackleDecay = 1.0f;
        }

        float clickNoise = Random.Shared.NextSingle() * 2f - 1f;
        float crackle = clickNoise * _crackleDecay;

        // Згасання 4 мілісекунди
        _crackleDecay *= MathF.Exp(-1.0f / (_sampleRate * 0.004f));

        // ФОНОВЕ ТЕРТЯ (Surface Noise & Rumble)
        float white = Random.Shared.NextSingle() * 2f - 1f;
        _pinkNoiseState = 0.98f * _pinkNoiseState + 0.02f * white;

        float surfaceNoise = _pinkNoiseState * 0.03f;

        float mix = (processed * 0.9f) + (crackle * 0.2f) + surfaceNoise;
        return MathF.Tanh(mix) * GainVinylRecord;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ProcessRobot(float sample)
    {
        float lfoFreq = 30.0f;
        float phaseStep = MathF.PI * 2.0f * lfoFreq / _sampleRate;

        _lfoPhase += phaseStep;
        if (_lfoPhase > MathF.PI * 2.0f) _lfoPhase -= MathF.PI * 2.0f;

        float lfo = MathF.Sin(_lfoPhase);

        return MathF.Tanh((sample * 0.6f) + (sample * lfo * 0.6f)) * GainRobot;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ProcessAlien(float sample)
    {
        _alienBuffer[_alienWritePos] = sample;

        float lfoFreq = 2.0f;
        float phaseStep = MathF.PI * 2.0f * lfoFreq / _sampleRate;
        _lfoPhase += phaseStep;
        if (_lfoPhase > MathF.PI * 2.0f) _lfoPhase -= MathF.PI * 2.0f;

        float delayMs = 15.0f + MathF.Sin(_lfoPhase) * 10.0f;
        int delaySamples = (int)(delayMs * _sampleRate / 1000.0f);

        int readPos = _alienWritePos - delaySamples;
        if (readPos < 0) readPos += 2048;

        float delayedSample = _alienBuffer[readPos];
        _alienWritePos = (_alienWritePos + 1) % 2048;

        return MathF.Tanh((sample * 0.6f) + (delayedSample * 0.6f)) * GainAlien;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessMegaphone(float sample)
    {
        float processed = sample * 4.0f;
        if (processed > 0) processed = MathF.Tanh(processed);
        else processed = MathF.Max(processed, -0.6f);

        return processed * GainMegaphone;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessOverdrive(float sample)
    {
        float processed = sample * 10.0f;
        if (processed > 1.0f) processed = 1.0f;
        else if (processed < -1.0f) processed = -1.0f;
        else processed -= MathF.Pow(processed, 3) / 3.0f;

        return processed * GainOverdrive;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ProcessWhisper(float sample)
    {
        float white = Random.Shared.NextSingle() * 2f - 1f;

        _breathNoiseState = 0.85f * _breathNoiseState + 0.15f * white;
        float breath = white - _breathNoiseState;

        float raspy = (sample * 0.7f) + (breath * MathF.Abs(sample) * 1.0f);

        return MathF.Tanh(raspy) * GainWhisper;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessArcade(float sample)
    {
        float processed = sample * 6.0f;
        if (processed > 1.0f) processed = 1.0f;
        else if (processed < -1.0f) processed = -1.0f;
        float bitDepth = 3f;

        return MathF.Round(processed * bitDepth) / bitDepth * GainArcade;
    }
}