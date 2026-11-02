using ONNX_Runner.Models.Audio;

namespace ONNX_Runner.Services.Audio.Spatial;

/// <summary>Fixed spatial profiles and authored balances; contains no per-request DSP state.</summary>
internal static class SpatialProfiles
{
    internal readonly record struct ReverbProfile(
        SpatialReverbParameters Parameters, float QuietWindowSeconds, float ReturnGain);

    // Cover two passes of the room feedback path and the longer paths of other profiles.
    private const float RoomQuietWindowSeconds = .45f;
    private const float ReverbQuietWindowSeconds = .65f;

    // Published EFX parameters; accepted output balances are kept separately from the DSP kernel.
    // Short, clear reflections with moderate high-frequency absorption.
    private static readonly ReverbProfile Room = new(
        new(
            .4287f, 1, .3162f, .5929f, 1, .4f, .83f, 1, .1503f, .002f,
            1.0629f, .003f, .25f, 0, .9943f, 5000, 250, true),
        QuietWindowSeconds: RoomQuietWindowSeconds, ReturnGain: 32f * 1.5f);

    // Short, soft reflections with heavy high-frequency damping.
    private static readonly ReverbProfile LivingRoom = new(
        new(
            .9766f, 1, .3162f, .001f, 1, .5f, .1f, 1, .2051f, .003f,
            .2805f, .004f, .25f, 0, .9943f, 5000, 250, true),
        QuietWindowSeconds: RoomQuietWindowSeconds, ReturnGain: 32f * 2f);

    // Small tiled space with strong, close reflections.
    private static readonly ReverbProfile Bathroom = new(
        new(
            .1715f, 1, .3162f, .2512f, 1, 1.49f, .54f, 1, .6531f, .007f,
            3.2734f, .011f, .25f, 0, .9943f, 5000, 250, true),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 1.5f);

    // Wooden performance hall with warm, delayed reflections.
    private static readonly ReverbProfile Stage = new(
        new(
            1, 1, .3162f, .0794f, .2818f, 3.45f, .3f, .82f, .8913f, .088f,
            .7943f, .063f, .25f, 0, .9943f, 4705, 99.6f, true),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 2f);

    // Large concert hall with a long, dense reflected tail.
    private static readonly ReverbProfile ConcreteHall = new(
        new(
            1, 1, .3162f, .5623f, 1, 3.92f, .7f, 1, .2427f, .020f,
            .9977f, .029f, .25f, 0, .9943f, 5000, 250, true),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 2f);

    // Vast empty enclosure with a dense, long, dark decay.
    private static readonly ReverbProfile Hangar = new(
        new(
            1, 1, .3162f, .3162f, 1, 10.05f, .23f, 1, .5f, .020f,
            1.256f, .030f, .25f, 0, .9943f, 5000, 250, true),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 2f);

    // Vast domed interior with delayed reflections and a long tail.
    private static readonly ReverbProfile Cathedral = new(
        new(
            1, .87f, .3162f, .3548f, .2239f, 10.48f, .19f, .1f, .1778f, .090f,
            1.2589f, .042f, .25f, 0, .9943f, 2854.3999f, 20, true),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 2f);

    // Large empty stadium with distant reflections and a sustained low decay.
    private static readonly ReverbProfile Stadium = new(
        new(
            1, 1, .3162f, .4467f, .7943f, 6.26f, .51f, 1.1f, .0631f, .183f,
            .3981f, .038f, .25f, 0, .9943f, 5000, 250, true),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 2f);

    // Compact stone chamber with dense, short reflections.
    private static readonly ReverbProfile Dungeon = new(
        new(
            1, .89f, .3162f, .3981f, .1f, 1.22f, .83f, .31f, .8913f, .022f,
            1.9953f, .011f, .25f, 0, .9943f, 5168.6001f, 139.5f, true),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 2f);

    // Enclosed passage with delayed reflections and a sustained low decay.
    private static readonly ReverbProfile Tunnel = new(
        new(
            1, .81f, .3162f, .3981f, .8913f, 3.42f, .94f, 1.31f, .7079f, .051f,
            .7079f, .047f, .25f, 0, .9943f, 5000, 155.3f, true),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 2f);

    // Reflective rocky cavern with a longer decay.
    private static readonly ReverbProfile Cave = new(
        new(
            1, 1, .3162f, 1, 1, 2.91f, 1.3f, 1, .5f, .015f,
            .7063f, .022f, .25f, 0, .9943f, 5000, 250, false),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 2f);

    // Hollow pipe with strong close reflections and a dark decay.
    private static readonly ReverbProfile SewerPipe = new(
        new(
            .3071f, .8f, .3162f, .3162f, 1, 2.81f, .14f, 1, 1.6387f, .014f,
            3.2471f, .021f, .25f, 0, .9943f, 5000, 250, true),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 2f);

