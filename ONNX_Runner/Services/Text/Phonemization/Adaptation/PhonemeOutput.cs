using System.Text;
using System.Buffers;

namespace ONNX_Runner.Services.Text.Phonemization.Adaptation;

// A value-type writer keeps the ordinary path's existing StringBuilder allocation unchanged.
internal ref struct PhonemeOutput(int capacity, bool captureAdaptations)
{
    internal readonly StringBuilder Builder = new(capacity);
    private readonly bool _capture = captureAdaptations;
    private AdaptedSpan _first;
    private AdaptedSpan[]? _adaptations;
    private int _count;
    private int _group;

    internal readonly void Append(string value) => Builder.Append(value);
    internal readonly void Append(ReadOnlySpan<char> value) => Builder.Append(value);
    internal int NewGroup() => ++_group;

    internal void Append(PhonemePlan plan, int group = 0, bool joined = false)
    {
        int start = Builder.Length;
        Builder.Append(plan.Text);
        if (!_capture) return;
        if (group == 0) group = NewGroup();
        Add(new(start, joined ? -group : group, plan));
    }

    internal void Append(PhonemeSequence sequence)
    {
        int start = Builder.Length;
        Builder.Append(sequence.Text);
        if (!_capture || sequence.Adaptations.IsEmpty) return;
        int previous = 0, group = 0;
        foreach (var span in sequence.Adaptations)
        {
            if (span.Group != previous) { previous = span.Group; group = NewGroup(); }
            Add(span with { Start = start + span.Start, Group = span.Group < 0 ? -group : group });
        }
    }

    private void Add(AdaptedSpan span)
    {
        if (_count == 0) { _first = span; _count = 1; return; }
        if (_adaptations == null)
        {
            _adaptations = ArrayPool<AdaptedSpan>.Shared.Rent(16);
            _adaptations[0] = _first;
        }
        if (_count == _adaptations.Length)
        {
            var next = ArrayPool<AdaptedSpan>.Shared.Rent(checked(_count * 2));
            _adaptations.AsSpan(0, _count).CopyTo(next);
            ArrayPool<AdaptedSpan>.Shared.Return(_adaptations, clearArray: true);
            _adaptations = next;
        }
        _adaptations[_count++] = span;
    }

    internal readonly PhonemeSequence Build() => new(Builder.ToString(), _count switch
    {
        0 => null,
        1 => [_first],
        _ => _adaptations.AsSpan(0, _count).ToArray()
    });

    internal void Dispose()
    {
        if (_adaptations != null) ArrayPool<AdaptedSpan>.Shared.Return(_adaptations, clearArray: true);
        _adaptations = null;
        _first = default;
        _count = 0;
    }
}
