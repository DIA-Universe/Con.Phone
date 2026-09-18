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
                long x = (long)a.X + b.X, y = (long)a.Y + b.Y;
                long w = (long)a.Wheel + b.Wheel, h = (long)a.Pan + b.Pan;
                if (new[] { x, y, w, h }.Any(v => Math.Abs(v) > 32767)) return false;
                last.Value = new(item.Epoch, new PointerPacket(a.Buttons, (int)x, (int)y, (int)w, (int)h));
                return true;
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
