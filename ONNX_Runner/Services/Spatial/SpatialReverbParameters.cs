// C# adaptation for Tsubaki TTS, 2026-10-31.
// Derived from OpenAL Soft 1.23.1.
// SPDX-License-Identifier: GPL-3.0-or-later
// This adapted copy uses GPL-3.0-or-later under LGPL-2.0 section 3; see LICENSE.
// Modified 2026-10-31: integrated into the Tsubaki spatial service namespace.
namespace ONNX_Runner.Services.Spatial;

/// <summary>Numeric DSP parameters. Profiles, intensity and output volume belong to the caller.</summary>
public readonly record struct SpatialReverbParameters(
    float Density, float Diffusion, float Gain, float GainHF, float GainLF,
    float DecaySeconds, float DecayHFRatio, float DecayLFRatio,
    float ReflectionsGain, float ReflectionsDelaySeconds,
    float LateReverbGain, float LateReverbDelaySeconds,
    float ModulationSeconds, float ModulationDepth,
    float AirAbsorptionGainHF, float HFReferenceHz, float LFReferenceHz, bool DecayHFLimit)
{
    internal void Validate()
    {
        Range(Density, 0, 1, nameof(Density)); Range(Diffusion, 0, 1, nameof(Diffusion));
        Range(Gain, 0, 1, nameof(Gain)); Range(GainHF, 0, 1, nameof(GainHF)); Range(GainLF, 0, 1, nameof(GainLF));
        Range(DecaySeconds, .1f, 20, nameof(DecaySeconds));
        Range(DecayHFRatio, .1f, 2, nameof(DecayHFRatio)); Range(DecayLFRatio, .1f, 2, nameof(DecayLFRatio));
        Range(ReflectionsGain, 0, 3.16f, nameof(ReflectionsGain));
        Range(ReflectionsDelaySeconds, 0, .3f, nameof(ReflectionsDelaySeconds));
        Range(LateReverbGain, 0, 10, nameof(LateReverbGain));
        Range(LateReverbDelaySeconds, 0, .1f, nameof(LateReverbDelaySeconds));
        Range(ModulationSeconds, .04f, 4, nameof(ModulationSeconds));
        Range(ModulationDepth, 0, 1, nameof(ModulationDepth));
        Range(AirAbsorptionGainHF, .892f, 1, nameof(AirAbsorptionGainHF));
        Range(HFReferenceHz, 1000, 20000, nameof(HFReferenceHz));
        Range(LFReferenceHz, 20, 1000, nameof(LFReferenceHz));
    }

    private static void Range(float value, float minimum, float maximum, string name)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, value, $"Expected a finite value in [{minimum}, {maximum}].");
    }
}
