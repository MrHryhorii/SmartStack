using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;

/// <summary>Observes real shared-pool leases without replacing production allocators.</summary>
sealed class PoolEvents : EventListener
{
    private readonly object _sync = new();
    private readonly HashSet<int> _rented = [], _returned = [];
    private volatile bool _active;
    private int _thread;
    private bool _allThreads;
    public void Begin(bool allThreads = false) { _thread = Environment.CurrentManagedThreadId; _allThreads = allThreads; _active = true; }
    public void End() => _active = false;
    public int Outstanding { get { lock (_sync) return _rented.Except(_returned).Count(); } }
    public bool WasReturned(Array buffer) { lock (_sync) return _returned.Contains(RuntimeHelpers.GetHashCode(buffer)); }
    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "System.Buffers.ArrayPoolEventSource") EnableEvents(source, EventLevel.Verbose);
    }
    protected override void OnEventWritten(EventWrittenEventArgs value)
    {
        if (!_active || !_allThreads && Environment.CurrentManagedThreadId != _thread) return;
        if (value.Payload is not { Count: > 0 } || value.Payload[0] is not int id) return;
        lock (_sync)
        {
            if (value.EventName == "BufferRented") { _rented.Add(id); _returned.Remove(id); }
            if (value.EventName == "BufferReturned") _returned.Add(id);
        }
    }
}
