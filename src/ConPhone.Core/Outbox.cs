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

    // BLE connection intervals are much slower than modern gaming-mouse raw input.
    // Hold only pointer motion for a tiny frame window, then merge any motion that
    // arrived after the first packet was dequeued. Keyboard events and button
    // transitions remain strict ordering barriers.
    public async Task<Envelope> TakePointerFrameAsync(
        CancellationToken token,
        int coalesceMilliseconds = 6)
    {
        if (coalesceMilliseconds is < 0 or > 20)
            throw new ArgumentOutOfRangeException(nameof(coalesceMilliseconds));

        var first = await TakeAsync(token).ConfigureAwait(false);
        if (coalesceMilliseconds == 0 || first.Packet is not PointerPacket pointer)
            return first;

        await Task.Delay(coalesceMilliseconds, token).ConfigureAwait(false);

        long x = pointer.X, y = pointer.Y, wheel = pointer.Wheel, pan = pointer.Pan;
        lock (_gate)
        {
            while (_items.First is { } next &&
                   next.Value.Epoch == first.Epoch &&
                   next.Value.Packet is PointerPacket candidate &&
                   candidate.Buttons == pointer.Buttons)
            {
                long nx = x + candidate.X;
                long ny = y + candidate.Y;
                long nw = wheel + candidate.Wheel;
                long np = pan + candidate.Pan;

                if (new[] { nx, ny, nw, np }.Any(v => Math.Abs(v) > 32767))
                    break;

                _items.RemoveFirst();
                x = nx;
                y = ny;
                wheel = nw;
                pan = np;
            }
        }

        return new Envelope(
            first.Epoch,
            new PointerPacket(pointer.Buttons, (int)x, (int)y, (int)wheel, (int)pan));
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
