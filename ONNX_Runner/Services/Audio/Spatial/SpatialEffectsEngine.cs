using ONNX_Runner.Services.Audio;
using ONNX_Runner.Models.Audio;
using System.Runtime.CompilerServices;
using NAudio.Dsp;

namespace ONNX_Runner.Services.Audio.Spatial;

/// <summary>
/// Request-local spatial processing, including filter and delay state, intensity and tail handling.
/// Fixed environment settings are defined in <see cref="SpatialProfiles"/>.
/// </summary>
public class SpatialEffectsEngine
{
    private readonly int _sampleRate;
    private readonly int _reverbSilenceWindowSamples;

    private SpatialReverbKernel? _profileReverb;
    private SpatialEchoKernel? _profileEcho;
    private SpatialTapDelay? _profileDelay;
    private SpatialFilterKernel? _echoDamping;
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
    private SpatialFilterKernel? _environmentEq;
    private SpatialFilterKernel? _environmentEq2;

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
    private float _muffledOutputGain;

    // =========================================================================
    // PRE-COMPUTED DELAY SIZES
    // =========================================================================

    private float _innerVoiceMicroDelaySamples;

    // =========================================================================
    // BRANCH-HOISTING FLAGS
    // =========================================================================

    private bool _hasReverbEq;
    private bool _hasMuffledOutput;

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

        _innerVoiceMicroDelay = new DelayBuffer(SamplesFor(SpatialProfiles.InnerVoice.DelayCapacityMs));
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

    /// <summary>Sample-rate-scaled safety budget for the active profile's complete effect tail.</summary>
    internal int MaximumTailSamples => _maximumTailSamples;

