using ONNX_Runner.Models;
using ONNX_Runner.Services.Spatial;
using System.Runtime.CompilerServices;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Server-side spatial acoustics engine.
/// Room, LivingRoom, Stage, ConcreteHall, Dungeon and Cave use an OpenAL-derived mono reverb; remaining profiles retain their current algorithms.
///
/// Design priority: PERCEPTUAL RECOGNIZABILITY. Each environment has one
/// dominant, instantly identifiable trait rather than a cluster of small
/// parameter tweaks. Every effect is structurally distinct.
///
/// - Room:         Short, clear reflections with moderate HF absorption.
/// - LivingRoom:   Short, soft, warm. Heavy HF damping. Recognizable at any mix.
/// - Stage:        Wooden performance hall with warm, delayed reflections.
/// - ConcreteHall: Large concert hall with a long, dense reflected tail.
/// - Dungeon:      Compact stone chamber with dense, short reflections.
/// - Cave:         Reflective rocky cavern with a longer decay.
/// - Forest:       Outdoor, sparse. Two discrete decaying taps, no reverb at all.
///                 Absence of a tail IS the recognizable trait.
/// - Muffled:      Occlusion. Gradual low-pass filtering down to 450Hz with no reflections.
///                 Simulates hearing sound through walls, earplugs, or UI pause menus.
/// - Underwater:   Drowned. Steep 24dB/oct cascaded LP muffling + slapback pressure ring.
/// - InnerVoice:   Cinematic telepathy. Uses the Haas effect (micro-delay) and a dynamic
///                 low-pass filter to pull the voice inside the listener's head, supported 
///                 by a dark, wall-less shadow reverb.
/// </summary>
public class SpatialEffectsEngine
{
    private readonly int _sampleRate;
    private readonly int _reverbSilenceWindowSamples;
    private const float SpeedOfSound = 343f;

    // Published EFX profiles; output balance and intensity stay caller-owned.
    private static readonly SpatialReverbParameters RoomParameters = new(
        .4287f, 1, .3162f, .5929f, 1, .4f, .83f, 1, .1503f, .002f,
        1.0629f, .003f, .25f, 0, .9943f, 5000, 250, true);
    private static readonly SpatialReverbParameters LivingRoomParameters = new(
        .9766f, 1, .3162f, .001f, 1, .5f, .1f, 1, .2051f, .003f,
        .2805f, .004f, .25f, 0, .9943f, 5000, 250, true);
    private static readonly SpatialReverbParameters StageParameters = new(
        1, 1, .3162f, .0794f, .2818f, 3.45f, .3f, .82f, .8913f, .088f,
        .7943f, .063f, .25f, 0, .9943f, 4705, 99.6f, true);
    private static readonly SpatialReverbParameters ConcreteHallParameters = new(
        1, 1, .3162f, .5623f, 1, 3.92f, .7f, 1, .2427f, .020f,
        .9977f, .029f, .25f, 0, .9943f, 5000, 250, true);
    private static readonly SpatialReverbParameters DungeonParameters = new(
        1, .89f, .3162f, .3981f, .1f, 1.22f, .83f, .31f, .8913f, .022f,
        1.9953f, .011f, .25f, 0, .9943f, 5168.6001f, 139.5f, true);
    private static readonly SpatialReverbParameters CaveParameters = new(
        1, 1, .3162f, 1, 1, 2.91f, 1.3f, 1, .5f, .015f,
        .7063f, .022f, .25f, 0, .9943f, 5000, 250, false);
    // Preserve the accepted room balance; other published profiles use doubled return.
    private const float RoomReturnGain = 64f;
    private const float DefaultReverbReturnGain = 2f;
    // Covers two passes of this profile's longest feedback path (approximately 209 ms).
    private const float RoomQuietWindowSeconds = .45f;
    // Cover the longer input and feedback paths of the other published profiles.
    private const float ReverbQuietWindowSeconds = .65f;
    private SpatialReverbKernel? _profileReverb;
    private float _profileReturnGain;
    private int _profileQuietWindowSamples;
    private int _maximumTailSamples;

