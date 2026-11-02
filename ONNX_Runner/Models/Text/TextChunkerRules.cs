using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PunctuationRole = ONNX_Runner.Models.Text.TextChunkerDefaults.PunctuationRole;

namespace ONNX_Runner.Models.Text;

/// <summary>
/// Immutable shared catalog for sentence boundaries, abbreviations, and punctuation semantics.
/// Optional JSON adds entries to the built-in categories before requests are accepted.
/// </summary>
public sealed class TextChunkerRules
{
    public enum PunctuationKind : byte
    {
        Period, Question, Exclamation, Interrobang, DoubleQuestion,
        QuestionExclamation, ExclamationQuestion, Comma, Semicolon, Colon,
        Ellipsis, Dash, OpenBracket, CloseBracket, WordSeparator,
        OpeningQuestion, OpeningExclamation
    }

    private readonly record struct PunctuationEntry(
        PunctuationKind? Kind, PunctuationRole Roles);

    public static TextChunkerRules Default { get; } = new(new Extensions());

    public SearchValues<char> SentenceTerminators { get; }
    public SearchValues<char> SentenceBoundaryCandidates { get; }
    public SearchValues<char> PeriodLikeMarks { get; }
    public SearchValues<char> EllipsisMarks { get; }
    public SearchValues<char> ClausePunctuation { get; }
    public SearchValues<char> PauseMarks { get; }
    public SearchValues<char> ClosingPunctuation { get; }
    public SearchValues<char> OpeningPunctuation { get; }
    public SearchValues<char> LineBoundaries { get; }
    public SearchValues<char> SeparatedTerminals { get; }
    public SearchValues<char> ReportingDashes { get; }
    public SearchValues<char> LexicalHyphens { get; }
    public SearchValues<char> VisualQuotes { get; }
    public SearchValues<char> BoundaryFormatControls { get; }
    public SearchValues<char> TechnicalMarkers { get; }
    public SearchValues<char> TechnicalTokenMarkers { get; }
    public SearchValues<char> LexicalApostrophes { get; }
    public SearchValues<char> TechnicalSuffixMarks { get; }
    public FrozenDictionary<Rune, PunctuationKind> SemanticKinds { get; }
    public FrozenSet<string> CommonAbbreviations { get; }
    public FrozenSet<string> PrefixAbbreviations { get; }
    public FrozenSet<string> NameBindingAbbreviations { get; }
    public FrozenSet<string> NumberBindingAbbreviations { get; }
    public FrozenSet<string> IntroductoryAbbreviations { get; }
    public FrozenSet<string> KnownAbbreviations { get; }
    private readonly FrozenDictionary<PunctuationKind, ImmutableArray<Rune>> _semanticMarks;

    /// <summary>Returns the immutable marks assigned to one punctuation meaning.</summary>
    internal ImmutableArray<Rune> GetSemanticMarks(PunctuationKind kind)
    {
        return _semanticMarks[kind];
    }

    // Literal syntax is spoken by name only inside a protected technical span.
    public bool IsTechnicalSpeechSymbol(char value)
    {
        return !char.IsSurrogate(value) && (char.IsPunctuation(value) || char.IsSymbol(value));
    }

    // Unicode operators and currencies do not require an exhaustive glyph list.
    // Other symbols count as technical evidence only when attached to text or numbers.

    public bool IsTechnicalSymbolMarker(char value, bool hasText)
    {
        var category = char.GetUnicodeCategory(value);
        return category is System.Globalization.UnicodeCategory.MathSymbol
            or System.Globalization.UnicodeCategory.CurrencySymbol
            or System.Globalization.UnicodeCategory.ConnectorPunctuation ||
            (hasText && char.IsSymbol(value));
    }