    /// <summary>Quiet observation span covering the active reflection path before a tail can end.</summary>
    internal int TailSilenceWindowSamples
    {
        get
        {
            int probeSamples = Math.Max(1, _sampleRate / 50);
            if (_current is SpatialEnvironment.None or SpatialEnvironment.Muffled) return probeSamples;
            if (_profileReverb is not null || _profileEcho is not null || _profileDelay is not null)
                return _profileQuietWindowSamples;
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
        _profileEcho?.Reset();
        _profileDelay?.Reset();
        _echoDamping?.Reset();
        _profileDirectFilter?.Reset();
        _environmentEq?.Reset();
        _environmentEq2?.Reset();
        foreach (var c in _combs) c.Clear();
        foreach (var a in _allPasses) a.Clear();

        _innerVoiceMicroDelay.Clear();

        _innerVoiceLpState = 0f;
        if (_current == SpatialEnvironment.InnerVoice && _hasReverbEq)
        {
            _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Math.Min(SpatialProfiles.InnerVoice.ReverbLowCutHz, _sampleRate * .45f), SpatialProfiles.InnerVoice.FilterQ);
            _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Math.Min(SpatialProfiles.InnerVoice.LowPassHz, _sampleRate * .45f), SpatialProfiles.InnerVoice.FilterQ);
        }
    }

    /// <summary>
    /// Published reverb profiles use inverse-square intensity for reflections and optional direct coloration.
    /// Inverse-square intensity (1-(1-mix)²) moves the Muffled cutoff; cubic intensity adds a mild late gain.
    /// DeepWater passes the original intensity to Underwater and Muffled in sequence.
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
            if (_hasMuffledOutput) ApplyMuffled(buffer, mix);
            return;
        }

        if (_profileDelay is { } delay)
        {
            ApplySingleEcho(buffer, mix, delay, _echoDamping!);
            return;
        }
        if (_profileEcho is { } echo)
        {
            ApplyDualEcho(buffer, mix, echo);
            return;
        }

        switch (env)
        {
            case SpatialEnvironment.Muffled:
                ApplyMuffled(buffer, mix);
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

    /// <summary>Mixes one feed-forward return; only the stored repeat receives damped feedback.</summary>
    private static void ApplySingleEcho(Span<float> buffer, float amount, SpatialTapDelay delay, SpatialFilterKernel damping)
    {
        float position = Math.Clamp(amount, 0, 1);
        float gain = SpatialProfiles.Echo.ReturnGain * (position * (2 - position));
        for (int i = 0; i < buffer.Length; i++)
        {
            float input = buffer[i], returned = delay.Read();
            delay.Write(input + damping.Process(returned) * SpatialProfiles.Echo.Feedback);
            buffer[i] = input + returned * gain;
        }
    }

    /// <summary>Mixes the original mono two-tap echo without changing its feedback or delay timing.</summary>
    private static void ApplyDualEcho(Span<float> buffer, float amount, SpatialEchoKernel echo)
    {
        float position = Math.Clamp(amount, 0, 1);
        float gain = SpatialProfiles.DualEcho.ReturnGain * (position * (2 - position));
        for (int i = 0; i < buffer.Length; i++)
        {
            float input = buffer[i];
            buffer[i] = input + echo.Process(input) * gain;
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
        _innerVoicePresenceGain = SpatialProfiles.InnerVoice.PresenceGain * strength;
        _innerVoiceReverbGain = SpatialProfiles.InnerVoice.ReturnGain * strength;
        float cutoff = Math.Min(SpatialProfiles.InnerVoice.LowPassHz, _sampleRate * .45f);
        float fullCoeff = 1f - MathF.Exp(-2f * MathF.PI * cutoff / _sampleRate);
        // Unity coefficient is exact digital bypass; move the pole gradually to its full position.
        _innerVoiceLpCoeff = _innerVoiceFullStrength ? fullCoeff : Dsp.Lerp(1f, fullCoeff, strength);
    }

    /// <summary>Applies the existing occlusion path to the complete incoming signal, including any reflections.</summary>
    private void ApplyMuffled(Span<float> buffer, float amount)
    {
        PrepareMuffled(amount);
        for (int i = 0; i < buffer.Length; i++)
        {
            float input = Dsp.KillDenormal(buffer[i]);
            buffer[i] = _environmentEq2!.Process(_environmentEq!.Process(input)) * _muffledOutputGain;
        }
    }

    /// <summary>Allocates independent post-filter state for the Muffled path.</summary>
    private void SetupMuffled()
    {
        _muffledPreparedAmount = float.NaN;
        float cutoff = Math.Min(SpatialProfiles.Muffled.CutoffHz, _sampleRate * .45f);
        _environmentEq = SpatialFilterKernel.FromSlope(_sampleRate, SpatialFilterType.LowPass, cutoff);
        _environmentEq2 = SpatialFilterKernel.FromSlope(_sampleRate, SpatialFilterType.LowPass, cutoff);
    }

    /// <summary>Moves the input band toward 450 Hz; a mild cubic gain rises mainly near full intensity.</summary>
    private void PrepareMuffled(float amount)
    {
        if (_muffledPreparedAmount == amount) return;
        _muffledPreparedAmount = amount;
        float depth = Math.Clamp(amount, 0f, 1f);
        float strength = depth * (2f - depth);
        float nyquist = _sampleRate * .5f;
        float fullCutoff = Math.Min(SpatialProfiles.Muffled.CutoffHz, _sampleRate * .45f);
        float cutoff = depth >= 1f ? fullCutoff
            : nyquist * MathF.Pow(fullCutoff / nyquist, strength);
        cutoff = Math.Min(cutoff, _sampleRate * .4999f);
        _environmentEq!.SetLowPass(_sampleRate, cutoff);
        _environmentEq2!.SetLowPass(_sampleRate, cutoff);
        _muffledOutputGain = MathF.Pow(10f, SpatialProfiles.Muffled.MaximumGainDb * depth * depth * depth / 20f);
    }

    /// <summary>Configures environment-specific filters and reflection parameters without retaining the previous environment's EQ.</summary>
    private void Setup(SpatialEnvironment env)
    {
        _profileReverb = null;
        _profileEcho = null;
        _profileDelay = null;
        _echoDamping = null;
        _profileQuietWindowSamples = 0;
        _profileDirectFilter = null;
        _profileDirectGain = 1;
        _maximumTailSamples = checked(_sampleRate * 4);
        // Nullify all specific EQ filters to prevent state leakage between environments.
        _reverbHpFilter = null;
        _reverbLpFilter = null;
        _environmentEq = null;
        _environmentEq2 = null;

        _hasReverbEq = false;
        _hasMuffledOutput = false;

        float nyq = _sampleRate * 0.45f;
        // Caps environment filter frequencies at the configured Nyquist safety limit.
        float Safe(float f) => Math.Min(f, nyq);

        if (SpatialProfiles.GetReverb(env) is { } profile)
        {
            PrepareReverb(profile.Parameters, profile.QuietWindowSeconds, profile.ReturnGain);
            if (env is SpatialEnvironment.Underwater or SpatialEnvironment.DeepWater)
            {
                _profileDirectFilter = SpatialFilterKernel.FromSlope(_sampleRate, SpatialFilterType.HighShelf,
                    Math.Min(SpatialProfiles.Underwater.DirectFilterReferenceHz, _sampleRate * .49f), SpatialProfiles.Underwater.DirectGainHF);
                _profileDirectGain = SpatialProfiles.Underwater.DirectGain;
                if (env == SpatialEnvironment.DeepWater)
                {
                    SetupMuffled();
                    _hasMuffledOutput = true;
                }
            }
            return;
        }

        switch (env)
        {
            // Occlusion / Muffled effect (Hearing through thick walls or heavy earplugs).
            // Two Butterworth low-pass stages attenuate at 24 dB/oct without adding reflections or saturation.
            case SpatialEnvironment.Muffled:
                SetupMuffled();
                break;

            // A quiet, dark diffuse tail supports the authored inner-monologue coloration.
            case SpatialEnvironment.InnerVoice:
                _innerVoicePreparedAmount = float.NaN;
                ConfigureCombsByRt60(SpatialProfiles.InnerVoice.DecaySeconds, SpatialProfiles.InnerVoice.Damping);
                _reverbHpFilter = BiQuadFilter.HighPassFilter(_sampleRate, Safe(SpatialProfiles.InnerVoice.ReverbLowCutHz), SpatialProfiles.InnerVoice.FilterQ);
                _reverbLpFilter = BiQuadFilter.LowPassFilter(_sampleRate, Safe(SpatialProfiles.InnerVoice.LowPassHz), SpatialProfiles.InnerVoice.FilterQ);
                _hasReverbEq = true;

                // This 8 ms mono tap is intentional timbral coloration, not a binaural model.
                _innerVoiceMicroDelaySamples = SpatialProfiles.InnerVoice.MicroDelaySeconds * _sampleRate;
                break;

            case SpatialEnvironment.Echo:
                int delay = Math.Max((int)(SpatialProfiles.Echo.DelaySeconds * _sampleRate + .5f), 1);
                _profileDelay = new SpatialTapDelay(delay);
                _echoDamping = SpatialFilterKernel.FromSlope(_sampleRate, SpatialFilterType.HighShelf,
                    Math.Min(SpatialProfiles.Echo.DampingReferenceHz, _sampleRate * .49f), SpatialProfiles.Echo.DampingReferenceGain);
                PrepareEchoTail(delay, SpatialProfiles.Echo.Feedback);
                break;

            case SpatialEnvironment.DualEcho:
                _profileEcho = new SpatialEchoKernel(_sampleRate, SpatialProfiles.DualEcho.Parameters);
                PrepareEchoTail(_profileEcho.SecondTapDelaySamples, SpatialProfiles.DualEcho.Parameters.Feedback);
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

    /// <summary>Covers the last initial tap, 120 dB of feedback decay, and two complete quiet feedback cycles.</summary>
    private void PrepareEchoTail(int longestDelaySamples, float feedback)
    {
        _profileQuietWindowSamples = Math.Max(_sampleRate / 50, checked(longestDelaySamples * 2 + 1));
        int decays = feedback > 0 ? (int)Math.Ceiling(Math.Log(1e-6) / Math.Log(feedback)) : 0;
        double seconds = ((decays + 1d) * longestDelaySamples + _profileQuietWindowSamples) / _sampleRate;
        _maximumTailSamples = checked(_sampleRate * Math.Max(4, (int)Math.Ceiling(seconds)));
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
        // Damp multiplies the stored state, so scale the pole rather than the feedforward coefficient.
        float scaledDamp = MathF.Pow(damp, 48000f / _sampleRate);
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
