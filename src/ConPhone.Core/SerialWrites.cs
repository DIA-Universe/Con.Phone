namespace ConPhone.Core;

// Cancelling a caller's wait does NOT mean the underlying native notification has completed.
// Keep its task and await it before starting another native operation.
public sealed class SerialWrites
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task? _pending;
    public bool Busy => _pending is { IsCompleted: false };
    public async Task<bool> RunAsync(Func<bool> stillValid, Func<Task> write, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_pending is { } previous)
            {
                try { await previous.WaitAsync(token).ConfigureAwait(false); }
                catch when (previous.IsCompleted && !token.IsCancellationRequested) { /* previous caller reports its failure */ }
                _pending = null;
            }
            token.ThrowIfCancellationRequested();
            if (!stillValid()) return false;
            _pending = write();
            await _pending.WaitAsync(token).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }
}
