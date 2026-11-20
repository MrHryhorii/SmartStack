using System.Text;
using ONNX_Runner.Models.Inference;

namespace ONNX_Runner.Services.Text.Phonemization.Adaptation;

/// <summary>Resolves native partitions, aliases and structural cascades once per loaded model.</summary>
internal sealed class PhonemePlanCompiler
{
    private sealed record Solution(NativePhoneme[] Tokens, int Cost, bool Joined)
    {
        internal string Text => string.Concat(Tokens.Select(token => token.Text));
    }

    private readonly Dictionary<string, NativePhoneme> _native;
    private readonly Dictionary<string, Solution?> _resolved = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);
    private readonly PhoibleCatalog _catalog;
    private readonly (NativePhoneme Token, PhonemeProfile Profile)[] _candidates;
    private readonly int[] _nativeLengths;
    private readonly int[] _padding;
    private readonly PhonemeInventory _inventory;

    internal PhonemeInventory Inventory => _inventory;

    internal Dictionary<string, PhonemePlan> Plans { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, PhonemePlan> NativePlans { get; } = new(StringComparer.Ordinal);
    internal Dictionary<(IpaModifier Modifier, bool Voiced), PhonemePlan?> Modifiers { get; } = [];
    internal HashSet<char> VoicedBases { get; } = [];

    internal PhonemePlanCompiler(PiperConfig config, PhoibleCatalog catalog)
    {
        _catalog = catalog;
        _inventory = new(config.PhonemeIdMap);
        _native = _inventory.Tokens
            .ToDictionary(pair => pair.Key, pair => new NativePhoneme(pair.Key, pair.Value), StringComparer.Ordinal);
        _nativeLengths = _native.Keys.Select(key => key.Length).Distinct().OrderDescending().ToArray();
        _padding = _native.TryGetValue("_", out var pad) ? pad.Ids : [0];
        _candidates = _native.Values.OrderBy(token => token.Text, StringComparer.Ordinal)
            .Select(token => (Token: token, Profile: catalog.TryGet(token.Text, out var profile) ? profile : null))
            .Where(pair => pair.Profile != null).Select(pair => (pair.Token, pair.Profile!)).ToArray();
        foreach (var token in _native.Values) NativePlans[token.Text] = new([token], _padding, false);

        var sources = new HashSet<string>(catalog.Profiles.Keys, StringComparer.Ordinal);
        sources.UnionWith(_native.Keys);
        foreach (var (symbol, expansion) in PhonemeDecompositionCatalog.Symbols)
        {
            sources.Add(symbol.ToString());
            sources.Add(expansion);
        }
        // Modifier meanings are shared data; combinations are resolved against this inventory.
        char[] modifiers = ['\u0303', 'ʲ', 'ʷ', 'ˠ', 'ˤ', 'ʰ', 'ʱ', 'ː', 'ˑ', '\u0329', '\u032F', 'ⁿ', 'ᵐ', 'ᵑ', 'ˡ', '˞', '\u032C', '\u0325'];
        var bases = sources.SelectMany(source => source.EnumerateRunes()).Where(rune => rune.IsBmp &&
            char.IsLetter((char)rune.Value) && IpaReader.Modifier((char)rune.Value) == IpaModifier.None &&
            !IpaReader.IsBoundary((char)rune.Value)).Select(rune => (char)rune.Value).ToHashSet();
        foreach (char value in bases)
        {
            foreach (char modifier in modifiers) sources.Add(string.Concat(value, modifier));
        }
        foreach (string source in sources.ToArray())
        {
            string composed = source.Normalize(NormalizationForm.FormC);
            sources.Add(composed);
            if (composed.Length == 1 && char.IsLetter(composed[0]) &&
                IpaReader.Modifier(composed[0]) == IpaModifier.None && !IpaReader.IsBoundary(composed[0]))
                bases.Add(composed[0]);
            sources.Add(source.Normalize(NormalizationForm.FormD));
            if (source.Contains('ɡ')) sources.Add(source.Replace('ɡ', 'g'));
            if (!IpaReader.HasBase(source)) continue;
            int first = PhonemeInventory.ScalarLength(source);
            if (first < source.Length && IpaReader.Modifier(source[first]) == IpaModifier.None &&
                !IpaReader.IsBoundary(source[first]))
            {
                sources.Add(source.Insert(first, "\u0361"));
                sources.Add(source.Insert(first, "\u035C"));
            }
        }
        // Unlisted modifier combinations must classify composed and decomposed bases identically.
        foreach (char value in bases)
            if (catalog.IsVoiced(value.ToString())) VoicedBases.Add(value);
        foreach (string source in sources.OrderBy(source => source, StringComparer.Ordinal))
        {
            Solution? solution = Resolve(source);
            if (solution == null || (solution.Text == source && _inventory.CanEncode(source))) continue;
            Plans[source] = ToPlan(solution);
        }
        foreach (IpaModifier modifier in Enum.GetValues<IpaModifier>())
        foreach (bool voiced in new[] { false, true })
        {
            Solution? solution = ModifierSolution(modifier, voiced);
            Modifiers[(modifier, voiced)] = solution == null ? null : ToPlan(solution);
        }
    }

    private Solution? Resolve(string source)
    {
        if (_resolved.TryGetValue(source, out var cached)) return cached;
        if (source.Length == 0 || !_resolving.Add(source)) return null;
        Solution? result = ResolveCore(source);
        _resolving.Remove(source);
        _resolved[source] = result;
        return result;
    }

    private Solution? ResolveCore(string source)
    {
        Solution? native = Native(source);
        if (native != null) return native;
        if (!IpaReader.HasBase(source) && (!_catalog.TryGet(source, out var description) || description.Kind != PhonemeKind.Tone)) return null;
        Solution? best = null;

        string normalized = source.Normalize(NormalizationForm.FormD);
        if (normalized != source) Consider(ref best, Changed(Resolve(normalized), 1));
        string canonical = IpaReader.Canonical(source);
        if (canonical != source)
            Consider(ref best, Changed(Resolve(canonical), source.Any(IpaReader.IsTie) ? 0 : 1, source.Any(IpaReader.IsTie)));
        if (source.Contains('ɡ')) Consider(ref best, Changed(Native(source.Replace('ɡ', 'g')), 1));
        if (source.Length == 1 && PhonemeDecompositionCatalog.Symbols.TryGetValue(source[0], out string? expansion))
            Consider(ref best, Changed(Resolve(expansion), 1, true));

        int last = source.Length - (char.IsLowSurrogate(source[^1]) && source.Length > 1 ? 2 : 1);
        IpaModifier modifier = IpaReader.Modifier(source[last]);
        if (last > 0 && modifier != IpaModifier.None)
        {
            string prefix = source[..last];
            Solution? part = Resolve(prefix);
            if (part != null)
            {
                bool isLength = modifier is IpaModifier.Long or IpaModifier.HalfLong;
                int firstModifier = FirstModifier(prefix);
                if (isLength && part.Joined && firstModifier > 0)
                {
                    // A duration marker belongs to the carrier, before an added glide or nasal release.
                    string reordered = prefix[..firstModifier] + source[last..] + prefix[firstModifier..];
                    Consider(ref best, Resolve(reordered));
                }
                else if (modifier is IpaModifier.Voiced or IpaModifier.Voiceless or IpaModifier.Feature)
                {
                    Solution? feature = FeatureSolution(source, prefix, modifier);
                    Consider(ref best, feature ?? Changed(part, 32, true));
                }
                else
                {
                    Solution? mark = Native(source[last..]);
                    if (mark != null) Consider(ref best, Join(part, mark, 0));
                    else if (isLength) Consider(ref best, Join(part, part, 3));
                    else
                    {
                        Solution? replacement = ModifierSolution(modifier, BaseVoiced(prefix));
                        Consider(ref best, replacement == null ? Changed(part, 24, true) :
                            modifier == IpaModifier.Syllabic ? Join(replacement, part, 2) : Join(part, replacement, 2));
                    }
                }
            }
        }

        IpaModifier leading = IpaReader.Modifier(source[0]);
        if (leading != IpaModifier.None && source.Length > 1)
        {
            Solution? rest = Resolve(source[1..]);
            Solution? mark = Native(source[..1]) ?? ModifierSolution(leading, BaseVoiced(source[1..]));
            if (rest != null) Consider(ref best, mark == null ? Changed(rest, 24, true) : Join(mark, rest, 2));
        }

        // Prefer longer prefixes on ties; a better suffix can still win on cost.
        for (int split = source.Length - 1; split > 0; split--)
        {
            if (!IpaReader.CanSplit(source, split)) continue;
            Solution? left = Resolve(source[..split]), right = Resolve(source[split..]);
            if (left != null && right != null) Consider(ref best, Join(left, right, 0));
        }
        if (best != null) return best;

        // Feature search is the leaf fallback, never a shortcut around a structural solution.
        return _catalog.TryGet(source, out var profile) ? Closest(profile) : null;
    }

    private Solution? Native(string source)
    {
        var lookup = _native.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(source.AsSpan(), out var exact)) return new([exact], 0, false);
        var paths = new NativePhoneme[]?[source.Length + 1];
        paths[source.Length] = [];
        for (int position = source.Length - 1; position >= 0; position--)
        {
            if (char.IsLowSurrogate(source[position])) continue;
            foreach (int length in _nativeLengths)
            {
                if (length > source.Length - position || paths[position + length] == null ||
                    !lookup.TryGetValue(source.AsSpan(position, length), out var token)) continue;
                var suffix = paths[position + length]!;
                if (paths[position] != null && paths[position]!.Length <= suffix.Length + 1) continue;
                paths[position] = [token, .. suffix];
            }
        }
        return paths[0] is { } tokens ? new(tokens, 0, false) : null;
    }

    private Solution? ModifierSolution(IpaModifier modifier, bool voiced)
    {
        string[] candidates = modifier switch
        {
            IpaModifier.Nasal or IpaModifier.NasalRelease => PhonemeDecompositionCatalog.Nasals,
            IpaModifier.LabialNasalRelease => PhonemeDecompositionCatalog.LabialNasals,
            IpaModifier.VelarNasalRelease => PhonemeDecompositionCatalog.VelarNasals,
            IpaModifier.Palatal => PhonemeDecompositionCatalog.Palatals,
            IpaModifier.Labial => PhonemeDecompositionCatalog.Labials,
            IpaModifier.Velar => PhonemeDecompositionCatalog.Velars,
            IpaModifier.Pharyngeal => PhonemeDecompositionCatalog.Pharyngeals,
            IpaModifier.Aspirated => voiced ? PhonemeDecompositionCatalog.VoicedAspiration : PhonemeDecompositionCatalog.VoicelessAspiration,
            IpaModifier.Breathy => PhonemeDecompositionCatalog.VoicedAspiration,
            IpaModifier.LateralRelease => PhonemeDecompositionCatalog.Laterals,
            IpaModifier.Rhotic => PhonemeDecompositionCatalog.Rhotics,
            IpaModifier.Syllabic => PhonemeDecompositionCatalog.Schwas,
            _ => []
        };
        foreach (string candidate in candidates)
            if (_native.TryGetValue(candidate, out var token)) return new([token], 0, false);
        // An unavailable secondary articulation may be omitted, but must not become an unrelated sound.
        return null;
    }

    private Solution? FeatureSolution(string source, string prefix, IpaModifier modifier)
    {
        if (_catalog.TryGet(source, out var exact)) return Closest(exact);
        if (!_catalog.TryGet(prefix, out var profile)) return null;
        return modifier is IpaModifier.Voiced or IpaModifier.Voiceless
            ? Closest(_catalog.WithVoicing(profile, modifier == IpaModifier.Voiced)) : null;
    }

    private Solution? Closest(PhonemeProfile source)
    {
        int distance = int.MaxValue;
        NativePhoneme? chosen = null;
        foreach (var (token, profile) in _candidates)
        {
            int score = _catalog.Distance(source, profile, distance);
            if (score >= distance) continue;
            chosen = token;
            distance = score;
            if (score == 0) break;
        }
        return chosen is { } target ? new([target], 8 + distance, false) : null;
    }

    private bool BaseVoiced(string source)
    {
        for (int index = source.Length - 1; index >= 0; index--)
            if (char.IsLetter(source[index]) && IpaReader.Modifier(source[index]) == IpaModifier.None)
                return _catalog.IsVoiced(source[index].ToString());
        return false;
    }

    private static int FirstModifier(string source)
    {
        for (int index = 1; index < source.Length; index++)
            if (IpaReader.Modifier(source[index]) != IpaModifier.None) return index;
        return -1;
    }

    private PhonemePlan ToPlan(Solution solution) => new(solution.Tokens, _padding, solution.Joined);
    private static Solution? Changed(Solution? source, int cost, bool joined = false) =>
        source == null ? null : source with { Cost = source.Cost + cost, Joined = source.Joined || joined };
    private static Solution Join(Solution left, Solution right, int cost) =>
        new([.. left.Tokens, .. right.Tokens], left.Cost + right.Cost + cost, true);
    private static void Consider(ref Solution? best, Solution? candidate)
    {
        if (candidate == null) return;
        if (best == null || candidate.Cost < best.Cost ||
            (candidate.Cost == best.Cost && candidate.Tokens.Length < best.Tokens.Length)) best = candidate;
    }
}
