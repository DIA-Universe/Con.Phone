namespace ConPhone.Core;

public enum PhoneSide { Right, Left }
public readonly record struct ScreenBounds(int Left, int Top, int Right, int Bottom)
{
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}
public sealed record EdgeEntry(ScreenBounds Screen, int Y, PhoneSide Side);
public sealed record CalibrationMarkPacket : InputPacket;

public sealed class EdgeSwitchProfile
{
    public bool Enabled { get; set; }
    public PhoneSide Side { get; set; } = PhoneSide.Right;
    public int Travel { get; set; }
    public bool Calibrated => Travel is >= 32 and <= 32767;
    public EdgeSwitchProfile Normalized() => new()
    {
        Side = Enum.IsDefined(Side) ? Side : PhoneSide.Right,
        Travel = Calibrated ? Travel : 0,
        Enabled = Enabled && Calibrated
    };
}

// Owned by the input thread. Only the outer edge at the cursor's current height
// is eligible; adjacent monitors (including staggered/negative layouts) stay local.
public sealed class EdgeEntryDetector
{
    private bool _armed;
    public void Disarm() => _armed = false;
    public EdgeEntry? Observe(IReadOnlyList<ScreenBounds> screens, PhoneSide side,
        int x, int y, int deltaX, bool blocked)
    {
        var row = screens.Where(s => y >= s.Top && y < s.Bottom).ToArray();
        if (row.Length == 0) { _armed = false; return null; }
        var edge = side == PhoneSide.Right ? row.Max(s => s.Right) - 1 : row.Min(s => s.Left);
        var distance = side == PhoneSide.Right ? edge - x : x - edge;
        if (distance >= 16) _armed = true;
        if (distance > 0 || !row.Any(s => s.Contains(x, y))) return null;
        if (blocked) { _armed = false; return null; }
        if (!_armed || (side == PhoneSide.Right ? deltaX <= 0 : deltaX >= 0)) return null;
        _armed = false;
        return new EdgeEntry(row.First(s => s.Contains(x, y)), y, side);
    }
}

// Coordinates are transmitted relative HID units, NOT phone pixels. Saturation
// and acceleration on the host cannot be observed without a companion app.
public sealed class EstimatedPhonePointer
{
    private readonly int _travel, _direction;
    private long _position;
    private bool _armed;
    public EstimatedPhonePointer(int travel, PhoneSide side)
    {
        if (travel is < 32 or > 32767) throw new ArgumentOutOfRangeException(nameof(travel));
        _travel = travel;
        _direction = side == PhoneSide.Right ? 1 : -1;
    }
    public long Position => _position;
    public bool Advance(int deliveredX, bool held)
    {
        var movement = (long)deliveredX * _direction;
        _position = Math.Clamp(_position + movement, 0, _travel);
        if (_position >= Math.Clamp(_travel / 20, 8, 32)) _armed = true;
        if (_position != 0 || movement >= 0 || !_armed) return false;
        _armed = false; // Releasing a key at the edge must not cause a late return.
        return !held;
    }
}

public sealed class PointerCalibration
{
    private readonly int _direction;
    private long _leg, _outward;
    public int Stage { get; private set; }
    public PointerCalibration(PhoneSide side) => _direction = side == PhoneSide.Right ? 1 : -1;
    public void Advance(int deliveredX)
    {
        if (Stage is 1 or 2) _leg += (long)deliveredX * _direction;
    }
    public int? Mark()
    {
        if (Stage == 0) { Stage = 1; _leg = 0; return null; }
        if (Stage == 1)
        {
            if (_leg is < 32 or > 32767) throw new InvalidOperationException("반대쪽 끝까지 평소 속도로 이동한 뒤 다시 보정해 주세요.");
            _outward = _leg; _leg = 0; Stage = 2; return null;
        }
        if (Stage != 2) throw new InvalidOperationException("보정이 이미 끝났어요.");
        var back = -_leg;
        if (back is < 32 or > 32767 || Math.Abs(back - _outward) > Math.Max(back, _outward) * .35)
            throw new InvalidOperationException("왕복 이동량 차이가 커요. 휴대폰 감도를 유지하고 비슷한 속도로 다시 보정해 주세요.");
        Stage = 3;
        return (int)((_outward + back) / 2);
    }
}
