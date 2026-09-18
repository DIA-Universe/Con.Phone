namespace ConPhone.Core;

/// <summary>
/// Scales relative pointer motion before it is encoded as a BLE HID report.
/// Fractional carry keeps low sensitivity settings smooth instead of dropping small movements.
/// </summary>
public sealed class PointerScaler
{
    private readonly object _gate = new();
    private int _speedPercent = 100;
    private int _scrollPercent = 100;
    private bool _invertScroll;
    private double _carryX, _carryY, _carryWheel, _carryPan;

    public int SpeedPercent { get { lock (_gate) return _speedPercent; } }
    public int ScrollPercent { get { lock (_gate) return _scrollPercent; } }
    public bool InvertScroll { get { lock (_gate) return _invertScroll; } }

    public void Configure(int speedPercent, int scrollPercent, bool invertScroll)
    {
        lock (_gate)
        {
            _speedPercent = Math.Clamp(speedPercent, 25, 300);
            _scrollPercent = Math.Clamp(scrollPercent, 25, 300);
            _invertScroll = invertScroll;
            _carryX = _carryY = _carryWheel = _carryPan = 0;
        }
    }

    public PointerPacket Apply(PointerPacket packet)
    {
        lock (_gate)
        {
            var sx = packet.X * (_speedPercent / 100.0) + _carryX;
            var sy = packet.Y * (_speedPercent / 100.0) + _carryY;
            var direction = _invertScroll ? -1.0 : 1.0;
            var sw = packet.Wheel * (_scrollPercent / 100.0) * direction + _carryWheel;
            var sp = packet.Pan * (_scrollPercent / 100.0) * direction + _carryPan;

            var x = (int)Math.Truncate(sx);
            var y = (int)Math.Truncate(sy);
            var wheel = (int)Math.Truncate(sw);
            var pan = (int)Math.Truncate(sp);

            _carryX = sx - x;
            _carryY = sy - y;
            _carryWheel = sw - wheel;
            _carryPan = sp - pan;

            return new PointerPacket(packet.Buttons, x, y, wheel, pan);
        }
    }

    public void Reset()
    {
        lock (_gate) _carryX = _carryY = _carryWheel = _carryPan = 0;
    }
}
