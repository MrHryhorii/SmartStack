using ONNX_Runner.Models.Audio;
using System.Runtime.CompilerServices;

namespace ONNX_Runner.Services.Audio.Effects;

/// <summary>Fixed character-effect tuning and routing; contains no request state or sample processing.</summary>
internal static class AudioEffectProfiles
{
    internal enum RoutingMode
    {
        PowerMatchedCrossfade,
        ModulationInsert,
        StrictInsert,
        StrictCrossfade,
        CrusherInsert,
        RingInsert,
        TapeInsert
    }

    /// <summary>Selects the wet/dry routing policy used to mix each voice effect with the source signal.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static RoutingMode GetRoutingMode(VoiceEffectType type)
    {
        return type switch
        {
            VoiceEffectType.Telephone => RoutingMode.PowerMatchedCrossfade,
            VoiceEffectType.G711MuLaw => RoutingMode.PowerMatchedCrossfade,
            VoiceEffectType.G711ALaw => RoutingMode.PowerMatchedCrossfade,
            VoiceEffectType.Overdrive => RoutingMode.PowerMatchedCrossfade,
            VoiceEffectType.Bitcrusher => RoutingMode.CrusherInsert,
            VoiceEffectType.RingModulator => RoutingMode.RingInsert,
            VoiceEffectType.Flanger => RoutingMode.ModulationInsert,
            VoiceEffectType.Chorus => RoutingMode.ModulationInsert,
            VoiceEffectType.LoFiTape => RoutingMode.TapeInsert,
            VoiceEffectType.DecoderGlitch => RoutingMode.StrictInsert,
            VoiceEffectType.TacticalRadio => RoutingMode.PowerMatchedCrossfade,
            VoiceEffectType.FmRadio => RoutingMode.PowerMatchedCrossfade,
            _ => RoutingMode.StrictInsert
        };
    }

    internal static class Common
    {
        // Sample-domain tuning is calibrated to the reference voice-model rate.
        internal const int ReferenceSampleRate = 22050;
        internal const float NyquistFactor = 0.45f;
        internal const float DcCutoffHz = 35f;
        internal const float EnvelopeAttack = 0.3847673f;
        internal const float EnvelopeRelease = 0.010852337f;
        internal const float ThermalCoefficient = 0.00021773577f;
        internal const float CompletionSilenceFloor = 1e-7f;
    }

    internal static class Telephone
    {
        internal const float HighPassHz = 380f;
        internal const float BandPassQ = 1.0f;
        internal const float BodyPeakHz = 820f;
        internal const float BodyPeakQ = 6.0f;
        internal const float BodyPeakGainDb = 8.0f;
        internal const float CavityPeakHz = 950f;
        internal const float CavityPeakQ = 4.5f;
        internal const float CavityPeakGainDb = 7.5f;
        internal const float PresenceHz = 1800f;
        internal const float PresenceQ = 2.2f;
        internal const float PresenceGainDb = 6.0f;
        internal const float LowPassHz = 3400f;
        internal const float Drive = 3.5f;
        internal const float BaseNoise = 0.003f;
        internal const float ThermalNoise = 0.002f;
        internal const float LineNoise = 0.018f;
    }

    internal static class Overdrive
    {
        // Apply the fixed wet trim after character generation, preserving saturation.
        internal const float OutputGain = 0.5347782f;
        internal const float Drive = 1f + 4.808956f;
        internal const float HighPassHz = 180f;
        internal const float HighPassQ = 0.8f;
        internal const float LowPassQ = 0.707f;
        internal const float ThermalBias = .06f;
        internal static readonly float Trim = 1f / MathF.Sqrt(Drive);
    }

    internal static class Bitcrusher
    {
        internal const double MaximumLevelGain = 4;
        internal const double MinimumPower = 1e-8;
        internal const float HighPassHz = 130f;
        internal const float HighPassQ = 0.707f;
        internal const float MaximumBits = 16f;
        internal const float MinimumBits = 4f;
        internal const float BitReduction = 12f;
        internal const float FullClockHz = 11025f;
        internal const float ThermalJitter = 0.005f;
    }

    internal static class RingModulator
    {
        internal const float HighPassHz = 130f;
        internal const float HighPassQ = 0.707f;
        internal const float LevelWindowMs = 1200f;
        internal const double MaximumRiseDbPerSecond = 24;
        internal const float MakeupDepth = .8f;
        internal const float CarrierHz = 30f;
        internal const float ThermalPitchHz = 1.5f;
    }

    internal static class Modulation
    {
        internal const float DelayOffsetMs = .1f;
        internal const float OutputWetScale = .85f;
    }

    internal static class Flanger
    {
        internal const float LfoHz = 0.45f;
        internal const float BaseDelayMs = 1.7f;
        internal const float DepthMs = 1.1f;
        internal const float WetScale = .72f;
        internal const float Feedback = .75f;
    }

    internal static class Chorus
    {
        internal const float LfoHz = 0.55f;
        internal const float SecondLfoHz = 0.83f;
        internal const float BaseDelayMs = 14.9f;
        internal const float SecondBaseDelayMs = 23.9f;
        internal const float DepthMs = 6f;
        internal const float SecondDepthMs = 7f;
        internal const float WetScale = .45f;
        internal const float ThermalDelayMs = .006f;
    }

