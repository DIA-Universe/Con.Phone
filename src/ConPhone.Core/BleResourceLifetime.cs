namespace ConPhone.Core;

/// <summary>One server generation. Late registrations are released immediately.</summary>
public sealed class BleResourceLifetime : IDisposable
{
    private static long _nextId;
    private readonly object _gate = new();
    private readonly List<Action> _cleanup = [];
    private readonly CancellationTokenSource _cancel = new();
    private readonly Action<string>? _log;
    private bool _disposed;
    public long Id { get; } = Interlocked.Increment(ref _nextId);
    public CancellationToken Token => _cancel.Token;
    public bool IsDisposed { get { lock (_gate) return _disposed; } }

    public BleResourceLifetime(Action<string>? log = null) => _log = log;

    public void Register(Action cleanup)
    {
        lock (_gate)
        {
            if (!_disposed) { _cleanup.Add(cleanup); return; }
        }
        BestEffort(cleanup);
    }

    public void Dispose()
    {
        Action[] actions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            actions = _cleanup.AsEnumerable().Reverse().ToArray();
            _cleanup.Clear();
        }
        Log($"[BLE][Session {Id}] cleanup started");
        BestEffort(_cancel.Cancel);
        foreach (var action in actions) BestEffort(action);
        Log($"[BLE][Session {Id}] cleanup completed");
        // Keep the cancelled CTS usable by async operations unwinding on other threads.
    }

    private void BestEffort(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log($"[BLE][Session {Id}] cleanup {ex.GetType().Name} HRESULT=0x{ex.HResult:X8}"); }
    }

    private void Log(string message) { try { _log?.Invoke(message); } catch { } }
}
