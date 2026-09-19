namespace ConPhone.Core;

// Bounded queue; only adjacent pointer packets with identical buttons/epoch may merge.
// A key or button transition is an ordering barrier. No key-up event is silently dropped.
public sealed class Outbox
{
    private readonly object _gate = new();
    private readonly LinkedList<Envelope> _items = [];
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly int _capacity;
    public Outbox(int capacity = 128)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }
    public int Count { get { lock (_gate) return _items.Count; } }
    public bool TryAdd(Envelope item)
    {
        lock (_gate)
        {
            if (_items.Last is { } last && last.Value.Epoch == item.Epoch &&
                last.Value.Packet is PointerPacket a && item.Packet is PointerPacket b && a.Buttons == b.Buttons)
            {
                long x = (long)a.X + b.X;
                long y = (long)a.Y + b.Y;
                long wheel = (long)a.Wheel + b.Wheel;
                long pan = (long)a.Pan + b.Pan;

                // This path runs for every raw mouse packet. Keep it allocation-free
                // so pointer coalescing itself cannot introduce GC jitter.
                var canMerge =
                    x is >= short.MinValue and <= short.MaxValue &&
                    y is >= short.MinValue and <= short.MaxValue &&
                    wheel is >= short.MinValue and <= short.MaxValue &&
                    pan is >= short.MinValue and <= short.MaxValue;

                if (canMerge)
                {
                    last.Value = new(
                        item.Epoch,
                        new PointerPacket(
                            a.Buttons,
                            (int)x,
                            (int)y,
                            (int)wheel,
                            (int)pan));
                    return true;
                }

                // A very fast/high-DPI move is not a queue failure. Stop coalescing
                // this pair and enqueue a second pointer item below; Hid.Encode will
                // split each large relative move into legal HID reports.
            }
            if (_items.Count >= _capacity) return false;
            _items.AddLast(item);
            if (_signal.CurrentCount == 0) _signal.Release();
            return true;
        }
    }
    public async Task<Envelope> TakeAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_items.First is { } first)
                {
                    _items.RemoveFirst();
                    return first.Value;
                }
            }
            await _signal.WaitAsync(token).ConfigureAwait(false);
        }
    }
    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            while (_signal.Wait(0)) { }
        }
    }
}
