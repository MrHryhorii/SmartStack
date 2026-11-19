namespace ONNX_Runner.Services.Text.Phonemization.Adaptation;

/// <summary>Visible IPA and optional prepared encodings for adapted phonetic groups.</summary>
public readonly struct PhonemeSequence
{
    private readonly string? _text;
    private readonly AdaptedSpan[]? _adaptations;

    public string Text => _text ?? string.Empty;
    internal ReadOnlySpan<AdaptedSpan> Adaptations => _adaptations;

    public PhonemeSequence(string text) : this(text, null) { }

    internal PhonemeSequence(string text, AdaptedSpan[]? adaptations)
    {
        _text = text;
        _adaptations = adaptations;
    }
}

// Negative group IDs force joining prepared pieces of an unlisted structural combination.
internal readonly record struct AdaptedSpan(int Start, int Group, PhonemePlan Plan)
{
    internal int Length => Plan.Text.Length;
    internal bool Joined => Group < 0 || Plan.Joined;
}
internal readonly record struct NativePhoneme(string Text, int[] Ids);

/// <summary>Immutable model-specific encoding prepared before requests are accepted.</summary>
internal sealed class PhonemePlan
{
    internal string Text { get; }
    internal int[] Ids { get; }
    internal int[] JoinedIds { get; }
    internal int[] LastTokenIds { get; }
    internal bool Joined { get; }

    internal PhonemePlan(NativePhoneme[] tokens, int[] padding, bool joined)
    {
        Text = string.Concat(tokens.Select(token => token.Text));
        Joined = joined;
        JoinedIds = tokens.SelectMany(token => token.Ids).ToArray();
        LastTokenIds = tokens[^1].Ids;
        Ids = joined || tokens.Length < 2 ? JoinedIds : tokens
            .SelectMany((token, index) => index == 0 ? token.Ids : padding.Concat(token.Ids)).ToArray();
    }
}
