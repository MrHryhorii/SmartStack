namespace ONNX_Runner.Models.Audio;

/// <summary>
/// Available server-side audio effects for post-processing spatial audio generation.
/// </summary>
public enum SpatialEnvironment
{
    // --- Default & Everyday ---

    /// <summary>No reverb; dry signal only. Highest performance mode.</summary>
    None,

    /// <summary>Small room with short, clear reflections and less high-frequency absorption.</summary>
    Room,

    /// <summary>Furnished room with short, soft reflections and strong high-frequency absorption.</summary>
    LivingRoom,

    /// <summary>Small tiled room with strong, close reflections.</summary>
    Bathroom,

    // --- Public Spaces ---

    /// <summary>Wooden performance hall with warm, delayed reflections.</summary>
    Stage,

    /// <summary>Large concert hall with a long, dense reflected tail.</summary>
    ConcreteHall,

    /// <summary>Vast empty enclosure with dense reflections and a long, dark decay.</summary>
    Hangar,

    /// <summary>Vast domed interior with delayed reflections and a long reverberant tail.</summary>
    Cathedral,

    /// <summary>Large empty stadium with distant reflections and a sustained low-frequency decay.</summary>
    Stadium,

    // --- Adventure & Nature ---

    /// <summary>Compact stone chamber with dense, short reflections.</summary>
    Dungeon,

    /// <summary>Enclosed tunnel with delayed reflections and a sustained low-frequency decay.</summary>
    Tunnel,

    /// <summary>Reflective rocky cavern with a longer decay.</summary>
    Cave,

    /// <summary>Hollow sewer pipe with strong close reflections and rapid high-frequency decay.</summary>
    SewerPipe,

    /// <summary>Narrow outdoor passage with scattered, relatively bright reflections.</summary>
    Alley,

    /// <summary>Scattered forest reflections with delayed returns and strong high-frequency absorption.</summary>
    Forest,

    /// <summary>Delayed canyon reflections with a longer, fuller reverberant decay.</summary>
    Canyon,

    // --- Water ---

    /// <summary>Indoor swimming pool with bright, lightly modulated reflections above the water.</summary>
    SwimmingPool,

    /// <summary>Muffled direct speech with modulated underwater reflections.</summary>
    Underwater,

    /// <summary>Underwater coloration followed by gradual low-pass filtering of the voice and reflections.</summary>
    DeepWater,

    // --- Special & Narrative ---

    /// <summary>Occlusion effect. Pure low-pass filter with no spatial reflections. Simulates hearing sound through walls, earplugs, or UI pause menus.</summary>
    Muffled,

    /// <summary>Cinematic inner monologue with close, dark voice coloration and a restrained diffuse tail.</summary>
    InnerVoice,

    // --- Classic Effects ---

    /// <summary>Balanced, dense reverberation without a specific room character.</summary>
    Reverb,

    /// <summary>One fixed delay with progressively quieter and darker repeats.</summary>
    Echo,

    /// <summary>Two fixed delay taps with a repeating, damped echo pattern.</summary>
    DualEcho
}
