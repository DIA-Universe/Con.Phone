namespace ConPhone.Core;

/// <summary>
/// Own one active native BLE session per peer. WinRT may return different managed
/// wrappers for keyboard, mouse, clipboard and request callbacks on the same link.
/// A borrowed wrapper is never closed merely because its reference differs.
/// </summary>
public static class SharedBleResource<T> where T : class, IDisposable
{
    internal sealed class Entry(string peer, T resource, Func<T, bool> active)
    {
        public readonly string Peer = peer;
        public readonly T Resource = resource;
        public readonly Func<T, bool> Active = active;
        public readonly long Generation = Interlocked.Increment(ref _nextGeneration);
        public int References;
        public bool Closing;
    }

    private static long _nextGeneration;
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    public static Lease Acquire(string peer, T resource, Func<T, bool> active)
    {
        lock (Entries)
        {
            if (!active(resource)) throw new ObjectDisposedException(nameof(resource));
            Entries.TryGetValue(peer, out var entry);
            if (entry?.Closing == true) throw new ObjectDisposedException(nameof(resource));
            if (entry is null || !entry.Active(entry.Resource))
            {
                entry = new Entry(peer, resource, active);
                Entries[peer] = entry;
            }
            entry.References++;
            return new Lease(entry);
        }
    }

    public sealed class Lease : IDisposable
    {
        private Entry? _entry;
        internal Lease(Entry entry) => _entry = entry;
        public T Resource => Volatile.Read(ref _entry)?.Resource
            ?? throw new ObjectDisposedException(nameof(Lease));
        public long Generation => Volatile.Read(ref _entry)?.Generation ?? 0;
        public bool IsActive => Volatile.Read(ref _entry) is { } entry && entry.Active(entry.Resource);

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry is null) return;
            lock (Entries)
            {
                if (--entry.References != 0) return;
                entry.Closing = true;
            }
            try { entry.Resource.Dispose(); }
            finally
            {
                lock (Entries)
                {
                    // Keep a closing entry until native Close completes. Otherwise
                    // another wrapper could reacquire the link just before Close.
                    // An old connection's delayed cleanup must not remove a new one.
                    if (Entries.TryGetValue(entry.Peer, out var current) && ReferenceEquals(current, entry))
                        Entries.Remove(entry.Peer);
                }
            }
        }
    }
}
