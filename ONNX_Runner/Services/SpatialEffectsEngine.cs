using ONNX_Runner.Models;
using ONNX_Runner.Services.Spatial;
using System.Runtime.CompilerServices;
using NAudio.Dsp;

namespace ONNX_Runner.Services;

/// <summary>
/// Server-side spatial acoustics engine.
/// Published environments, including Underwater, use an OpenAL-derived mono reverb; Muffled and InnerVoice retain their current algorithms.
///
/// Design priority: PERCEPTUAL RECOGNIZABILITY. Each environment has one
/// dominant, instantly identifiable trait rather than a cluster of small
/// parameter tweaks. Direct coloration and reflected sound are configured separately.
///
/// - Room:         Short, clear reflections with moderate HF absorption.
/// - LivingRoom:   Short, soft, warm. Heavy HF damping. Recognizable at any mix.
/// - Stage:        Wooden performance hall with warm, delayed reflections.
/// - ConcreteHall: Large concert hall with a long, dense reflected tail.
/// - Dungeon:      Compact stone chamber with dense, short reflections.
/// - Cave:         Reflective rocky cavern with a longer decay.
/// - Forest:       Delayed, scattered outdoor reflections with strong HF absorption.
/// - Muffled:      Occlusion. Gradual low-pass filtering down to 450Hz with no reflections.
///                 Simulates hearing sound through walls, earplugs, or UI pause menus.
/// - Underwater:   Muffled direct speech with the published modulated underwater reverb.
/// - InnerVoice:   Cinematic telepathy. Uses the Haas effect (micro-delay) and a dynamic
///                 low-pass filter to pull the voice inside the listener's head, supported 
///                 by a dark, wall-less shadow reverb.
/// </summary>
public class SpatialEffectsEngine
{
    private readonly int _sampleRate;
    private readonly int _reverbSilenceWindowSamples;

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
    private static readonly SpatialReverbParameters ForestParameters = new(
        1, .3f, .3162f, .0224f, 1, 1.49f, .54f, 1, .0525f, .162f,
        .7682f, .088f, .25f, 0, .9943f, 5000, 250, true);
    private static readonly SpatialReverbParameters UnderwaterParameters = new(
        .3645f, 1, .3162f, .01f, 1, 1.49f, .1f, 1, .5963f, .007f,
        7.0795f, .011f, 1.18f, .348f, .9943f, 5000, 250, true);
    // EFX direct-filter balance used by OpenMW's underwater environment.
    private const float UnderwaterDirectGain = .9f;
    private const float UnderwaterDirectGainHF = .125f;
    private const float UnderwaterReturnGain = 1.5f;
    // Keep the accepted room balance and separate return levels for standard and underwater profiles.
    private const float RoomReturnGain = 64f;
    private const float DefaultReverbReturnGain = 2f;
    // Covers two passes of this profile's longest feedback path (approximately 209 ms).
    private const float RoomQuietWindowSeconds = .45f;
    // Cover the longer input and feedback paths of the other published profiles.
    private const float ReverbQuietWindowSeconds = .65f;
    private SpatialReverbKernel? _profileReverb;
    private SpatialFilterKernel? _profileDirectFilter;
    private float _profileDirectGain = 1;
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

    private readonly DelayBuffer _innerVoiceMicroDelay;

    // =========================================================================
    // EQ FILTERS
    // =========================================================================

    private BiQuadFilter? _reverbHpFilter;
    private BiQuadFilter? _reverbLpFilter;

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

    private float _innerVoiceMicroDelaySamples;

    // =========================================================================
    // BRANCH-HOISTING FLAGS
    // =========================================================================

    private bool _hasReverbEq;

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
                SpatialEnvironment.InnerVoice => _reverbSilenceWindowSamples + _innerVoiceMicroDelaySamples,
                _ => _reverbSilenceWindowSamples
            };
            // Dense feedback can rise again after a quiet pass; observe two complete path spans.
            return Math.Max(probeSamples, (int)MathF.Ceiling(pathSamples * 2) + 2);
        }
    }

    /// <summary>Clears delay, filter, modulation, and tail state before reusing the environment processor.</summary>
    public void Reset()
    {
        _profileReverb?.Reset();
        _profileDirectFilter?.Reset();
        foreach (var c in _combs) c.Clear();
        foreach (var a in _allPasses) a.Clear();

        _innerVoiceMicroDelay.Clear();

        _innerVoiceLpState = 0f;
    }

    /// <summary>
    /// Published reverb profiles use inverse-square intensity for reflections and optional direct coloration.
    /// Inverse-square intensity (1-(1-mix)²) moves the Muffled cutoff and saturation depth.
    /// InnerVoice uses that intensity for internal coloration.
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
            ApplyReverb(buffer, mix, profileReverb, _profileReturnGain, _profileDirectFilter, _profileDirectGain);
            return;
        }

        switch (env)
        {
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

    /// <summary>Blends optional direct coloration and a fixed reflected response; final gain and limiting remain downstream.</summary>
    private static void ApplyReverb(Span<float> buffer, float amount, SpatialReverbKernel reverb, float fullReturnGain,
        SpatialFilterKernel? directFilter, float directGain)
    {
        float position = Math.Clamp(amount, 0, 1);
        float strength = position * (2 - position);
        float returnGain = fullReturnGain * strength;
        if (directFilter is null)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                float direct = buffer[i];
                buffer[i] = direct + reverb.Process(direct) * returnGain;
            }
            return;
        }
        if (position == 1)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                float direct = buffer[i];
                buffer[i] = directFilter.Process(direct) * directGain + reverb.Process(direct) * returnGain;
            }
            return;
        }
        for (int i = 0; i < buffer.Length; i++)
        {
            float direct = buffer[i];
            float colored = directFilter.Process(direct) * directGain;
            buffer[i] = Dsp.Lerp(direct, colored, strength) + reverb.Process(direct) * returnGain;
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
        _profileDirectFilter = null;
        _profileDirectGain = 1;
        _maximumTailSamples = checked(_sampleRate * 4);
        // Nullify all specific EQ filters to prevent state leakage between environments.
        _reverbHpFilter = null;
        _reverbLpFilter = null;
        _environmentEq = null;
        _environmentEq2 = null;

        _hasReverbEq = false;

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

            case SpatialEnvironment.Forest:
                PrepareReverb(ForestParameters, ReverbQuietWindowSeconds, DefaultReverbReturnGain);
                break;

            // Occlusion / Muffled effect (Hearing through thick walls or heavy earplugs).
            // Uses a cascaded 24dB/oct low-pass at 450Hz to aggressively destroy 
            // speech intelligibility, but keeps Q=0.707 to avoid underwater resonance.
            case SpatialEnvironment.Muffled:
                _muffledPreparedAmount = float.NaN;
                _environmentEq = BiQuadFilter.LowPassFilter(_sampleRate, Safe(450f), 0.707f);
                _environmentEq2 = BiQuadFilter.LowPassFilter(_sampleRate, Safe(450f), 0.707f);
                break;

            case SpatialEnvironment.Underwater:
                PrepareReverb(UnderwaterParameters, ReverbQuietWindowSeconds, UnderwaterReturnGain);
                _profileDirectFilter = SpatialFilterKernel.FromSlope(_sampleRate, SpatialFilterType.HighShelf,
                    Math.Min(5000f, _sampleRate * .49f), UnderwaterDirectGainHF);
                _profileDirectGain = UnderwaterDirectGain;
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
