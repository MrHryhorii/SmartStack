using ONNX_Runner.Models;
using System.Runtime.CompilerServices;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Server-side spatial acoustics engine.
/// Uses Freeverb/Schroeder algorithms for zero-allocation room simulation.
///
/// Environments Overview:
/// - LivingRoom: Short, warm reverb (~0.3s).
/// - ConcreteHall: Long, bright reverb (~1.5s).
/// - Forest: Discrete 250ms delay echo (no reverb).
/// - Underwater: Dense reverb + 40ms slapback + 750Hz LP.
/// - Cave: Reverb + dual pre-delay (15/38ms) + 80Hz boost.
/// - Stage: Reverb + 12ms lateral echo + 22ms pre-delay + 180Hz boost.
/// - InnerVoice: Intracranial comb cluster (5-14ms) + skull EQ (no chest resonance)
///   + dynamic depth LP for inward focus.
/// - Dungeon: Short reverb + 7ms flutter echo + 200Hz room resonance.
///
/// Note: environments are mutually exclusive (one space at a time, no blending
/// between spaces), so Stage, InnerVoice, and Dungeon safely share _preDelay —
/// only one of their Setup configurations is ever active at once.
/// </summary>
public class SpatialEffectsEngine
{
    private readonly int _sampleRate;

    // =========================================================================
    // FREEVERB PRIMITIVES
    // =========================================================================

    private readonly CombFilter[] _combs;
    private readonly AllPassFilter[] _allPasses;

    // Separate short comb cluster exclusively for InnerVoice intracranial resonance.
    private readonly CombFilter[] _innerCombs;

    // =========================================================================
    // DELAY BUFFERS (Isolated to prevent acoustic bleed-over)
    // =========================================================================

    private readonly DelayBuffer _forestDelay;
    private readonly DelayBuffer _underwaterDelay;
    private readonly DelayBuffer _caveNearDelay;
    private readonly DelayBuffer _caveFarDelay;
    private readonly DelayBuffer _stageEarlyDelay;

    // Shared pre-delay buffer for Stage, InnerVoice, and Dungeon.
    private readonly DelayBuffer _preDelay;

    // Dedicated buffer for Dungeon's parallel wall flutter echo.
    private readonly DelayBuffer _dungeonFlutter;

    // =========================================================================
    // EQ FILTERS (Instantiated in Setup when needed)
    // =========================================================================

    private BiQuadFilter? _environmentEq;
    private BiQuadFilter? _reverbHpFilter;
    private BiQuadFilter? _reverbLpFilter;

    private BiQuadFilter? _caveSubBoost;
    private BiQuadFilter? _caveHfRolloff;

    private BiQuadFilter? _stageBassBoost;
    private BiQuadFilter? _stageHfAbsorption;

    private BiQuadFilter? _innerVoiceHp;
    private BiQuadFilter? _innerVoicePresenceCut;
    private BiQuadFilter? _innerVoiceNasalBoost;

    // Dynamic depth LP (2-pole, 12dB/oct). Manually cascaded to avoid BiQuad allocations.
    private float _innerVoiceLpState1;
    private float _innerVoiceLpState2;

    // Cutoff: 5500Hz (transparent) -> 2500Hz (muffled "hands-over-ears" focus).
    private float _innerVoiceLpCoeffMin;
    private float _innerVoiceLpCoeffMax;

    // Resonance: adds a "closed-box" presence bump near the cutoff at high mix values.
    private float _innerVoiceResonanceMin;
    private float _innerVoiceResonanceMax;

    private BiQuadFilter? _dungeonRoomTone;
    private BiQuadFilter? _dungeonHfDamp;

    // =========================================================================
    // PRE-COMPUTED DELAY SIZES (Samples)
    // =========================================================================

    private readonly float _forestDelaySamples;
    private readonly float _underwaterDelaySamples;

    private float _caveNearDelaySamples;
    private float _caveFarDelaySamples;
    private float _stageEarlyDelaySamples;
    private float _stagePreDelaySamples;
    private float _innerVoiceTap1Samples;
    private float _innerVoiceTap2Samples;
    private float _innerVoiceTap3Samples;
    private float _dungeonPreDelaySamples;
    private float _dungeonFlutterDelaySamples;

    // =========================================================================
    // BRANCH-HOISTING FLAGS
    // =========================================================================

