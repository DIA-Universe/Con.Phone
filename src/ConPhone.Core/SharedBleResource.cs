namespace ConPhone.Core;

/// <summary>
/// Own one active native BLE session per peer. WinRT may return different managed
/// wrappers for keyboard, mouse, clipboard and request callbacks on the same link.
/// A borrowed wrapper is never closed merely because its reference differs.
/// Retire/Detach release incoming observations without native Close; Dispose is
/// reserved for explicit teardown of an owned, non-retired generation.
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
        public bool Retired;
        public bool SuppressClose;
    }

    private static long _nextGeneration;
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    // HID and clipboard observe the same incoming link. A missing advertisement
    // must not cause either service to republish while the other still owns it.
    public static bool HasActiveResources
    {
        get
        {
            lock (Entries) return Entries.Values.Any(entry =>
                !entry.Closing && !entry.Retired && entry.References > 0 && entry.Active(entry.Resource));
        }
    }

    public static Lease Acquire(string peer, T resource, Func<T, bool> active)
    {
        lock (Entries)
        {
            if (!active(resource)) throw new ObjectDisposedException(nameof(resource));
            Entries.TryGetValue(peer, out var entry);
            if (entry?.Closing == true) throw new ObjectDisposedException(nameof(resource));
            if (entry is null || entry.Retired || !entry.Active(entry.Resource))
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
        public bool IsActive => Volatile.Read(ref _entry) is { } entry &&
            !Volatile.Read(ref entry.Retired) && entry.Active(entry.Resource);

        /// <summary>
        /// Invalidate this connection generation without closing a native session
        /// that Windows may already have reactivated for the same bonded peer.
        /// Delayed releases from this generation cannot close its replacement.
        /// </summary>
        public void Retire()
        {
            var entry = Volatile.Read(ref _entry);
            if (entry is null) return;
            lock (Entries)
            {
                if (entry.Closing) return;
                entry.Retired = true;
                entry.SuppressClose = true;
                if (Entries.TryGetValue(entry.Peer, out var current) && ReferenceEquals(current, entry))
                    Entries.Remove(entry.Peer);
            }
        }

        /// <summary>Release an observation of an incoming session, without native Close.</summary>
        public void Detach() => Release(close: false);

        public void Dispose() => Release(close: true);

        private void Release(bool close)
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry is null) return;
            lock (Entries)
            {
                if (!close) entry.SuppressClose = true;
                if (--entry.References != 0) return;
                entry.Closing = true;
                close = !entry.SuppressClose;
            }
            try { if (close) entry.Resource.Dispose(); }
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
