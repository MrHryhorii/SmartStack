using ONNX_Runner.Models;
using System.Runtime.CompilerServices;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Server-side spatial acoustics engine.
/// Uses Freeverb/Schroeder algorithms for zero-allocation room simulation.
///
/// Design priority: PERCEPTUAL RECOGNIZABILITY. Each environment has one
/// dominant, instantly identifiable trait rather than a cluster of small
/// parameter tweaks. Every effect is structurally distinct.
///
/// - LivingRoom:   Short, soft, warm. Heavy HF damping. Recognizable at any mix.
/// - Stage:        Warm, dense, live. Bass shelf + audible lateral reflection
///                 (band-passed to sound like a side wall) + slow shimmer.
/// - ConcreteHall: Long, bright, crystalline. Near-infinite RT60, HF shelf boost,
///                 LFO-modulated flutter that avoids comb-filter resonance.
/// - Dungeon:      Tight, harsh, resonant. Sharp 190Hz modal honk + saturated
///                 flutter. Instantly recognizable on any voiced sound.
/// - Cave:         Dark, massive, rumbling. 100Hz body boost + cross-feedback
///                 echo pair + the longest, darkest tail of any environment.
/// - Forest:       Outdoor, sparse. Two discrete decaying taps, no reverb at all.
///                 Absence of a tail IS the recognizable trait.
/// - Muffled:      Occlusion. Simple 800Hz low-pass filter with no spatial reflections.
///                 Simulates hearing sound through walls, earplugs, or UI pause menus.
/// - Underwater:   Drowned. Steep 24dB/oct cascaded LP muffling + slapback pressure ring.
/// - InnerVoice:   Cinematic telepathy. Uses the Haas effect (micro-delay) and a dynamic
///                 low-pass filter to pull the voice inside the listener's head, supported 
///                 by a dark, wall-less shadow reverb.
/// </summary>
public class SpatialEffectsEngine
{
    private readonly int _sampleRate;
    private const float SpeedOfSound = 343f;

    // =========================================================================
    // FREEVERB PRIMITIVES
    // =========================================================================

    private readonly CombFilter[] _combs;
    private readonly AllPassFilter[] _allPasses;
    private readonly int[] _combDelaySamples;

    // =========================================================================
    // DELAY BUFFERS
    // =========================================================================

    private readonly DelayBuffer _stageLateralDelay;
    private readonly DelayBuffer _dungeonFlutter;
    private readonly DelayBuffer _caveNearDelay;
    private readonly DelayBuffer _caveFarDelay;
    private readonly DelayBuffer _hallFlutter;
    private readonly DelayBuffer _forestNearDelay;
    private readonly DelayBuffer _forestFarDelay;
    private readonly DelayBuffer _underwaterDelay;
    private readonly DelayBuffer _innerVoiceMicroDelay;

    // Shared pre-delay for Stage (ITDG) and Dungeon (wall gap) — mutually exclusive.
    private readonly DelayBuffer _preDelay;

    // =========================================================================
    // EQ FILTERS
    // =========================================================================

    private BiQuadFilter? _reverbHpFilter;
    private BiQuadFilter? _reverbLpFilter;

    private BiQuadFilter? _stageBassShelf;
    private BiQuadFilter? _stageLateralHp;
    private BiQuadFilter? _stageLateralLp;

    private BiQuadFilter? _dungeonResonator;
    private BiQuadFilter? _dungeonHfDamp;

    private BiQuadFilter? _caveSubBoost;
    private BiQuadFilter? _caveHfRolloff;

    private BiQuadFilter? _hallShimmer;

    private BiQuadFilter? _forestHfDamp;

    // Attenuation/Obstacle filters
    private BiQuadFilter? _environmentEq;
    private BiQuadFilter? _environmentEq2;

    // =========================================================================
    // STATE VARIABLES
    // =========================================================================

    // Stage: two detuned LFOs for "live room" amplitude shimmer.
    private float _modPhaseA;
    private float _modPhaseB;
    private float _modPhaseIncA;
    private float _modPhaseIncB;

