namespace ConPhone.Core;

/// <summary>Rate-limit failed publication without changing the lifetime of a live service.</summary>
public sealed class BleAdvertisingBackoff
{
    private long? _nextAttempt;
    private long _delay = 4_000;
    private readonly object _gate = new();

    public bool TryBegin(long monotonicMilliseconds)
    {
        lock (_gate)
        {
            if (_nextAttempt is { } next && monotonicMilliseconds < next) return false;
            _nextAttempt = monotonicMilliseconds + _delay;
            _delay = Math.Min(_delay * 2, 60_000);
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate) { _nextAttempt = null; _delay = 4_000; }
    }
}
