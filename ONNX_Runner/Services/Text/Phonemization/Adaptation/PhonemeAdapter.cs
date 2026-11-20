using ONNX_Runner.Models.Inference;

namespace ONNX_Runner.Services.Text.Phonemization.Adaptation;

/// <summary>Immutable prepared model adaptation; requests never search PHOIBLE feature vectors.</summary>
public sealed class PhonemeAdapter
{
    private readonly Dictionary<string, PhonemePlan> _plans;
    private readonly Dictionary<string, PhonemePlan> _native;
    private readonly Dictionary<(IpaModifier Modifier, bool Voiced), PhonemePlan?> _modifiers;
    private readonly HashSet<char> _voiced;
    private readonly Dictionary<int, KeyValuePair<string, PhonemePlan>[]> _starts;
    private readonly PhonemeInventory _inventory;

    internal int MaxSourceLength { get; }

    internal PhonemeAdapter(string path, PiperConfig config)
    {
        var compiler = new PhonemePlanCompiler(config, new PhoibleCatalog(path));
        _plans = compiler.Plans;
        _native = compiler.NativePlans;
        _modifiers = compiler.Modifiers;
        _voiced = compiler.VoicedBases;
        _inventory = compiler.Inventory;
        _starts = _plans.Where(pair => pair.Key.Length > 1 && !pair.Key.Any(char.IsWhiteSpace))
            .GroupBy(pair => Prefix(pair.Key.AsSpan())).ToDictionary(group => group.Key,
                group => group.OrderByDescending(pair => pair.Key.Length).ThenBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
        MaxSourceLength = _plans.Count == 0 ? 0 : _plans.Keys.Max(source => source.Length);
        Console.WriteLine($"[INFO] Prepared {_plans.Count} phoneme adaptation plans for the loaded model.");
    }

    public PhonemeSequence Adapt(string phonemes)
    {
        var output = new PhonemeOutput(phonemes.Length, true);
        try
        {
            Append(phonemes.AsSpan(), ref output);
            return output.Build();
        }
        finally { output.Dispose(); }
    }

    internal bool TryGetPlan(ReadOnlySpan<char> source, out PhonemePlan plan) =>
        _plans.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(source, out plan!);

    internal bool TryGetKnown(ReadOnlySpan<char> source, out PhonemePlan plan) =>
        TryGetPlan(source, out plan!) || _native.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(source, out plan!);

    // Shared with raw-block chunking so a prepared source cannot be reinterpreted after a cut.
    internal int ReadLength(ReadOnlySpan<char> input) => Read(input, out _, out _);

    private int Read(ReadOnlySpan<char> input, out PhonemePlan? plan, out bool native)
    {
        int unit = IpaReader.ReadLength(input);
        int length = unit;
        plan = null;
        native = _inventory.TryMatch(input, out int nativeLength, out _);
        if (nativeLength > length) length = nativeLength;
        if (input.Length > 1 && _starts.TryGetValue(Prefix(input), out var candidates))
        {
            foreach (var candidate in candidates)
            {
                if (!input.StartsWith(candidate.Key, StringComparison.Ordinal)) continue;
                if (candidate.Key.Length >= length) { length = candidate.Key.Length; plan = candidate.Value; }
                break;
            }
        }
        if (length == 1 && TryGetPlan(input[..1], out var single)) plan = single;
        if (!IpaReader.IsBoundary(input[0]))
            while (length < input.Length && IpaReader.Modifier(input[length]) != IpaModifier.None)
                length += PhonemeInventory.ScalarLength(input[length..]);
        if (plan != null && !TryGetPlan(input[..length], out plan)) plan = null;
        native = native && nativeLength == length && plan == null;
        return length;
    }

    internal void Append(ReadOnlySpan<char> input, ref PhonemeOutput output)
    {
        while (!input.IsEmpty)
        {
            int length = Read(input, out var plan, out bool native);
            ReadOnlySpan<char> unit = input[..length];
            if (plan != null) output.Append(plan);
            else if (native || _inventory.CanEncode(unit)) output.Append(unit);
            else AppendUnprepared(unit, ref output);
            input = input[length..];
        }
    }

    // Unlisted combinations reuse prepared pieces in one bounded forward scan. No feature
    // searches, normalization strings, mutable request caches or recursive expansion are needed.
    private void AppendUnprepared(ReadOnlySpan<char> source, ref PhonemeOutput output)
    {
        if (!IpaReader.HasBase(source)) { output.Append(source); return; }
        int group = output.NewGroup();
        bool voiced = false, lengthened = false;
        bool repeatCarrier = (source.Contains('ː') && !_native.ContainsKey("ː")) ||
            (source.Contains('ˑ') && !_native.ContainsKey("ˑ"));
        if (source.IndexOf('\u0329') >= 0 && !_native.ContainsKey("\u0329") &&
            _modifiers.TryGetValue((IpaModifier.Syllabic, false), out var schwa) && schwa != null)
            output.Append(schwa, group, true);

        for (int index = 0; index < source.Length;)
        {
            ReadOnlySpan<char> remaining = source[index..];
            int width = PhonemeInventory.ScalarLength(remaining);
            char symbol = remaining[0];
            IpaModifier modifier = IpaReader.Modifier(symbol);
            if (IpaReader.IsTie(symbol)) { index += width; continue; }
            PhonemePlan? part = null;
            bool nativePart = false;
            int consumed = width;
            if (remaining.Length > 1 && _starts.TryGetValue(Prefix(remaining), out var candidates))
                foreach (var candidate in candidates)
                    if (remaining.StartsWith(candidate.Key, StringComparison.Ordinal))
                    { part = candidate.Value; consumed = candidate.Key.Length; break; }
            if (_inventory.TryMatch(remaining, out int nativeLength, out _) && nativeLength >= consumed &&
                _native.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(remaining[..nativeLength], out var token))
            { part = token; consumed = nativeLength; nativePart = true; }
            if (part == null && TryGetPlan(remaining[..width], out var simple)) part = simple;

            if (part != null)
            {
                if (modifier == IpaModifier.None)
                {
                    lengthened |= remaining[..consumed].IndexOfAny('ː', 'ˑ') >= 0;
                    if (repeatCarrier && !lengthened)
                    {
                        PhonemePlan carrier = part;
                        int baseLength = width;
                        while (baseLength < consumed && IpaReader.Modifier(remaining[baseLength]) == IpaModifier.None)
                            baseLength += PhonemeInventory.ScalarLength(remaining[baseLength..]);
                        if (!nativePart && TryGetKnown(remaining[..baseLength], out var simpleCarrier)) carrier = simpleCarrier;
                        output.Append(carrier, group, true);
                        lengthened = true;
                    }
                    foreach (char value in remaining[..consumed])
                    {
                        IpaModifier feature = IpaReader.Modifier(value);
                        if (feature == IpaModifier.None && char.IsLetter(value)) voiced = _voiced.Contains(value);
                        if (feature == IpaModifier.Voiced) voiced = true;
                        if (feature == IpaModifier.Voiceless) voiced = false;
                    }
                }
                output.Append(part, group, true);
            }
            else if (modifier == IpaModifier.None) output.Append(remaining[..width]);
            else if (modifier is not (IpaModifier.Long or IpaModifier.HalfLong or IpaModifier.Syllabic))
            {
                if (_modifiers.TryGetValue((modifier, voiced), out var replacement) && replacement != null)
                    output.Append(replacement, group, true);
            }
            index += consumed;
        }
    }

    private static int Prefix(ReadOnlySpan<char> source) => (source[0] << 16) | source[1];
}
