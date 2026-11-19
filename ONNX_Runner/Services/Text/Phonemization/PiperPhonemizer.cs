using ONNX_Runner.Models.Inference;
using System.Buffers;
using ONNX_Runner.Services.Text.Phonemization.Adaptation;

namespace ONNX_Runner.Services.Text.Phonemization;

public interface IPhonemizer
{
    // Converts phonemes into the model's padded input IDs.
    long[] PhonemesToIds(string phonemes, bool isContinuation = false, bool isFinished = true);
    long[] PhonemesToIds(PhonemeSequence phonemes, bool isContinuation = false, bool isFinished = true) =>
        PhonemesToIds(phonemes.Text, isContinuation, isFinished);
}

/// <summary>Converts phonemes into Piper tensors using the loaded model's complete token mappings.</summary>
public partial class PiperPhonemizer(PiperConfig config, ILogger<PiperPhonemizer> logger) : IPhonemizer
{
    private readonly PhonemeInventory _inventory = new(config.PhonemeIdMap);
    private readonly int[] _startIds = GetServiceIds(config, "^", 1);
    private readonly int[] _endIds = GetServiceIds(config, "$", 2);
    private readonly int[] _padIds = GetServiceIds(config, "_", 0);
    private readonly int[] _spaceIds = GetServiceIds(config, " ", 3);

    /// <summary>Reads tokens once, using stack storage or a pooled buffer before allocating the returned tensor.</summary>
    public long[] PhonemesToIds(string phonemes, bool isContinuation = false, bool isFinished = true)
    {
        var output = new IdBuffer(stackalloc long[512]);
        try
        {
            WriteIds(phonemes.AsSpan(), ref output, isContinuation, isFinished);
            long[] result = output.ToArray();
            LogInputPhonemes(logger, phonemes);
            LogTensorSize(logger, result.Length);
            return result;
        }
        finally
        {
            output.Dispose();
        }
    }

    private void WriteIds(ReadOnlySpan<char> phonemes, ref IdBuffer output, bool isContinuation, bool isFinished)
    {
        output.Append(_startIds);
        output.Append(_padIds);
        bool lastWasSpace = _startIds.AsSpan().SequenceEqual(_spaceIds);
        if (!isContinuation)
        {
            output.Append(_spaceIds);
            output.Append(_padIds);
            lastWasSpace = true;
        }

        while (!phonemes.IsEmpty)
        {
            bool normalizeSpace = phonemes[0] is '\r' or '\n' ||
                (char.IsWhiteSpace(phonemes[0]) && phonemes[0] != ' ' &&
                 !_inventory.TryMatch(phonemes, out _, out _));
            if (normalizeSpace)
            {
                int length = phonemes.StartsWith("\r\n", StringComparison.Ordinal) ? 2 : PhonemeInventory.ScalarLength(phonemes);
                phonemes = phonemes[length..];
                if (lastWasSpace) continue;
                output.Append(_spaceIds);
                output.Append(_padIds);
                lastWasSpace = true;
                continue;
            }
            if (!_inventory.TryMatch(phonemes, out int consumed, out var ids))
            {
                phonemes = phonemes[PhonemeInventory.ScalarLength(phonemes)..];
                continue;
            }

            // Padding separates phonemes, never the IDs belonging to one model token.
            output.Append(ids);
            output.Append(_padIds);
            lastWasSpace = ids.AsSpan().SequenceEqual(_spaceIds);
            phonemes = phonemes[consumed..];
        }
        if (isFinished && !lastWasSpace)
        {
            output.Append(_spaceIds);
            output.Append(_padIds);
        }
        output.Append(_endIds);
    }

    public long[] PhonemesToIds(PhonemeSequence phonemes, bool isContinuation = false, bool isFinished = true)
    {
        // Keep the native loop independent of adaptation bookkeeping on the common path.
        if (phonemes.Adaptations.IsEmpty) return PhonemesToIds(phonemes.Text, isContinuation, isFinished);
        var output = new IdBuffer(stackalloc long[512]);
        try
        {
            WriteAdaptedIds(phonemes, ref output, isContinuation, isFinished);
            long[] result = output.ToArray();
            LogInputPhonemes(logger, phonemes.Text);
            LogTensorSize(logger, result.Length);
            return result;
        }
        finally
        {
            output.Dispose();
        }
    }