    internal static class Tape
    {
        internal const float HissLevel = 0.8f;
        internal const float HissHighPassHz = 1400f;
        internal const float HissLowPassHz = 9000f;
        // Partial compensation of record-stage gain retains the accepted playback level.
        internal const float PlaybackCompensation = 0.423369f;
        internal const float SlewCoefficient = 0.004348576f;
        internal const float HighPassHz = 80f;
        internal const float HighPassQ = 0.707f;
        internal const float EqualizationHz = 1326f;
        internal const float FlutterNoiseHz = 35f;
        internal const float CompressorAttackMs = 2f;
        internal const float CompressorReleaseMs = 200f;
        internal const float AzimuthLowPassHz = 4500f;
        internal const float SpeakerBumpHz = 120f;
        internal const float SpeakerBumpQ = 0.8f;
        internal const float SpeakerBumpGainDb = 4.0f;
        internal const float SpeakerResonanceHz = 700f;
        internal const float SpeakerResonanceQ = 1f;
        internal const float SpeakerResonanceGainDb = 1.5f;
        internal const float SpeakerHighPassHz = 70f;
        internal const float SpeakerHighPassQ = 0.707f;
        internal const float SpeakerLowPassHz = 7500f;
        internal const float SpeakerLowPassQ = 0.707f;
        internal const float HissColorHz = 4500f;
        internal const float HissColorQ = 1f;
        internal const float HissColorGainDb = -2f;
        internal const float WowHz = 0.4f;
        internal const float SecondWowHz = 0.124f;
        internal const float FlutterHz = 8.5f;
        internal const float AzimuthHz = 0.37f;
        internal const float PreEmphasisScale = 0.8f;
        internal const float SaturationDrive = 2.0f;
        internal const float SaturationCompensation = 0.35f;
        internal const float CompressorThreshold = 0.38f;
        internal const float CompressorMakeup = 0.90f;
        internal const float CharacterDelayMs = 0.1f;
        internal const float WearDelayMs = 1.3f;
        internal const float HighFrequencyCut = 0.50f;
        internal const float AzimuthMix = 0.45f;
        internal const float GrainScale = 0.0020f;
        internal const float BaseHiss = 0.0025f;
        internal const float WearHiss = 0.0075f;
        internal const float FlutterNoiseScale = 0.50f;
        internal const float PerceptualTrim = 0.25f;
        internal const float CompressorRatio = 3.5f;
        internal const float WowDepthMs = 0.8f;
        internal const float SecondWowDepthMs = 0.4f;
        internal const float FlutterDepthMs = 0.15f;
        internal const float ThermalHiss = 0.003f;
    }

    internal static class DecoderGlitch
    {
        internal const int ReferenceCaptureSamples = 4096;
        internal const int DetectorWindowsPerSecond = 100;
        internal const float AttackCoefficient = 0.99334526f;
        internal const float ReleaseCoefficient = 0.0021755695f;
        internal const float ZcrMemory = 0.85f;
        internal const float ZcrInputWeight = 0.15f;
        internal const float MinimumEnergy = 0.004f;
        internal const float MaximumZcr = 0.08f;
        internal const float BaseChance = 0.01f;
        internal const float DurationMs = 600f;
        internal const float KneeAmount = 0.1f;
        internal const float MinimumLoopMs = 5f;
        internal const float MaximumLoopMs = 33f;
        internal const float LoopFloorMs = 2f;
        internal const float CooldownMs = 200f;
        internal const float ExtraCooldownMs = 200f;
        internal const float ChanceAccumulation = 0.001f;
        internal const float PlaybackDrive = 1.15f;
        internal const int LoopFadeMs = 2;
        internal const int ReleaseFadeMs = 25;
    }

    internal static class TacticalRadio
    {
        internal const float OutputGain = 0.63f;
        internal const float HighPassHz = 320f;
        internal const float BandPassQ = 0.707f;
        internal const float FirstPeakHz = 1100f;
        internal const float FirstPeakQ = 1.4f;
        internal const float FirstPeakGainDb = 3.0f;
        internal const float SecondPeakHz = 2100f;
        internal const float SecondPeakQ = 1.8f;
        internal const float SecondPeakGainDb = 5.0f;
        internal const float ThirdPeakHz = 2800f;
        internal const float ThirdPeakQ = 1.4f;
        internal const float ThirdPeakGainDb = 3.0f;
        internal const float LowPassHz = 3400f;
        internal const float CompressorAttackMs = 1f;
        internal const float CompressorReleaseMs = 80f;
        internal const float ReconstructionHz = 2400f;
        internal const float AccumulatorDecaySeconds = 0.025f;
        internal const float StepDecaySeconds = 0.004f;
        internal const float MinimumStep = 0.0043537416f;
        internal const float MaximumStep = 0.26122448f;
        internal const float StepGrowth = 0.017414967f;
        internal const float CompanderThreshold = 0.1f;
        internal const float QuietGain = 1.15f;
        internal const float LoudGain = 0.92f;
        internal const float InputDrive = 2.0f;
        internal const float CompressorThreshold = 0.25f;
        internal const float CompressorRatio = 4f;
        internal const float RfGain = 1.5f;
        internal const float AccumulatorLimit = 1.2f;
        internal const float SaturationDrive = 1.4f;
        internal const float OutputTrim = 0.75f;
    }

    internal static class FmRadio
    {
        internal const float OutputBoostDb = 1.7f;
        internal static readonly float OutputGain = .78f * MathF.Pow(10f, OutputBoostDb / 20f);
    }

    internal static class G711
    {
        internal const float HighPassHz = 300f;
        internal const float LowPassHz = 3400f;
        internal const float BandPassQ = 0.707f;
    }
}