    private bool _hasReverbEq;
    private bool _hasCaveEq;
    private bool _hasStageEq;
    private bool _hasInnerVoiceEq;
    private bool _hasDungeonEq;

    private SpatialEnvironment _current = SpatialEnvironment.None;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public SpatialEffectsEngine(int sampleRate)
    {
        _sampleRate = sampleRate;
        _forestDelaySamples = 0.25f * sampleRate;
        _underwaterDelaySamples = 0.04f * sampleRate;

        float scale = sampleRate / 44100f;
        int ScaleToPrime(int baseSize) => GetNextPrime((int)(baseSize * scale));

        // Standard 8-comb Freeverb topology.
        _combs =
        [
            new(ScaleToPrime(1116)), new(ScaleToPrime(1188)),
            new(ScaleToPrime(1277)), new(ScaleToPrime(1356)),
            new(ScaleToPrime(1422)), new(ScaleToPrime(1491)),
            new(ScaleToPrime(1557)), new(ScaleToPrime(1617))
        ];

        // 4 series all-pass stages for phase diffusion.
        _allPasses =
        [
            new(ScaleToPrime(225)), new(ScaleToPrime(341)),
            new(ScaleToPrime(441)), new(ScaleToPrime(556))
        ];

        // 3 short combs for InnerVoice.
        _innerCombs =
        [
            new(ScaleToPrime((int)(0.005f * sampleRate))),
            new(ScaleToPrime((int)(0.009f * sampleRate))),
            new(ScaleToPrime((int)(0.014f * sampleRate)))
        ];

        _forestDelay = new DelayBuffer(32768);      // ~680ms
        _underwaterDelay = new DelayBuffer(8192);   // ~170ms
        _caveNearDelay = new DelayBuffer(4096);     // ~85ms
        _caveFarDelay = new DelayBuffer(4096);      // ~85ms
        _stageEarlyDelay = new DelayBuffer(2048);   // ~42ms
        _preDelay = new DelayBuffer(2048);          // ~42ms
        _dungeonFlutter = new DelayBuffer(512);     // ~10ms
    }

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    public void Reset()
    {
        foreach (var c in _combs) c.Clear();
        foreach (var c in _innerCombs) c.Clear();
        foreach (var a in _allPasses) a.Clear();

        _forestDelay.Clear();
        _underwaterDelay.Clear();
        _caveNearDelay.Clear();
        _caveFarDelay.Clear();
        _stageEarlyDelay.Clear();
        _preDelay.Clear();
        _dungeonFlutter.Clear();

        _innerVoiceLpState1 = 0f;
        _innerVoiceLpState2 = 0f;
    }