    // ConcreteHall: LFO-modulated flutter delay.
    private float _hallFlutterPhase;
    private float _hallFlutterPhaseInc;

    // InnerVoice: Dynamic 1-pole LP state
    private float _innerVoiceLpState;

    // =========================================================================
    // PRE-COMPUTED DELAY SIZES
    // =========================================================================

    private float _stageLateralSamples;
    private float _stagePreDelaySamples;
    private float _dungeonPreDelaySamples;
    private float _dungeonFlutterSamples;
    private float _caveNearSamples;
    private float _caveFarSamples;
    private float _caveCrossFeedback;
    private float _hallFlutterSamples;
    private float _forestNearSamples;
    private float _forestFarSamples;
    private float _underwaterDelaySamples;
    private float _innerVoiceMicroDelaySamples;

    // =========================================================================
    // BRANCH-HOISTING FLAGS
    // =========================================================================

    private bool _hasReverbEq;
    private bool _hasStageEq;
    private bool _hasDungeonEq;
    private bool _hasCaveEq;
    private bool _hasHallEq;
    private bool _hasForestEq;
    private bool _hasModulation;

    private SpatialEnvironment _current = SpatialEnvironment.None;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public SpatialEffectsEngine(int sampleRate)
    {
        _sampleRate = sampleRate;

        float scale = sampleRate / 44100f;
        int ScaleToPrime(int baseSize) => GetNextPrime((int)(baseSize * scale));

        int[] combSizes =
        [
            ScaleToPrime(1116), ScaleToPrime(1188),
            ScaleToPrime(1277), ScaleToPrime(1356),
            ScaleToPrime(1422), ScaleToPrime(1491),
            ScaleToPrime(1557), ScaleToPrime(1617)
        ];

        _combDelaySamples = combSizes;
        _combs = new CombFilter[combSizes.Length];
        for (int i = 0; i < combSizes.Length; i++)
            _combs[i] = new CombFilter(combSizes[i]);

        _allPasses =
        [
            new(ScaleToPrime(225)), new(ScaleToPrime(341)),
            new(ScaleToPrime(441)), new(ScaleToPrime(556))
        ];

        _stageLateralDelay = new DelayBuffer(SamplesFor(42f));
        _dungeonFlutter = new DelayBuffer(SamplesFor(10f));
        _caveNearDelay = new DelayBuffer(SamplesFor(85f));
        _caveFarDelay = new DelayBuffer(SamplesFor(85f));
        _hallFlutter = new DelayBuffer(SamplesFor(15f));
        _forestNearDelay = new DelayBuffer(SamplesFor(280f));
        _forestFarDelay = new DelayBuffer(SamplesFor(550f));
        _underwaterDelay = new DelayBuffer(SamplesFor(170f));
        _preDelay = new DelayBuffer(SamplesFor(42f));
        _innerVoiceMicroDelay = new DelayBuffer(SamplesFor(15f));
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Rt60ToFeedback(float rt60Seconds, float delaySeconds)
        => MathF.Pow(10f, -3f * delaySeconds / rt60Seconds);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float DistanceToRoundTripSamples(float distanceMeters)
        => (2f * distanceMeters / SpeedOfSound) * _sampleRate;

    private int SamplesFor(float targetMs)
    {
        int required = (int)MathF.Ceiling(targetMs * 0.001f * _sampleRate * 1.25f);
        return NextPowerOf2(Math.Max(required, 64));
    }

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    public void Reset()
    {
        foreach (var c in _combs) c.Clear();
        foreach (var a in _allPasses) a.Clear();

        _stageLateralDelay.Clear();
        _dungeonFlutter.Clear();
        _caveNearDelay.Clear();
        _caveFarDelay.Clear();
        _hallFlutter.Clear();
        _forestNearDelay.Clear();
        _forestFarDelay.Clear();
        _underwaterDelay.Clear();
        _preDelay.Clear();
        _innerVoiceMicroDelay.Clear();

        _modPhaseA = 0f;
        _modPhaseB = 0f;
        _hallFlutterPhase = 0f;
        _innerVoiceLpState = 0f;
    }

    /// <summary>
    /// Quadratic mix (mix²) for additive/spatial environments gives finer control at low mix levels.
    /// Inverse-square mix (1-(1-mix)²) for attenuation environments (Muffled, Underwater, InnerVoice)
    /// ramps in fast, providing fine control near mix=1 where the muffling/dissolution lives.
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

        float curvedMix = mix * mix;
        float inverseSquareMix = 1f - (1f - mix) * (1f - mix);

        switch (env)
        {
            case SpatialEnvironment.LivingRoom:
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, AlgorithmicReverb(dry), curvedMix));
                }
                break;

            case SpatialEnvironment.Stage:
                bool hasStageEq = _hasStageEq;
                bool hasMod = _hasModulation;
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, StageReverb(dry, hasStageEq, hasMod), curvedMix));
                }
                break;

            case SpatialEnvironment.ConcreteHall:
                bool hasHallEq = _hasHallEq;
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, ConcreteHallReverb(dry, hasHallEq), curvedMix));
                }
                break;

            case SpatialEnvironment.Dungeon:
                bool hasDungeonEq = _hasDungeonEq;
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, DungeonReverb(dry, hasDungeonEq), curvedMix));
                }
                break;

            case SpatialEnvironment.Cave:
                bool hasCaveEq = _hasCaveEq;
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, CaveReverb(dry, hasCaveEq), curvedMix));
                }
                break;

            case SpatialEnvironment.Forest:
                bool hasForestEq = _hasForestEq;
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, ForestEcho(dry, hasForestEq), curvedMix));
                }
                break;

            case SpatialEnvironment.Muffled:
                bool hasMuffledEq = _environmentEq != null;

                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = dry;

                    if (hasMuffledEq)
                    {
                        // Double pass through the "concrete wall" (-24 dB/oct)
                        // Destroys articulation but keeps the fundamental bass energy intact.
                        wet = _environmentEq!.Transform(wet);
                        wet = _environmentEq2!.Transform(wet);
                    }

                    // Linear crossfade (Lerp) prevents unnatural volume bumps 
                    // when blending heavily correlated signals.
                    float mixed = Dsp.Lerp(dry, wet, inverseSquareMix);

                    buffer[i] = Dsp.SoftClip(mixed);
                }
                break;

            case SpatialEnvironment.Underwater:
                bool hasUwEq = _environmentEq != null;
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    float wet = Underwater(dry);

                    if (hasUwEq)
                    {
                        // Cascade two filters for a steeper 24dB/oct slope
                        wet = _environmentEq!.Transform(wet);
                        wet = _environmentEq2!.Transform(wet);
                    }
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, inverseSquareMix));
                }
                break;

            case SpatialEnvironment.InnerVoice:
                // Dynamic cutoff from 8000Hz (transparent) to 1800Hz (disconnected from reality).
                // A higher mix increasingly isolates the voice from the physical world.
                float currentFreq = Dsp.Lerp(8000f, 1800f, inverseSquareMix);
                float safeFreq = Math.Min(currentFreq, _sampleRate * 0.45f);
                float lpCoeff = 1f - MathF.Exp(-2f * MathF.PI * safeFreq / _sampleRate);

                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);

                    // Haas Effect: Blend dry signal with an 8ms micro-delay to collapse external 
                    // spatialization and pull the voice directly "inside" the listener's head.
                    _innerVoiceMicroDelay.Write(dry);
                    float microDelayed = _innerVoiceMicroDelay.Read(_innerVoiceMicroDelaySamples);
                    float presenceVoice = dry + microDelayed * 0.15f;

                    // Apply gentle 1-pole LP to darken the presence voice without ruining TTS articulation.
                    _innerVoiceLpState += lpCoeff * (presenceVoice - _innerVoiceLpState);
                    _innerVoiceLpState = Dsp.KillDenormal(_innerVoiceLpState);

                    // Reverb is generated from the close-up voice, giving it a massive spatial shadow.
                    float wet = AlgorithmicReverb(presenceVoice);

                    float mixed = _innerVoiceLpState + wet * inverseSquareMix * 0.45f;

                    // Use Equal-Gain (Lerp) crossfade instead of Equal-Power. 
                    // Since 'mixed' contains the correlated 'dry' signal, Equal-Power would cause 
                    // an unnatural +3dB volume bump at the center of the mix.
                    buffer[i] = Dsp.SoftClip(Dsp.Lerp(dry, mixed, inverseSquareMix));
                }
                break;
        }
    }

    // =========================================================================
    // SETUP
    // =========================================================================

    private void Setup(SpatialEnvironment env)
    {
        // Nullify all specific EQ filters to prevent state leakage between environments.
        _reverbHpFilter = null;
        _reverbLpFilter = null;
        _stageBassShelf = null;
        _stageLateralHp = null;
        _stageLateralLp = null;
        _dungeonResonator = null;
        _dungeonHfDamp = null;
        _caveSubBoost = null;
        _caveHfRolloff = null;
        _hallShimmer = null;
        _forestHfDamp = null;
        _environmentEq = null;
        _environmentEq2 = null;

        _hasReverbEq = false;
        _hasStageEq = false;
        _hasDungeonEq = false;
        _hasCaveEq = false;
        _hasHallEq = false;
        _hasForestEq = false;
        _hasModulation = false;

        float nyq = _sampleRate * 0.45f;
        float Safe(float f) => Math.Min(f, nyq);

        switch (env)
        {
            // Short, soft, warm room. RT60=0.35s, heavy HF damping (damp=0.78).
            // LP at 4200Hz darkens the tail — the fastest and darkest decay.
            // No early reflections: small rooms fuse them into the reverb onset immediately.
            case SpatialEnvironment.LivingRoom:
                ConfigureCombsByRt60(0.35f, 0.78f);
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(200f), 0.707f);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(4200f), 0.707f);
                _hasReverbEq = true;
                break;

            // Concert hall / theater. Defining traits:
            // 1. Bass shelf +4.5dB at 200Hz (low-end warmth).
            // 2. Lateral reflection at ~12ms, band-passed (350Hz–5kHz) to sound like a wall bounce.
            // 3. Dual-LFO amplitude shimmer (±2%) breaks up static Freeverb decay.
            case SpatialEnvironment.Stage:
                ConfigureCombsByRt60(1.7f, 0.28f);
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(90f), 0.707f);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(7500f), 0.707f);
                _hasReverbEq = true;

                _stageBassShelf = BiQuadFilter.LowShelf(_sampleRate, Safe(200f), 0.707f, 4.5f);

                // Band-pass the lateral reflection: HP strips bass (walls don't reflect sub-bass), 
                // LP strips air (distance absorbs highs).
                _stageLateralHp = BiQuadFilter.HighPassFilter(_sampleRate, Safe(350f), 0.9f);
                _stageLateralLp = BiQuadFilter.LowPassFilter(_sampleRate, Safe(5000f), 0.9f);
                _hasStageEq = true;

                _stageLateralSamples = DistanceToRoundTripSamples(2.0f); // ~12ms
                _stagePreDelaySamples = 0.020f * _sampleRate;             // 20ms ITDG

                _modPhaseIncA = 2f * MathF.PI * 0.13f / _sampleRate;
                _modPhaseIncB = 2f * MathF.PI * 0.19f / _sampleRate;
                _hasModulation = true;
                break;

            // Bright, crystalline, long. RT60=2.2s with near-zero HF damping.
            // HighShelf +2.5dB at 7kHz keeps the decay sparkling. 
            // LFO-modulated flutter (±1.0ms at 2.8Hz) creates a gentle chorus/chirp effect.
            case SpatialEnvironment.ConcreteHall:
                ConfigureCombsByRt60(2.2f, 0.05f);
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(70f), 0.707f);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(11000f), 0.707f);
                _hasReverbEq = true;

                _hallShimmer = BiQuadFilter.HighShelf(_sampleRate, Safe(7000f), 0.707f, 2.5f);
                _hasHallEq = true;

                _hallFlutterSamples = 0.004f * _sampleRate;
                _hallFlutterPhaseInc = 2f * MathF.PI * 2.8f / _sampleRate;
                _hallFlutterPhase = 0f;
                break;

            // Tight confinement chamber. Defining traits:
            // 1. Sharp modal resonance: 190Hz / Q=5.5 / +9dB.
            // 2. Saturated flutter: AsymmetricSaturation on the 7ms feedback loop gives a gritty character.
            case SpatialEnvironment.Dungeon:
                ConfigureCombs(Rt60ToFeedback(0.40f, 0.007f), Dsp.ScaleCoeff(0.40f, _sampleRate));
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(140f), 0.707f);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(5000f), 0.707f);
                _hasReverbEq = true;

                _dungeonResonator = BiQuadFilter.PeakingEQ(_sampleRate, Safe(190f), 5.5f, 9.0f);
                _dungeonHfDamp = BiQuadFilter.LowPassFilter(_sampleRate, Safe(3000f), 0.707f);
                _hasDungeonEq = true;

                _dungeonPreDelaySamples = DistanceToRoundTripSamples(1.0f); // ~6ms
                _dungeonFlutterSamples = 0.007f * _sampleRate;              // 7ms
                break;

            // Dark massive cavern. Defining traits:
            // 1. 100Hz body boost (+5dB, Q=0.6) sits in the fundamental frequency of TTS voices.
            // 2. Cross-feedback echo pair (15ms/38ms, feedback=0.30) scatters energy irregularly.
            // 3. Longest RT60 (3.5s) + steepest HF rolloff (2600Hz) ensures the tail is massive and dark.
            case SpatialEnvironment.Cave:
                ConfigureCombsByRt60(3.5f, 0.03f);
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(40f), 0.707f);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(6000f), 0.707f);
                _hasReverbEq = true;

                _caveSubBoost = BiQuadFilter.PeakingEQ(_sampleRate, Safe(100f), 0.6f, 5.0f);
                _caveHfRolloff = BiQuadFilter.LowPassFilter(_sampleRate, Safe(2600f), 0.707f);
                _hasCaveEq = true;

                _caveNearSamples = DistanceToRoundTripSamples(2.5f); // ~15ms
                _caveFarSamples = DistanceToRoundTripSamples(6.5f);  // ~38ms
                _caveCrossFeedback = 0.30f;
                break;

            // Outdoor. Two discrete taps at ~250ms and ~500ms with progressive HF loss.
            // No reverb tail is generated.
            case SpatialEnvironment.Forest:
                _forestNearSamples = DistanceToRoundTripSamples(43f); // ~250ms
                _forestFarSamples = DistanceToRoundTripSamples(86f);  // ~500ms

                _forestHfDamp = BiQuadFilter.LowPassFilter(_sampleRate, Safe(2200f), 0.707f);
                _hasForestEq = true;
                break;

            // Occlusion / Muffled effect (Hearing through thick walls or heavy earplugs).
            // Uses a cascaded 24dB/oct low-pass at 450Hz to aggressively destroy 
            // speech intelligibility, but keeps Q=0.707 to avoid underwater resonance.
            case SpatialEnvironment.Muffled:
                _environmentEq = BiQuadFilter.LowPassFilter(_sampleRate, Safe(450f), 0.707f);
                _environmentEq2 = BiQuadFilter.LowPassFilter(_sampleRate, Safe(450f), 0.707f);
                break;

            // Dense medium. Uses two cascaded LPs at 420Hz for a sharp 24dB/oct cutoff, 
            // plus a 40ms slapback delay for pressure emulation.
            case SpatialEnvironment.Underwater:
                ConfigureCombsByRt60(1.2f, 0.92f);
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(120f), 0.707f);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(1800f), 0.707f);
                _hasReverbEq = true;

                // Cascade achieves steep physical muffling; Q=0.9 on the second stage adds resonant pressure.
                _environmentEq = BiQuadFilter.LowPassFilter(_sampleRate, Safe(420f), 0.707f);
                _environmentEq2 = BiQuadFilter.LowPassFilter(_sampleRate, Safe(420f), 0.9f);
                _underwaterDelaySamples = DistanceToRoundTripSamples(7.0f); // ~40ms
                break;

            // Cinematic Telepathy. 
            // Massive (3.5s RT60) but extremely dark reverb to imply boundless mental space 
            // without reflecting real physical walls (150Hz HP / 1800Hz LP).
            case SpatialEnvironment.InnerVoice:
                ConfigureCombsByRt60(3.5f, 0.85f);
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(150f), 0.707f);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(1800f), 0.707f);
                _hasReverbEq = true;

                // 8ms is the optimal Haas effect delay to bring the source "inside" the head.
                _innerVoiceMicroDelaySamples = 0.008f * _sampleRate;
                break;
        }
    }

    // =========================================================================
    // ACOUSTIC ALGORITHMS
    // =========================================================================

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

        float reverb = outCombs * 0.075f; // 0.6 / 8 combs
        for (int i = 0; i < _allPasses.Length; i++)
            reverb = _allPasses[i].Process(reverb);

        return reverb;
    }

    // Stage variant: LFO amplitude wobble on the reverb tail.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float AlgorithmicReverbModulated(float input)
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

        _modPhaseA += _modPhaseIncA;
        if (_modPhaseA >= 2f * MathF.PI) _modPhaseA -= 2f * MathF.PI;
        _modPhaseB += _modPhaseIncB;
        if (_modPhaseB >= 2f * MathF.PI) _modPhaseB -= 2f * MathF.PI;

        float wobble = (Dsp.Sine(_modPhaseA) + Dsp.Sine(_modPhaseB)) * 0.5f;
        float modulated = reverb * (1f + wobble * 0.02f);

        for (int i = 0; i < _allPasses.Length; i++)
            modulated = _allPasses[i].Process(modulated);

        return modulated;
    }

    // Stage: bass shelf → reverb with shimmer → mix lateral reflection.
    private float StageReverb(float x, bool hasStageEq, bool hasModulation)
    {
        float warmed = hasStageEq && _stageBassShelf != null ? _stageBassShelf.Transform(x) : x;

        _stageLateralDelay.Write(warmed);
        float lateral = _stageLateralDelay.Read(_stageLateralSamples);

        if (hasStageEq && _stageLateralHp != null) lateral = _stageLateralHp.Transform(lateral);
        if (hasStageEq && _stageLateralLp != null) lateral = _stageLateralLp.Transform(lateral);

        _preDelay.Write(warmed);
        float preDelayed = _preDelay.Read(_stagePreDelaySamples);

        float reverb = hasModulation
            ? AlgorithmicReverbModulated(preDelayed)
            : AlgorithmicReverb(preDelayed);

        // Lateral mixed at 0.45: Must be audible above the reverb tail as the main "hall" cue.
        return reverb + lateral * 0.45f;
    }

    // ConcreteHall: long bright reverb + LFO-modulated flutter + HF shimmer.
    private float ConcreteHallReverb(float x, bool hasHallEq)
    {
        float reverb = AlgorithmicReverb(x);

        _hallFlutterPhase += _hallFlutterPhaseInc;
        if (_hallFlutterPhase >= 2f * MathF.PI) _hallFlutterPhase -= 2f * MathF.PI;

        float modDepth = 0.0010f * _sampleRate; // ±1.0ms
        float modulatedDelay = _hallFlutterSamples + Dsp.Sine(_hallFlutterPhase) * modDepth;

        float flutter = _hallFlutter.Read(modulatedDelay);
        _hallFlutter.Write(x + flutter * 0.15f);

        float wet = reverb + flutter * 0.20f;

        if (hasHallEq && _hallShimmer != null)
            wet = _hallShimmer.Transform(wet);

        return wet;
    }

    // Dungeon: modal resonance → pre-delay → dense reverb + saturated flutter.
    private float DungeonReverb(float x, bool hasDungeonEq)
    {
        float resonated = hasDungeonEq && _dungeonResonator != null
            ? _dungeonResonator.Transform(x)
            : x;

        _preDelay.Write(resonated);
        float preDelayed = _preDelay.Read(_dungeonPreDelaySamples);

        float reverb = AlgorithmicReverb(preDelayed);

        float flutterDelayed = _dungeonFlutter.Read(_dungeonFlutterSamples);
        float flutterFeedback = Dsp.AsymmetricSaturation(flutterDelayed * 0.6f);

        // SoftClip before Write: resonator adds +9dB so sum can exceed 1.0.
        _dungeonFlutter.Write(Dsp.SoftClip(resonated + flutterFeedback));

        float wet = reverb + flutterDelayed * 0.50f;

        if (hasDungeonEq && _dungeonHfDamp != null)
            wet = _dungeonHfDamp.Transform(wet);

        return wet;
    }

    // Cave: sub-boost → cross-feedback echoes → long dark reverb.
    private float CaveReverb(float x, bool hasCaveEq)
    {
        float boosted = hasCaveEq && _caveSubBoost != null ? _caveSubBoost.Transform(x) : x;

        float prevNear = _caveNearDelay.Read(_caveNearSamples);
        float prevFar = _caveFarDelay.Read(_caveFarSamples);

        // SoftClip before both delay writes: stabilizes the cross-feedback loop.
        _caveNearDelay.Write(Dsp.SoftClip(boosted + prevFar * _caveCrossFeedback));
        _caveFarDelay.Write(Dsp.SoftClip(boosted + prevNear * _caveCrossFeedback));

        float reverb = AlgorithmicReverb(prevNear);
        float wet = reverb + prevNear * 0.55f + prevFar * 0.35f;

        if (hasCaveEq && _caveHfRolloff != null)
            wet = _caveHfRolloff.Transform(wet);

        return wet;
    }

    // Forest: two discrete taps, no reverb. Far tap darkened by foliage LP.
    private float ForestEcho(float x, bool hasForestEq)
    {
        float nearEcho = _forestNearDelay.Read(_forestNearSamples);
        float farEcho = _forestFarDelay.Read(_forestFarSamples);

        _forestNearDelay.Write(x + nearEcho * 0.35f);
        _forestFarDelay.Write(x + farEcho * 0.20f);

        if (hasForestEq && _forestHfDamp != null)
            farEcho = _forestHfDamp.Transform(farEcho);

        return nearEcho * 0.55f + farEcho * 0.30f;
    }

    // Underwater: damped reverb + 40ms slapback. Post-LP applied in ApplyEnvironment.
    private float Underwater(float x)
    {
        float reverb = AlgorithmicReverb(x);
        float delayed = _underwaterDelay.Read(_underwaterDelaySamples);
        _underwaterDelay.Write(x + delayed * 0.5f);
        return Dsp.Lerp(reverb, delayed, 0.4f);
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private void ConfigureCombs(float feedback, float scaledDamp)
    {
        foreach (var c in _combs)
        {
            c.Feedback = feedback;
            c.Damp = scaledDamp;
        }
    }

    private void ConfigureCombsByRt60(float rt60Seconds, float damp)
    {
        float scaledDamp = Dsp.ScaleCoeff(damp, _sampleRate);
        for (int i = 0; i < _combs.Length; i++)
        {
            float delaySeconds = _combDelaySamples[i] / (float)_sampleRate;
            _combs[i].Feedback = Rt60ToFeedback(rt60Seconds, delaySeconds);
            _combs[i].Damp = scaledDamp;
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
                if (start % i == 0) { isPrime = false; break; }
            if (isPrime) return start;
            start++;
        }
    }

    private static int NextPowerOf2(int minSize)
    {
        int size = 1;
        while (size < minSize) size <<= 1;
        return size;
    }
}