    // =========================================================================
    // FREEVERB PRIMITIVES
    // =========================================================================

    private readonly CombFilter[] _combs;
    private readonly AllPassFilter[] _allPasses;
    private readonly int[] _combDelaySamples;

    // =========================================================================
    // DELAY BUFFERS
    // =========================================================================

    private readonly DelayBuffer _forestNearDelay;
    private readonly DelayBuffer _forestFarDelay;
    private readonly DelayBuffer _underwaterDelay;
    private readonly DelayBuffer _innerVoiceMicroDelay;

    // =========================================================================
    // EQ FILTERS
    // =========================================================================

    private BiQuadFilter? _reverbHpFilter;
    private BiQuadFilter? _reverbLpFilter;

    private BiQuadFilter? _forestHfDamp;

    // Attenuation/Obstacle filters
    private BiQuadFilter? _environmentEq;
    private BiQuadFilter? _environmentEq2;

    // =========================================================================
    // STATE VARIABLES
    // =========================================================================

    // InnerVoice: Dynamic 1-pole LP state
    private float _innerVoiceLpState;
    private float _innerVoicePreparedAmount = float.NaN;
    private float _innerVoiceStrength;
    private float _innerVoiceInverseStrength;
    private float _innerVoicePresenceGain;
    private float _innerVoiceReverbGain;
    private float _innerVoiceLpCoeff;
    private bool _innerVoiceFullStrength;

    private float _muffledPreparedAmount = float.NaN;
    private float _muffledStrength;
    private float _muffledInverseStrength;
    private bool _muffledFullStrength;

    // =========================================================================
    // PRE-COMPUTED DELAY SIZES
    // =========================================================================

    private float _forestNearSamples;
    private float _forestFarSamples;
    private float _underwaterDelaySamples;
    private float _innerVoiceMicroDelaySamples;

    // =========================================================================
    // BRANCH-HOISTING FLAGS
    // =========================================================================

    private bool _hasReverbEq;
    private bool _hasForestEq;

