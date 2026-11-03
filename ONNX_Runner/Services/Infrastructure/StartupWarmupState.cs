namespace ONNX_Runner.Services.Infrastructure;

/// <summary>Reserves two startup-only request IDs without advancing the public request sequence.</summary>
internal sealed class StartupWarmupState
{
    internal const string HeaderName = "X-Tsubaki-Startup-Warmup";
    private readonly string[] _headers = [$"{Guid.NewGuid():N}/-1", $"{Guid.NewGuid():N}/0"];
    private int _active = 1;
    private int _claimed;

    internal string HeaderForPass(int pass) => _headers[pass];

    internal bool IsStartupRequest(HttpRequest request)
        => Match(request, out int pass) && (Volatile.Read(ref _claimed) & (1 << pass)) == 0;

    internal bool TryClaim(HttpRequest request, out long requestId)
    {
        requestId = 0;
        if (!Match(request, out int pass)) return false;
        int bit = 1 << pass;
        int observed = Volatile.Read(ref _claimed);
        while ((observed & bit) == 0)
        {
            int previous = Interlocked.CompareExchange(ref _claimed, observed | bit, observed);
            if (previous == observed) { requestId = pass - 1; return true; }
            observed = previous;
        }
        return false;
    }

    internal void Complete() => Interlocked.Exchange(ref _active, 0);

    private bool Match(HttpRequest request, out int pass)
    {
        pass = 0;
        if (Volatile.Read(ref _active) == 0) return false;
        var values = request.Headers[HeaderName];
        if (values.Count != 1) return false;
        if (string.Equals(values[0], _headers[0], StringComparison.Ordinal)) return true;
        pass = 1;
        return string.Equals(values[0], _headers[1], StringComparison.Ordinal);
    }
}
