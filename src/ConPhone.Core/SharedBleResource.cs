using System.Runtime.CompilerServices;

namespace ConPhone.Core;

/// <summary>HID and clipboard can receive the same WinRT session wrapper.</summary>
public static class SharedBleResource<T> where T : class, IDisposable
{
    private sealed class Entry { public int References; public bool Closed; }
    private static readonly ConditionalWeakTable<T, Entry> Entries = new();

    public static IDisposable Acquire(T resource)
    {
        lock (Entries)
        {
            var entry = Entries.GetOrCreateValue(resource);
            ObjectDisposedException.ThrowIf(entry.Closed, resource);
            entry.References++;
        }
        return new Lease(resource);
    }

    private sealed class Lease(T resource) : IDisposable
    {
        private T? _resource = resource;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _resource, null);
            if (current is null) return;
            lock (Entries)
            {
                var entry = Entries.GetOrCreateValue(current);
                if (--entry.References != 0) return;
                entry.Closed = true;
            }
            current.Dispose();
        }
    }
}