    private SpatialEnvironment _current = SpatialEnvironment.None;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    /// <summary>Allocates sample-rate-scaled reflection, comb, and all-pass state for one synthesis request.</summary>
    public SpatialEffectsEngine(int sampleRate)
    {
        _sampleRate = sampleRate;
        _maximumTailSamples = checked(sampleRate * 4);

        float scale = sampleRate / 44100f;
        // Scales a reference delay length and chooses a prime length to reduce coincident reflections.
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

        int allPassA = ScaleToPrime(225), allPassB = ScaleToPrime(341);
        int allPassC = ScaleToPrime(441), allPassD = ScaleToPrime(556);
        _allPasses = [new(allPassA), new(allPassB), new(allPassC), new(allPassD)];
        _reverbSilenceWindowSamples = combSizes[^1] + allPassA + allPassB + allPassC + allPassD;

        _forestNearDelay = new DelayBuffer(SamplesFor(280f));
        _forestFarDelay = new DelayBuffer(SamplesFor(550f));
        _underwaterDelay = new DelayBuffer(SamplesFor(170f));
        _innerVoiceMicroDelay = new DelayBuffer(SamplesFor(15f));
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>Converts an RT60 decay target and feedback delay into the per-round-trip gain.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Rt60ToFeedback(float rt60Seconds, float delaySeconds)
    {
        return MathF.Pow(10f, -3f * delaySeconds / rt60Seconds);
    }

    /// <summary>Converts an acoustic round-trip distance in meters to a delay in samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float DistanceToRoundTripSamples(float distanceMeters)
    {
        return 2f * distanceMeters / SpeedOfSound * _sampleRate;
    }

    /// <summary>Calculates delay capacity for a target duration, including headroom and power-of-two rounding.</summary>
    private int SamplesFor(float targetMs)
    {
        int required = (int)MathF.Ceiling(targetMs * 0.001f * _sampleRate * 1.25f);
        return NextPowerOf2(Math.Max(required, 64));
    }

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>Sample-rate-scaled safety budget for the active profile's complete reverb tail.</summary>
    internal int MaximumTailSamples => _maximumTailSamples;

    /// <summary>Quiet observation span covering the active reflection path before a tail can end.</summary>
    internal int TailSilenceWindowSamples
    {
        get
        {
            int probeSamples = Math.Max(1, _sampleRate / 50);
            if (_current is SpatialEnvironment.None or SpatialEnvironment.Muffled) return probeSamples;
            if (_profileReverb is not null) return _profileQuietWindowSamples;
            float pathSamples = _current switch
            {
                SpatialEnvironment.Forest => Math.Max(_forestNearSamples, _forestFarSamples),
                SpatialEnvironment.Underwater => Math.Max(_underwaterDelaySamples, _reverbSilenceWindowSamples),
                SpatialEnvironment.InnerVoice => _reverbSilenceWindowSamples + _innerVoiceMicroDelaySamples,
                _ => _reverbSilenceWindowSamples
            };
            // Dense feedback can rise again after a quiet pass; observe two complete path spans.
            // Forest needs one complete interval between its discrete echo returns.
            int passes = _current == SpatialEnvironment.Forest ? 1 : 2;
            return Math.Max(probeSamples, (int)MathF.Ceiling(pathSamples * passes) + 2);
        }
    }

    /// <summary>Clears delay, filter, modulation, and tail state before reusing the environment processor.</summary>
    public void Reset()
    {
        _profileReverb?.Reset();
        foreach (var c in _combs) c.Clear();
        foreach (var a in _allPasses) a.Clear();

        _forestNearDelay.Clear();
        _forestFarDelay.Clear();
        _underwaterDelay.Clear();
        _innerVoiceMicroDelay.Clear();

        _innerVoiceLpState = 0f;
    }

    /// <summary>
    /// Published reverb profiles retain direct sound and use inverse-square intensity for their reflected return.
    /// Remaining additive/spatial environments retain their quadratic mix (mix²).
    /// Inverse-square intensity (1-(1-mix)²) moves the Muffled cutoff and saturation depth.
    /// InnerVoice uses that intensity for internal coloration; Underwater uses it for blending.
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

        if (_profileReverb is { } profileReverb)
        {
            ApplyReverb(buffer, mix, profileReverb, _profileReturnGain);
            return;
        }

        float curvedMix = mix * mix;
        float inverseSquareMix = 1f - (1f - mix) * (1f - mix);

        // Equal-power gains are constant for the whole buffer. Compute only the pair
        // used by the selected environment to avoid MathF.Sqrt in the sample loop.
        float curvedDryGain = 1f;
        float curvedWetGain = 0f;
        float inverseDryGain = 1f;
        float inverseWetGain = 0f;

        if (env == SpatialEnvironment.Forest)
        {
            Dsp.EqualPowerGains(curvedMix, out curvedDryGain, out curvedWetGain);
        }
        else if (env == SpatialEnvironment.Underwater)
        {
            Dsp.EqualPowerGains(inverseSquareMix, out inverseDryGain, out inverseWetGain);
        }

        switch (env)
        {
            case SpatialEnvironment.Forest:
                bool hasForestEq = _hasForestEq;
                for (int i = 0; i < buffer.Length; i++)
                {
                    float dry = Dsp.KillDenormal(buffer[i]);
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, ForestEcho(dry, hasForestEq), curvedDryGain, curvedWetGain));
                }
                break;

            case SpatialEnvironment.Muffled:
                PrepareMuffled(mix);
                for (int i = 0; i < buffer.Length; i++)
                {
                    float input = Dsp.KillDenormal(buffer[i]);
                    float filtered = _environmentEq2!.Transform(_environmentEq!.Transform(input));
                    // Preserve the accepted full-output floating-point expression.
                    if (_muffledFullStrength) filtered = Dsp.Lerp(input, filtered, 1f);
                    buffer[i] = Dsp.ModulationSaturate(filtered, _muffledStrength, _muffledInverseStrength);
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
                    buffer[i] = Dsp.SoftClip(Dsp.EqualPowerCrossfade(dry, wet, inverseDryGain, inverseWetGain));
                }
                break;