    /// <summary>Merges validated extensions into the built-in catalog and prepares immutable lookup tables.</summary>
    private TextChunkerRules(Extensions additions)
    {
        var punctuation = new Dictionary<Rune, PunctuationEntry>();
        foreach (var group in TextChunkerDefaults.Punctuation)
        {
            foreach (Rune mark in group.Marks.EnumerateRunes())
            {
                punctuation.Add(mark, new PunctuationEntry(group.Kind, group.Roles));
            }
        }

        AddMarks(punctuation, additions.AdditionalSentenceTerminators, PunctuationRole.Sentence, PunctuationKind.Period);
        AddMarks(punctuation, additions.AdditionalPeriodLikeMarks, PunctuationRole.Sentence | PunctuationRole.PeriodLike, PunctuationKind.Period, strictKind: true);
        AddMarks(punctuation, additions.AdditionalEllipsisMarks, PunctuationRole.Sentence | PunctuationRole.Ellipsis, PunctuationKind.Ellipsis, strictKind: true);
        AddMarks(punctuation, additions.AdditionalQuestionMarks, PunctuationRole.Sentence, PunctuationKind.Question, strictKind: true);
        AddMarks(punctuation, additions.AdditionalExclamationMarks, PunctuationRole.Sentence, PunctuationKind.Exclamation, strictKind: true);
        AddMarks(punctuation, additions.AdditionalClausePunctuation, PunctuationRole.Clause | PunctuationRole.Pause, PunctuationKind.Semicolon);
        AddMarks(punctuation, additions.AdditionalPauseMarks, PunctuationRole.Pause, PunctuationKind.Comma);
        AddMarks(punctuation, additions.AdditionalClosingPunctuation, PunctuationRole.Closing, PunctuationKind.CloseBracket);

        AddTechnicalMarkers(punctuation, additions.AdditionalTechnicalTokenMarkers);

        foreach (var (mark, entry) in punctuation)
        {
            if ((entry.Roles & PunctuationRole.Sentence) != 0 &&
                ((entry.Roles & (PunctuationRole.Clause | PunctuationRole.Closing | PunctuationRole.ContextualQuestion)) != 0))
            {
                throw new ArgumentException($"Conflicting sentence and clause/closing roles for punctuation '{mark}'.");
            }
        }

        SentenceTerminators = SelectMarks(punctuation, PunctuationRole.Sentence);
        SentenceBoundaryCandidates = SelectMarks(punctuation, PunctuationRole.Sentence | PunctuationRole.ContextualQuestion);
        PeriodLikeMarks = SelectMarks(punctuation, PunctuationRole.PeriodLike);
        EllipsisMarks = SelectMarks(punctuation, PunctuationRole.Ellipsis);
        ClausePunctuation = SelectMarks(punctuation, PunctuationRole.Clause);
        PauseMarks = SelectMarks(punctuation, PunctuationRole.Pause);
        ClosingPunctuation = SelectMarks(punctuation, PunctuationRole.Closing);
        OpeningPunctuation = SelectMarks(punctuation, PunctuationRole.Opening);
        LineBoundaries = SelectMarks(punctuation, PunctuationRole.Line);
        SeparatedTerminals = SelectMarks(punctuation, PunctuationRole.SeparatedTerminal);
        ReportingDashes = SelectMarks(punctuation, PunctuationRole.ReportingDash);
        LexicalHyphens = SelectMarks(punctuation, PunctuationRole.LexicalHyphen);
        VisualQuotes = SelectMarks(punctuation, PunctuationRole.VisualQuote);
        BoundaryFormatControls = SelectMarks(punctuation, PunctuationRole.FormatControl);
        TechnicalMarkers = SelectMarks(punctuation, PunctuationRole.TechnicalMarker);
        TechnicalTokenMarkers = SelectMarks(punctuation, PunctuationRole.TechnicalToken);
        LexicalApostrophes = SelectMarks(punctuation, PunctuationRole.LexicalApostrophe);
        TechnicalSuffixMarks = SelectMarks(punctuation, PunctuationRole.TechnicalSuffix);
        SemanticKinds = punctuation.Where(pair => pair.Value.Kind.HasValue)
            .ToFrozenDictionary(pair => pair.Key, pair => pair.Value.Kind!.Value);
        _semanticMarks = punctuation.Where(pair => pair.Value.Kind.HasValue)
            .GroupBy(pair => pair.Value.Kind!.Value)
            .ToFrozenDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToImmutableArray());

        PrefixAbbreviations = MergeAbbreviations(TextChunkerDefaults.PrefixAbbreviations, additions.AdditionalPrefixAbbreviations);
        NameBindingAbbreviations = MergeAbbreviations(TextChunkerDefaults.NameBindingAbbreviations, additions.AdditionalNameBindingAbbreviations);
        NumberBindingAbbreviations = MergeAbbreviations(TextChunkerDefaults.NumberBindingAbbreviations, additions.AdditionalNumberBindingAbbreviations);
        IntroductoryAbbreviations = MergeAbbreviations(TextChunkerDefaults.IntroductoryAbbreviations, additions.AdditionalIntroductoryAbbreviations);

        // Keep the established phonemizer table; specialized sentence hints do not silently
        // change built-in pronunciation. Explicit user additions are shared by every consumer.
        CommonAbbreviations = MergeAbbreviations(TextChunkerDefaults.CommonAbbreviations,
            (additions.AdditionalAbbreviations ?? []).Concat(additions.AdditionalPrefixAbbreviations ?? [])
            .Concat(additions.AdditionalNameBindingAbbreviations ?? [])
            .Concat(additions.AdditionalNumberBindingAbbreviations ?? [])
            .Concat(additions.AdditionalIntroductoryAbbreviations ?? []));
        KnownAbbreviations = CommonAbbreviations.Concat(PrefixAbbreviations)
            .Concat(NameBindingAbbreviations).Concat(NumberBindingAbbreviations)
            .Concat(IntroductoryAbbreviations).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Missing JSON selects the complete built-in catalog without creating a file.</summary>
    public static TextChunkerRules LoadOrDefault(string path)
    {
        if (!File.Exists(path)) return Default;

        try
        {
            using var stream = File.OpenRead(path);
            var additions = JsonSerializer.Deserialize<Extensions>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new InvalidDataException($"TextChunker rules file is empty: {path}");
            return new TextChunkerRules(additions);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Invalid TextChunker rules file: {path}", error);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException($"Invalid TextChunker rules entries: {path}. {error.Message}", error);
        }
    }

