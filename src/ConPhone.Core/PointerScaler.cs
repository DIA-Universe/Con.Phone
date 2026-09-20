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
    private long _carryX, _carryY, _carryWheel, _carryPan;

    public int SpeedPercent { get { lock (_gate) return _speedPercent; } }
    public int ScrollPercent { get { lock (_gate) return _scrollPercent; } }
    public bool InvertScroll { get { lock (_gate) return _invertScroll; } }

    public void Configure(int speedPercent, int scrollPercent, bool invertScroll)
    {
        lock (_gate)
        {
            _speedPercent = Math.Clamp(speedPercent, 1, 300);
            _scrollPercent = Math.Clamp(scrollPercent, 25, 300);
            _invertScroll = invertScroll;
            _carryX = _carryY = _carryWheel = _carryPan = 0;
        }
    }

    public PointerPacket Apply(PointerPacket packet)
    {
        lock (_gate)
        {
            var sx = (long)packet.X * _speedPercent + _carryX;
            var sy = (long)packet.Y * _speedPercent + _carryY;
            var direction = _invertScroll ? -1L : 1L;
            var sw = (long)packet.Wheel * _scrollPercent * direction + _carryWheel;
            var sp = (long)packet.Pan * _scrollPercent * direction + _carryPan;

            var x = (int)(sx / 100);
            var y = (int)(sy / 100);
            var wheel = (int)(sw / 100);
            var pan = (int)(sp / 100);

            _carryX = sx % 100;
            _carryY = sy % 100;
            _carryWheel = sw % 100;
            _carryPan = sp % 100;

            return new PointerPacket(packet.Buttons, x, y, wheel, pan);
        }
    }

    public void Reset()
    {
        lock (_gate) _carryX = _carryY = _carryWheel = _carryPan = 0;
    }
}
