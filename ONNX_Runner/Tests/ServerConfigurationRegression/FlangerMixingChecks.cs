using ONNX_Runner.Models;

internal static class FlangerMixingChecks
{
    /// <summary>Checks the current eased Flanger path and its prepared output correction.</summary>
    internal static void Run(Action<string, Action> check) => ModulationCurveChecks.Run(check, VoiceEffectType.Flanger);
}
