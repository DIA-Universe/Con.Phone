namespace ConPhone.Core;

// Native connection/subscription snapshots can transiently disagree during host
// setup. Publication recovery waits for a quiet interval; input return never does.
public sealed class BleRecoveryQuietPeriod
{
    public const int DurationMs = 5000;
    private long _lastActivity = long.MinValue;
    public void Observe(long now)
    {
        long previous;
        do
        {
            previous = Interlocked.Read(ref _lastActivity);
            if (now <= previous) return;
        } while (Interlocked.CompareExchange(ref _lastActivity, now, previous) != previous);
    }
    public bool IsQuiet(long now)
    {
        var last = Interlocked.Read(ref _lastActivity);
        return last == long.MinValue || now - last >= DurationMs;
    }
}
