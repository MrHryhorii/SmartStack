using System.Buffers;
using System.Text;

namespace ONNX_Runner.Services.Text.Phonemization;

/// <summary>Matches the active model's complete tokens and ID sequences without per-lookup allocations.</summary>
internal sealed class PhonemeInventory
{
    private readonly Dictionary<string, int[]> _map;
    private readonly int[] _lengths;

    internal IEnumerable<KeyValuePair<string, int[]>> Tokens => _map;

    internal PhonemeInventory(Dictionary<string, int[]> map)
    {
        // Prepared readers own their IDs; later configuration edits cannot change a live inventory.
        _map = map.Where(pair => pair.Key.Length > 0 && pair.Value is { Length: > 0 })
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        _lengths = _map.Keys.Select(key => key.Length).Distinct().OrderDescending().ToArray();
    }

    internal bool TryMatch(ReadOnlySpan<char> input, out int length, out int[] ids)
    {
        var lookup = _map.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (int candidate in _lengths)
        {
            if (candidate > input.Length || (candidate < input.Length &&
                char.IsHighSurrogate(input[candidate - 1]) && char.IsLowSurrogate(input[candidate]))) continue;
            if (!lookup.TryGetValue(input[..candidate], out var matched) || matched is not { Length: > 0 }) continue;
            length = candidate;
            ids = matched;
            return true;
        }
        length = 0;
        ids = [];
        return false;
    }

    internal bool CanEncode(ReadOnlySpan<char> input)
    {
        while (!input.IsEmpty)
        {
            if (!TryMatch(input, out int length, out _)) return false;
            input = input[length..];
        }
        return true;
    }

    internal static int ScalarLength(ReadOnlySpan<char> input) =>
        Rune.DecodeFromUtf16(input, out _, out int length) == OperationStatus.Done ? length : 1;
}