    // Narrow outdoor passage with scattered, relatively bright reflections.
    private static readonly ReverbProfile Alley = new(
        new(
            1, .3f, .3162f, .7328f, 1, 1.49f, .86f, 1, .25f, .007f,
            .9954f, .011f, .25f, 0, .9943f, 5000, 250, true),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 2f);

    // Delayed, scattered outdoor reflections with strong high-frequency absorption.
    private static readonly ReverbProfile Forest = new(
        new(
            1, .3f, .3162f, .0224f, 1, 1.49f, .54f, 1, .0525f, .162f,
            .7682f, .088f, .25f, 0, .9943f, 5000, 250, true),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 2f);

    // Delayed outdoor reflections with a longer, fuller decay.
    private static readonly ReverbProfile Canyon = new(
        new(
            1, .74f, .3162f, .1778f, .631f, 3.89f, .21f, .46f, .3162f, .223f,
            .3548f, .019f, .25f, 0, .9943f, 4399.1001f, 242.9f, false),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 2f);

    // Indoor pool with bright, lightly modulated reflections above the water.
    private static readonly ReverbProfile SwimmingPool = new(
        new(
            1, .7f, .3162f, .7943f, .8913f, 2.76f, 1.25f, 1.14f, .631f, .020f,
            .7943f, .030f, .895f, .19f, .9943f, 5000, 250, false),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 2f);

    // Published modulated underwater reverb; direct coloration is configured below.
    private static readonly ReverbProfile UnderwaterReverb = new(
        new(
            .3645f, 1, .3162f, .01f, 1, 1.49f, .1f, 1, .5963f, .007f,
            7.0795f, .011f, 1.18f, .348f, .9943f, 5000, 250, true),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 1.5f);

    // Balanced, dense reverberation on the published generic profile.
    private static readonly ReverbProfile GenericReverb = new(
        new(
            1, 1, .3162f, .8913f, 1, 1.49f, .83f, 1, .05f, .007f,
            1.2589f, .011f, .25f, 0, .9943f, 5000, 250, true),
        QuietWindowSeconds: ReverbQuietWindowSeconds, ReturnGain: 4f);

    /// <summary>Resolves immutable reverb settings only when the active environment changes.</summary>
    internal static ReverbProfile? GetReverb(SpatialEnvironment environment) => environment switch
    {
        SpatialEnvironment.Room => Room,
        SpatialEnvironment.LivingRoom => LivingRoom,
        SpatialEnvironment.Bathroom => Bathroom,
        SpatialEnvironment.Stage => Stage,
        SpatialEnvironment.ConcreteHall => ConcreteHall,
        SpatialEnvironment.Hangar => Hangar,
        SpatialEnvironment.Cathedral => Cathedral,
        SpatialEnvironment.Stadium => Stadium,
        SpatialEnvironment.Dungeon => Dungeon,
        SpatialEnvironment.Tunnel => Tunnel,
        SpatialEnvironment.Cave => Cave,
        SpatialEnvironment.SewerPipe => SewerPipe,
        SpatialEnvironment.Alley => Alley,
        SpatialEnvironment.Forest => Forest,
        SpatialEnvironment.Canyon => Canyon,
        SpatialEnvironment.SwimmingPool => SwimmingPool,
        SpatialEnvironment.Underwater or SpatialEnvironment.DeepWater => UnderwaterReverb,
        SpatialEnvironment.Reverb => GenericReverb,
        _ => null
    };

    internal static class Underwater
    {
        // EFX direct-filter balance used by OpenMW's underwater environment.
        internal const float DirectGain = .9f;
        internal const float DirectGainHF = .125f;
        internal const float DirectFilterReferenceHz = 5000f;
    }

    internal static class Muffled
    {
        internal const float CutoffHz = 450f;
        internal const float MaximumGainDb = 1f;
    }

    internal static class InnerVoice
    {
        internal const float DecaySeconds = 3.5f;
        internal const float Damping = .85f;
        internal const float ReverbLowCutHz = 150f;
        internal const float LowPassHz = 1800f;
        internal const float FilterQ = .707f;
        internal const float PresenceGain = .15f;
        internal const float ReturnGain = .15f;
        // Intentional mono timbral coloration, not a binaural model.
        internal const float MicroDelaySeconds = .008f;
        internal const float DelayCapacityMs = 15f;
    }

    internal static class Echo
    {
        internal const float DelaySeconds = .3f;
        internal const float Feedback = .4f;
        internal const float DampingReferenceGain = .5f;
        internal const float DampingReferenceHz = 5000f;
        internal const float ReturnGain = .4f;
    }

    internal static class DualEcho
    {
        internal static readonly SpatialEchoParameters Parameters = new(.18f, .25f, .5f, .4f);
        internal const float ReturnGain = .3f;
    }
}