    /// <summary>Builds a BMP character lookup for entries matching any requested punctuation role.</summary>
    private static SearchValues<char> SelectMarks(
        Dictionary<Rune, PunctuationEntry> punctuation, PunctuationRole roles)
    {
        return SearchValues.Create(punctuation
            .Where(pair => pair.Key.IsBmp && (pair.Value.Roles & roles) != 0)
            .Select(pair => (char)pair.Key.Value).ToArray());
    }

    /// <summary>Validates additional punctuation and merges roles, rejecting incompatible meanings when required.</summary>
    private static void AddMarks(Dictionary<Rune, PunctuationEntry> punctuation,
        IEnumerable<string>? additions, PunctuationRole roles, PunctuationKind kind, bool strictKind = false)
    {
        if (additions is null) return;

        foreach (string? value in additions)
        {
            if (value is null || value.Length != 1 || char.IsSurrogate(value[0]) ||
                !(char.IsPunctuation(value[0]) || DefaultLineBoundary(value[0])))
            {
                throw new ArgumentException($"Punctuation entries must be individual BMP punctuation marks or line boundaries: '{value}'.");
            }

            var mark = new Rune(value[0]);
            punctuation.TryGetValue(mark, out var existing);
            if (strictKind && existing.Kind.HasValue && existing.Kind.Value != kind)
            {
                throw new ArgumentException($"Punctuation '{mark}' already belongs to {existing.Kind}; it cannot also belong to {kind}.");
            }

            punctuation[mark] = new PunctuationEntry(existing.Kind ?? kind, existing.Roles | roles);
        }
    }

    /// <summary>Adds validated BMP punctuation or symbol characters as technical token markers.</summary>
    private static void AddTechnicalMarkers(Dictionary<Rune, PunctuationEntry> punctuation,
        IEnumerable<string>? additions)
    {
        if (additions is null) return;
        foreach (string? value in additions)
        {
            if (value is null || value.Length != 1 || char.IsSurrogate(value[0]) ||
                !(char.IsPunctuation(value[0]) || char.IsSymbol(value[0])))
                throw new ArgumentException($"Technical markers must be individual BMP punctuation or symbol characters: '{value}'.");
            var mark = new Rune(value[0]);
            punctuation.TryGetValue(mark, out var existing);
            punctuation[mark] = new PunctuationEntry(existing.Kind, existing.Roles | PunctuationRole.TechnicalToken);
        }
    }

    /// <summary>Checks whether the built-in catalog treats a character as a line boundary.</summary>
    private static bool DefaultLineBoundary(char mark)
    {
        return TextChunkerDefaults.Punctuation.Any(group =>
            (group.Roles & PunctuationRole.Line) != 0 && group.Marks.Contains(mark));
    }

    /// <summary>Normalizes and validates abbreviation additions, then freezes the case-insensitive union.</summary>
    private FrozenSet<string> MergeAbbreviations(IEnumerable<string> builtIn, IEnumerable<string>? additions)
    {
        var merged = new HashSet<string>(builtIn, StringComparer.OrdinalIgnoreCase);
        if (additions is null) return merged.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        foreach (string? value in additions)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            ReadOnlySpan<char> normalized = value.AsSpan().Trim();
            while (!normalized.IsEmpty && PeriodLikeMarks.Contains(normalized[^1]))
            {
                normalized = normalized[..^1];
            }

            if (normalized.IsEmpty || normalized.Length > 128)
            {
                throw new ArgumentException($"Invalid abbreviation: '{value}'.");
            }

            string item = normalized.ToString();
            if (item.Any(char.IsWhiteSpace)) throw new ArgumentException($"Invalid abbreviation: '{value}'.");
            merged.Add(item);
        }

        return merged.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    // This DTO describes additions only; the runtime catalog is immutable.
    private sealed class Extensions
    {
        public List<string>? AdditionalSentenceTerminators { get; set; }
        public List<string>? AdditionalPeriodLikeMarks { get; set; }
        public List<string>? AdditionalEllipsisMarks { get; set; }
        public List<string>? AdditionalQuestionMarks { get; set; }
        public List<string>? AdditionalExclamationMarks { get; set; }
        public List<string>? AdditionalClausePunctuation { get; set; }
        public List<string>? AdditionalPauseMarks { get; set; }
        public List<string>? AdditionalClosingPunctuation { get; set; }
        public List<string>? AdditionalTechnicalTokenMarkers { get; set; }
        public List<string>? AdditionalAbbreviations { get; set; }
        public List<string>? AdditionalPrefixAbbreviations { get; set; }
        public List<string>? AdditionalNameBindingAbbreviations { get; set; }
        public List<string>? AdditionalNumberBindingAbbreviations { get; set; }
        public List<string>? AdditionalIntroductoryAbbreviations { get; set; }
    }
}