    /// <summary>
    /// Processes audio in-place. Uses unswitched loops for zero per-sample branching.
    /// Applies quadratic curve (mix²) for natural perceptual intensity scaling.
    /// </summary>
    public void ApplyEnvironment(Span<float> buffer, SpatialEnvironment env, float mix)
    {
        if (env == SpatialEnvironment.None || mix <= 0.001f)
            return;

        if (_current != env)
        {
            Setup(env);
            Reset();
            _current = env;
        }

        // Quadratic curve: finer control at low mix values.
        float curvedMix = mix * mix;

        switch (env)
        {
            case SpatialEnvironment.LivingRoom:
            case SpatialEnvironment.ConcreteHall:
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = AlgorithmicReverb(dry);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, curvedMix));
                }
                break;

            case SpatialEnvironment.Forest:
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = ForestEcho(dry);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, curvedMix));
                }
                break;

            case SpatialEnvironment.Underwater:
                bool hasUnderwaterEq = _environmentEq != null;
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = Underwater(dry);
                    if (hasUnderwaterEq) wet = _environmentEq!.Transform(wet);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, curvedMix));
                }
                break;

            case SpatialEnvironment.Cave:
                bool hasCaveEq = _hasCaveEq;
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = CaveReverb(dry, hasCaveEq);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, curvedMix));
                }
                break;

            case SpatialEnvironment.Stage:
                bool hasStageEq = _hasStageEq;
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = StageReverb(dry, hasStageEq);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, curvedMix));
                }
                break;

            case SpatialEnvironment.InnerVoice:
                bool hasInnerEq = _hasInnerVoiceEq;

                // HOISTING: depth-filter coefficients and resonance amount are interpolated
                // once per buffer against curvedMix, not once per sample.
                float lpCoeff = Dsp.Lerp(_innerVoiceLpCoeffMin, _innerVoiceLpCoeffMax, curvedMix);
                float resonance = Dsp.Lerp(_innerVoiceResonanceMin, _innerVoiceResonanceMax, curvedMix);
                float outputGain = Dsp.Lerp(0.85f, 0.55f, curvedMix);

                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = InnerVoice(dry, hasInnerEq, lpCoeff, resonance, outputGain);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, curvedMix));
                }
                break;

            case SpatialEnvironment.Dungeon:
                bool hasDungeonEq = _hasDungeonEq;
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = DungeonReverb(dry, hasDungeonEq);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, curvedMix));
                }
                break;

            default:
                break;
        }
    }

    // =========================================================================
    // SETUP
    // =========================================================================

    private void Setup(SpatialEnvironment env)
    {
        _environmentEq = null;
        _reverbHpFilter = null;
        _reverbLpFilter = null;
        _caveSubBoost = null;
        _caveHfRolloff = null;
        _stageBassBoost = null;
        _stageHfAbsorption = null;
        _innerVoiceHp = null;
        _innerVoicePresenceCut = null;
        _innerVoiceNasalBoost = null;
        _dungeonRoomTone = null;
        _dungeonHfDamp = null;

        _hasReverbEq = false;
        _hasCaveEq = false;
        _hasStageEq = false;
        _hasInnerVoiceEq = false;
        _hasDungeonEq = false;

        float nyq = _sampleRate * 0.45f;
        float Safe(float f) => Math.Min(f, nyq);

        switch (env)
        {
            case SpatialEnvironment.LivingRoom:
                ConfigureCombs(0.70f, 0.65f);
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(160f), 0.707f);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(6500f), 0.707f);
                _hasReverbEq = true;
                break;

            case SpatialEnvironment.ConcreteHall:
                ConfigureCombs(0.88f, 0.15f);
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(160f), 0.707f);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(6500f), 0.707f);
                _hasReverbEq = true;
                break;

            case SpatialEnvironment.Underwater:
                ConfigureCombs(0.85f, 0.90f);
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(160f), 0.707f);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(6500f), 0.707f);
                _hasReverbEq = true;
                // Raised from 450Hz to 750Hz to preserve vocal articulation
                _environmentEq = BiQuadFilter.LowPassFilter(_sampleRate, Safe(750f), 0.707f);
                break;

            case SpatialEnvironment.Cave:
                ConfigureCombs(0.94f, 0.05f);
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(60f), 0.707f);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(8000f), 0.707f);
                _hasReverbEq = true;

                _caveSubBoost = BiQuadFilter.PeakingEQ(_sampleRate, Safe(80f), 0.7f, 5.0f);
                _caveHfRolloff = BiQuadFilter.LowPassFilter(_sampleRate, Safe(3500f), 0.707f);
                _hasCaveEq = true;

                _caveNearDelaySamples = 0.015f * _sampleRate;   // 15ms near wall
                _caveFarDelaySamples = 0.038f * _sampleRate;    // 38ms far wall
                break;

            case SpatialEnvironment.Stage:
                ConfigureCombs(0.82f, 0.35f);
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(120f), 0.707f);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(8000f), 0.707f);
                _hasReverbEq = true;

                _stageBassBoost = BiQuadFilter.PeakingEQ(_sampleRate, Safe(180f), 1.2f, 3.5f);
                _stageHfAbsorption = BiQuadFilter.LowPassFilter(_sampleRate, Safe(6000f), 0.707f);
                _hasStageEq = true;

                _stageEarlyDelaySamples = 0.012f * _sampleRate; // 12ms lateral
                _stagePreDelaySamples = 0.022f * _sampleRate;   // 22ms gap
                break;

            case SpatialEnvironment.InnerVoice:
                foreach (var c in _innerCombs)
                {
                    c.Feedback = 0.25f;
                    c.Damp = Dsp.ScaleCoeff(0.30f, _sampleRate);
                }

                _innerVoiceHp = BiQuadFilter.HighPassFilter(_sampleRate, Safe(400f), 1.2f);
                // Presence cut moved up to 2500Hz for a more natural "inside the head" feel
                _innerVoicePresenceCut = BiQuadFilter.PeakingEQ(_sampleRate, Safe(2500f), 1.2f, -4.0f);
                _innerVoiceNasalBoost = BiQuadFilter.PeakingEQ(_sampleRate, Safe(800f), 1.2f, 2.5f);
                _hasInnerVoiceEq = true;

                // Tri-tap micro-delay network for a wide but strictly internal space
                _innerVoiceTap1Samples = 0.004f * _sampleRate; // 4ms
                _innerVoiceTap2Samples = 0.009f * _sampleRate; // 9ms
                _innerVoiceTap3Samples = 0.015f * _sampleRate; // 15ms

                // Depth LP (5500Hz -> 2500Hz): Muffles core formants for an internal "closed-box" feel.
                _innerVoiceLpCoeffMin = 1f - MathF.Exp(-2f * MathF.PI * Safe(5500f) / _sampleRate);
                _innerVoiceLpCoeffMax = 1f - MathF.Exp(-2f * MathF.PI * Safe(2500f) / _sampleRate);

                // Resonance (0.0 -> 0.08): Less boxiness, subtle presence bump near the cutoff.
                _innerVoiceResonanceMin = 0.0f;
                _innerVoiceResonanceMax = 0.08f;

                _innerVoiceLpState1 = 0f;
                _innerVoiceLpState2 = 0f;
                break;

            case SpatialEnvironment.Dungeon:
                ConfigureCombs(0.78f, 0.25f);
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(100f), 0.707f);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(7000f), 0.707f);
                _hasReverbEq = true;

                _dungeonRoomTone = BiQuadFilter.PeakingEQ(_sampleRate, Safe(200f), 0.8f, 4.0f);
                _dungeonHfDamp = BiQuadFilter.LowPassFilter(_sampleRate, Safe(4000f), 0.707f);
                _hasDungeonEq = true;

                _dungeonPreDelaySamples = 0.008f * _sampleRate;     // 8ms wall gap
                _dungeonFlutterDelaySamples = 0.007f * _sampleRate; // 7ms parallel flutter
                break;
        }
    }

    // =========================================================================
    // ACOUSTIC ALGORITHMS
    // =========================================================================

    /// <summary>
    /// Core Schroeder/Freeverb topology.
    /// Pre-reverb EQ cleans transients before the comb filter bank.
    /// 0.075f = 0.6f / 8 combs, pre-computed to replace division with multiplication.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float AlgorithmicReverb(float input)
    {
        float filtered = input;

        if (_hasReverbEq)
        {
            filtered = _reverbHpFilter!.Transform(filtered);
            filtered = _reverbLpFilter!.Transform(filtered);
        }

        float outCombs = 0f;
        for (int i = 0; i < _combs.Length; i++)
            outCombs += _combs[i].Process(filtered);

        float reverb = outCombs * 0.075f;

        for (int i = 0; i < _allPasses.Length; i++)
            reverb = _allPasses[i].Process(reverb);

        return reverb;
    }

    /// <summary>Discrete 250ms delay, simulating outdoor tree reflections.</summary>
    private float ForestEcho(float x)
    {
        float delayed = _forestDelay.Read(_forestDelaySamples);
        _forestDelay.Write(x + delayed * 0.4f);
        return delayed * 0.5f;
    }

    /// <summary>Dense reverb mixed with a 40ms slapback for pressure ring effect.</summary>
    private float Underwater(float x)
    {
        float reverb = AlgorithmicReverb(x);
        float delayed = _underwaterDelay.Read(_underwaterDelaySamples);
        _underwaterDelay.Write(x + delayed * 0.5f);
        return Dsp.Lerp(reverb, delayed, 0.4f);
    }

    /// <summary>Massive decay with 80Hz boost and dual pre-delay taps (15ms/38ms).</summary>
    private float CaveReverb(float x, bool hasCaveEq)
    {
        float boosted = hasCaveEq && _caveSubBoost != null ? _caveSubBoost.Transform(x) : x;

        _caveNearDelay.Write(boosted);
        _caveFarDelay.Write(boosted);

        float nearEcho = _caveNearDelay.Read(_caveNearDelaySamples);
        float farEcho = _caveFarDelay.Read(_caveFarDelaySamples);

        float reverb = AlgorithmicReverb(nearEcho);
        float wet = reverb + nearEcho * 0.35f + farEcho * 0.20f;

        if (hasCaveEq && _caveHfRolloff != null)
            wet = _caveHfRolloff.Transform(wet);

        return wet;
    }

    /// <summary>Theater acoustic with 180Hz warmth, 12ms lateral reflection, and 22ms ITDG.</summary>
    private float StageReverb(float x, bool hasStageEq)
    {
        float warmed = hasStageEq && _stageBassBoost != null ? _stageBassBoost.Transform(x) : x;

        _stageEarlyDelay.Write(warmed);
        float earlyReflection = _stageEarlyDelay.Read(_stageEarlyDelaySamples);

        _preDelay.Write(warmed);
        float preDelayed = _preDelay.Read(_stagePreDelaySamples);

        float reverb = AlgorithmicReverb(preDelayed);
        float wet = reverb + earlyReflection * 0.30f;

        if (hasStageEq && _stageHfAbsorption != null)
            wet = _stageHfAbsorption.Transform(wet);

        return wet;
    }

    /// <summary>
    /// Internal thoughts: Comb clusters and a 3-tap micro-delay network.
    /// Phase diffusion (all-passes) is intentionally removed to avoid a "small room" acoustic,
    /// keeping the sound dry, intimate, and cinematically locked inside the head.
    /// </summary>
    private float InnerVoice(float x, bool hasEq, float lpCoeff, float resonance, float outputGain)
    {
        float combOut = 0f;
        for (int i = 0; i < _innerCombs.Length; i++)
            combOut += _innerCombs[i].Process(x);

        // Diffusion (all-passes) removed for InnerVoice to prevent "small room" feel.
        float diffused = combOut * 0.333f;

        _preDelay.Write(x);
        float tap1 = _preDelay.Read(_innerVoiceTap1Samples);
        float tap2 = _preDelay.Read(_innerVoiceTap2Samples);
        float tap3 = _preDelay.Read(_innerVoiceTap3Samples);

        // 3-tap mix. Brain perceives this as width inside the head without physical distance.
        float wet = diffused + tap1 * 0.22f + tap2 * 0.15f + tap3 * 0.08f;

        if (hasEq)
        {
            if (_innerVoiceHp != null) wet = _innerVoiceHp.Transform(wet);
            if (_innerVoicePresenceCut != null) wet = _innerVoicePresenceCut.Transform(wet);
            if (_innerVoiceNasalBoost != null) wet = _innerVoiceNasalBoost.Transform(wet);
        }

        // Two-stage 1-pole LP with resonance feedback for a steeper, "closed-box" rolloff.
        _innerVoiceLpState1 += lpCoeff * ((wet - _innerVoiceLpState2 * resonance) - _innerVoiceLpState1);
        _innerVoiceLpState1 = Dsp.KillDenormal(_innerVoiceLpState1);

        _innerVoiceLpState2 += lpCoeff * (_innerVoiceLpState1 - _innerVoiceLpState2);
        _innerVoiceLpState2 = Dsp.KillDenormal(_innerVoiceLpState2);

        return Dsp.SoftClip(_innerVoiceLpState2 * outputGain);
    }

    /// <summary>Stone cellar with 200Hz modal resonance and 7ms parallel flutter echo.</summary>
    private float DungeonReverb(float x, bool hasDungeonEq)
    {
        float boosted = hasDungeonEq && _dungeonRoomTone != null ? _dungeonRoomTone.Transform(x) : x;

        _preDelay.Write(boosted);
        float preDelayed = _preDelay.Read(_dungeonPreDelaySamples);

        float reverb = AlgorithmicReverb(preDelayed);

        // Flutter echo (parallel walls). _dungeonFlutter is instantiated in constructor, no null checks needed.
        float flutterDelayed = _dungeonFlutter.Read(_dungeonFlutterDelaySamples);
        _dungeonFlutter.Write(boosted + flutterDelayed * 0.40f);

        float wet = reverb + flutterDelayed * 0.50f;

        if (hasDungeonEq && _dungeonHfDamp != null)
            wet = _dungeonHfDamp.Transform(wet);

        return wet;
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private void ConfigureCombs(float feedback, float damp)
    {
        float scaledDamp = Dsp.ScaleCoeff(damp, _sampleRate);
        foreach (var c in _combs)
        {
            c.Feedback = feedback;
            c.Damp = scaledDamp;
        }
    }

    private static int GetNextPrime(int start)
    {
        if (start < 2) start = 2;

        while (true)
        {
            bool isPrime = true;
            int limit = (int)Math.Sqrt(start);
            for (int i = 2; i <= limit; i++)
            {
                if (start % i == 0) { isPrime = false; break; }
            }
            if (isPrime) return start;
            start++;
        }
    }
}