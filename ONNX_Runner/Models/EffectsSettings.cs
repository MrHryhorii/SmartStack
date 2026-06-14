/// <summary>
/// Available server-side audio effects for post-processing spatial audio generation.
/// </summary>
public enum SpatialEnvironment
{
    // --- Default & Everyday ---

    /// <summary>No reverb; dry signal only. Highest performance mode.</summary>
    None,

    /// <summary>Small, intimate room. Short decay and balanced frequency response; natural for dialogue.</summary>
    LivingRoom,

    // --- Public Spaces ---

    /// <summary>Performance stage. Distinct pre-delay simulates stage-to-audience projection; clean but spacious.</summary>
    Stage,

    /// <summary>Large reflective hall. Long, dense reverb tail with strong early reflections; adds scale.</summary>
    ConcreteHall,

    // --- Adventure & Nature ---

    /// <summary>Tight stone space. Short, dark reverb characterized by distinct flutter echoes between parallel walls.</summary>
    Dungeon,

    /// <summary>Large enclosed stone space. High reflectivity with very long, dark decay; emphasizes deep resonance.</summary>
    Cave,

    /// <summary>Open outdoor space. Discrete echoes rather than dense reverb tails; simulates natural canyon or forest acoustics.</summary>
    Forest,

    // --- Extreme & Narrative ---

    /// <summary>Muffled underwater space. Significant high-frequency roll-off and characteristic "slapback" echo.</summary>
    Underwater,

    /// <summary>Intracranial acoustic space. Short tap delays simulate bone-conducted sound; used for internal thoughts.</summary>
    InnerVoice
}