    private void WriteAdaptedIds(PhonemeSequence sequence, ref IdBuffer output, bool isContinuation, bool isFinished)
    {
        output.Append(_startIds);
        output.Append(_padIds);
        bool lastWasSpace = _startIds.AsSpan().SequenceEqual(_spaceIds);
        if (!isContinuation)
        {
            output.Append(_spaceIds);
            output.Append(_padIds);
            lastWasSpace = true;
        }

        ReadOnlySpan<char> text = sequence.Text.AsSpan();
        ReadOnlySpan<AdaptedSpan> groups = sequence.Adaptations;
        int position = 0;
        for (int index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            AppendPhonemes(text[position..group.Start], ref output, ref lastWasSpace);
            output.Append(group.Joined ? group.Plan.JoinedIds : group.Plan.Ids);
            position = group.Start + group.Length;
            bool joinsNext = index + 1 < groups.Length && groups[index + 1].Group == group.Group &&
                groups[index + 1].Start == position;
            if (!joinsNext) output.Append(_padIds);
            lastWasSpace = group.Plan.LastTokenIds.AsSpan().SequenceEqual(_spaceIds);
        }
        AppendPhonemes(text[position..], ref output, ref lastWasSpace);
        if (isFinished && !lastWasSpace)
        {
            output.Append(_spaceIds);
            output.Append(_padIds);
        }
        output.Append(_endIds);
    }

    private void AppendPhonemes(ReadOnlySpan<char> phonemes, ref IdBuffer output, ref bool lastWasSpace)
    {
        while (!phonemes.IsEmpty)
        {
            bool normalizeSpace = phonemes[0] is '\r' or '\n' ||
                (char.IsWhiteSpace(phonemes[0]) && phonemes[0] != ' ' &&
                 !_inventory.TryMatch(phonemes, out _, out _));
            if (normalizeSpace)
            {
                int length = phonemes.StartsWith("\r\n", StringComparison.Ordinal) ? 2 : PhonemeInventory.ScalarLength(phonemes);
                phonemes = phonemes[length..];
                if (lastWasSpace) continue;
                output.Append(_spaceIds);
                output.Append(_padIds);
                lastWasSpace = true;
                continue;
            }
            if (!_inventory.TryMatch(phonemes, out int consumed, out var ids))
            {
                phonemes = phonemes[PhonemeInventory.ScalarLength(phonemes)..];
                continue;
            }

            // Padding separates phonemes, never the IDs belonging to one model token.
            output.Append(ids);
            output.Append(_padIds);
            lastWasSpace = ids.AsSpan().SequenceEqual(_spaceIds);
            phonemes = phonemes[consumed..];
        }
    }

    private ref struct IdBuffer(Span<long> storage)
    {
        private Span<long> _storage = storage;
        private long[]? _rented;
        private int _count;

        internal void Append(ReadOnlySpan<int> ids)
        {
            int required = checked(_count + ids.Length);
            if (required > _storage.Length) Grow(required);
            for (int index = 0; index < ids.Length; index++) _storage[_count + index] = ids[index];
            _count = required;
        }

        internal readonly long[] ToArray() => _storage[.._count].ToArray();

        private void Grow(int required)
        {
            long[] next = ArrayPool<long>.Shared.Rent(Math.Max(required, checked(_storage.Length * 2)));
            _storage[.._count].CopyTo(next);
            long[]? previous = _rented;
            _storage = _rented = next;
            if (previous != null) ArrayPool<long>.Shared.Return(previous);
        }

        internal readonly void Dispose()
        {
            if (_rented != null) ArrayPool<long>.Shared.Return(_rented);
        }
    }

    private static int[] GetServiceIds(PiperConfig config, string token, int fallback) =>
        config.PhonemeIdMap.TryGetValue(token, out var ids) && ids is { Length: > 0 } ? ids : [fallback];

    [LoggerMessage(Level = LogLevel.Debug, Message = "Input Phonemes: {Phonemes}")]
    private static partial void LogInputPhonemes(ILogger logger, string phonemes);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Target Tensor Size: {TensorSize}")]
    private static partial void LogTensorSize(ILogger logger, int tensorSize);
}