            case SpatialEnvironment.InnerVoice:
                PrepareInnerVoice(mix);
                for (int i = 0; i < buffer.Length; i++)
                {
                    float input = Dsp.KillDenormal(buffer[i]);
                    _innerVoiceMicroDelay.Write(input);
                    float delayed = _innerVoiceMicroDelay.Read(_innerVoiceMicroDelaySamples);
                    float presence = input + delayed * _innerVoicePresenceGain;
                    _innerVoiceLpState += _innerVoiceLpCoeff * (presence - _innerVoiceLpState);
                    _innerVoiceLpState = Dsp.KillDenormal(_innerVoiceLpState);
                    float reverb = AlgorithmicReverb(presence);
                    float mixed = _innerVoiceLpState + reverb * _innerVoiceReverbGain;
                    // Retain the accepted full expression's final floating-point rounding.
                    if (_innerVoiceFullStrength) mixed = Dsp.Lerp(input, mixed, 1f);
                    buffer[i] = Dsp.ModulationSaturate(mixed, _innerVoiceStrength, _innerVoiceInverseStrength);
                }
                break;
        }
    }

    /// <summary>Preserves direct speech and adds one fixed profile response; final gain and limiting remain downstream.</summary>
    private static void ApplyReverb(Span<float> buffer, float amount, SpatialReverbKernel reverb, float fullReturnGain)
    {
        float position = Math.Clamp(amount, 0, 1);
        float strength = position * (2 - position);
        float returnGain = fullReturnGain * strength;
        for (int i = 0; i < buffer.Length; i++)
        {
            float direct = buffer[i];
            buffer[i] = direct + reverb.Process(direct) * returnGain;
        }
    }

    // =========================================================================
    // SETUP
    // =========================================================================

    /// <summary>Prepares gradual direct coloration and one internally scaled reverb contribution.</summary>
    private void PrepareInnerVoice(float amount)
    {
        if (_innerVoicePreparedAmount == amount) return;
        _innerVoicePreparedAmount = amount;
        float depth = Math.Clamp(amount, 0f, 1f);
        float strength = depth * (2f - depth);
        _innerVoiceStrength = strength;
        _innerVoiceInverseStrength = 1f / strength;
        _innerVoiceFullStrength = depth >= 1f;
        _innerVoicePresenceGain = .15f * strength;
        _innerVoiceReverbGain = .45f * strength;
        float cutoff = Math.Min(1800f, _sampleRate * .45f);
        float fullCoeff = 1f - MathF.Exp(-2f * MathF.PI * cutoff / _sampleRate);
        // Unity coefficient is exact digital bypass; move the pole gradually to its full position.
        _innerVoiceLpCoeff = _innerVoiceFullStrength ? fullCoeff : Dsp.Lerp(1f, fullCoeff, strength);
    }

    /// <summary>Moves the complete input band toward the fixed 450 Hz full cutoff without level makeup.</summary>
    private void PrepareMuffled(float amount)
    {
        if (_muffledPreparedAmount == amount) return;
        _muffledPreparedAmount = amount;
        float depth = Math.Clamp(amount, 0f, 1f);
        _muffledStrength = depth * (2f - depth);
        _muffledInverseStrength = 1f / _muffledStrength;
        _muffledFullStrength = depth >= 1f;
        float nyquist = _sampleRate * .5f;
        float fullCutoff = Math.Min(450f, _sampleRate * .45f);
        float cutoff = _muffledFullStrength ? fullCutoff
            : nyquist * MathF.Pow(fullCutoff / nyquist, _muffledStrength);
        cutoff = Math.Min(cutoff, _sampleRate * .4999f);
        _environmentEq!.SetLowPassFilter(_sampleRate, cutoff, .707f);
        _environmentEq2!.SetLowPassFilter(_sampleRate, cutoff, .707f);
    }

    /// <summary>Configures environment-specific filters and reflection parameters without retaining the previous environment's EQ.</summary>
    private void Setup(SpatialEnvironment env)
    {
        _profileReverb = null;
        _maximumTailSamples = checked(_sampleRate * 4);
        // Nullify all specific EQ filters to prevent state leakage between environments.
        _reverbHpFilter = null;
        _reverbLpFilter = null;
        _forestHfDamp = null;
        _environmentEq = null;
        _environmentEq2 = null;

        _hasReverbEq = false;
        _hasForestEq = false;

        float nyq = _sampleRate * 0.45f;
        // Caps environment filter frequencies at the configured Nyquist safety limit.
        float Safe(float f) => Math.Min(f, nyq);

        switch (env)
        {
            case SpatialEnvironment.Room:
                PrepareReverb(RoomParameters, RoomQuietWindowSeconds, RoomReturnGain);
                break;

            case SpatialEnvironment.LivingRoom:
                PrepareReverb(LivingRoomParameters, RoomQuietWindowSeconds, RoomReturnGain);
                break;

            case SpatialEnvironment.Stage:
                PrepareReverb(StageParameters, ReverbQuietWindowSeconds, DefaultReverbReturnGain);
                break;

            case SpatialEnvironment.ConcreteHall:
                PrepareReverb(ConcreteHallParameters, ReverbQuietWindowSeconds, DefaultReverbReturnGain);
                break;

            case SpatialEnvironment.Dungeon:
                PrepareReverb(DungeonParameters, ReverbQuietWindowSeconds, DefaultReverbReturnGain);
                break;

            case SpatialEnvironment.Cave:
                PrepareReverb(CaveParameters, ReverbQuietWindowSeconds, DefaultReverbReturnGain);
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
                _muffledPreparedAmount = float.NaN;
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
                _innerVoicePreparedAmount = float.NaN;
                ConfigureCombsByRt60(3.5f, 0.85f);
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(150f), 0.707f);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(1800f), 0.707f);
                _hasReverbEq = true;

                // 8ms is the optimal Haas effect delay to bring the source "inside" the head.
                _innerVoiceMicroDelaySamples = 0.008f * _sampleRate;
                break;
        }
    }

    /// <summary>Prepares one fixed reverb and a bounded tail budget derived from its slowest decay band.</summary>
    private void PrepareReverb(SpatialReverbParameters parameters, float quietSeconds, float returnGain)
    {
        _profileReverb = new SpatialReverbKernel(_sampleRate, parameters);
        _profileReturnGain = returnGain;
        _profileQuietWindowSamples = Math.Max(1, (int)MathF.Ceiling(_sampleRate * quietSeconds));
        double slowestDecay = parameters.DecaySeconds * Math.Max(1d, Math.Max(parameters.DecayHFRatio, parameters.DecayLFRatio));
        // Allow two RT60 intervals plus the input delays and final quiet observation span.
        int seconds = Math.Max(4, (int)Math.Ceiling(2 * slowestDecay
            + parameters.ReflectionsDelaySeconds + parameters.LateReverbDelaySeconds + quietSeconds));
        _maximumTailSamples = checked(_sampleRate * seconds);
    }

    // =========================================================================
    // ACOUSTIC ALGORITHMS
    // =========================================================================

    /// <summary>Processes one sample through the reverb input EQ, parallel comb bank, and all-pass diffusion stages.</summary>
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

    /// <summary>Sets comb feedback from the target RT60 and scales damping for the current sample rate.</summary>
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

    /// <summary>Returns the first prime number greater than or equal to the requested delay length.</summary>
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

    /// <summary>Returns the smallest power-of-two buffer size that can hold the requested capacity.</summary>
    private static int NextPowerOf2(int minSize)
    {
        int size = 1;
        while (size < minSize) size <<= 1;
        return size;
    }
}
