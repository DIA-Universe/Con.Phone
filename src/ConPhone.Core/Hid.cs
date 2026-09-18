namespace ConPhone.Core;

public abstract record InputPacket;
public sealed record KeyboardPacket(byte[] Data) : InputPacket;
public sealed record PointerPacket(byte Buttons, int X = 0, int Y = 0, int Wheel = 0, int Pan = 0) : InputPacket;
public sealed record Envelope(long Epoch, InputPacket Packet);
public sealed record WireReport(bool Keyboard, byte[] Data);

public static class Hid
{
    // HOGP: report IDs are in the Report Reference descriptor, NOT in notification values.
    // ID 1: keyboard (8 input bytes / 1 output byte). ID 2: relative mouse (5 bytes).
    public static byte[] ReportMap => Convert.FromHexString(
        "05010906A1018501050719E029E715002501750195088102950175088101" +
        "95067508150026FF0019002AFF008100050819012905950575019102950175039101C0" +
        "05010902A10185020901A1000509190129051500250175019505810295038101" +
        "05010930093109381581257F750895038106050C0A38021581257F750895018106C0C0");

    public static IEnumerable<WireReport> Encode(InputPacket packet)
    {
        if (packet is KeyboardPacket k)
        {
            if (k.Data.Length != 8) throw new ArgumentException("Keyboard reports must contain 8 bytes.");
            yield return new(true, k.Data.ToArray());
            yield break;
        }
        var p = (PointerPacket)packet;
        int x = p.X, y = p.Y, w = p.Wheel, h = p.Pan;
        if (new[] { x, y, w, h }.Any(v => Math.Abs((long)v) > 32767))
            throw new ArgumentOutOfRangeException(nameof(packet), "Pointer backlog exceeds safety limit.");
        do
        {
            int dx = Math.Clamp(x, -127, 127), dy = Math.Clamp(y, -127, 127);
            int dw = Math.Clamp(w, -127, 127), dh = Math.Clamp(h, -127, 127);
            yield return new(false, [(byte)(p.Buttons & 31), unchecked((byte)dx), unchecked((byte)dy), unchecked((byte)dw), unchecked((byte)dh)]);
            x -= dx; y -= dy; w -= dw; h -= dh;
        } while (x != 0 || y != 0 || w != 0 || h != 0);
    }

    public static byte Usage(int vk, int scan = 0, bool extended = false)
    {
        if (vk is >= 0x41 and <= 0x5A) return (byte)(vk - 0x41 + 4);
        if (vk is >= 0x31 and <= 0x39) return (byte)(vk - 0x31 + 0x1E);
        if (vk is >= 0x70 and <= 0x7B) return (byte)(vk - 0x70 + 0x3A);
        if (vk is >= 0x7C and <= 0x87) return (byte)(vk - 0x7C + 0x68);
        if (vk is >= 0x61 and <= 0x69) return (byte)(vk - 0x61 + 0x59);
        return vk switch
        {
            0x30 => 0x27, 0x0D => extended ? (byte)0x58 : (byte)0x28,
            0x1B => 0x29, 0x08 => 0x2A, 0x09 => 0x2B, 0x20 => 0x2C,
            0xBD => 0x2D, 0xBB => 0x2E, 0xDB => 0x2F, 0xDD => 0x30,
            0xDC => 0x31, 0xBA => 0x33, 0xDE => 0x34, 0xC0 => 0x35,
            0xBC => 0x36, 0xBE => 0x37, 0xBF => 0x38, 0x14 => 0x39,
            0x2C => 0x46, 0x91 => 0x47, 0x13 => 0x48, 0x2D => 0x49,
            0x24 => 0x4A, 0x21 => 0x4B, 0x2E => 0x4C, 0x23 => 0x4D,
            0x22 => 0x4E, 0x27 => 0x4F, 0x25 => 0x50, 0x28 => 0x51,
            0x26 => 0x52, 0x90 => 0x53, 0x6F => 0x54, 0x6A => 0x55,
            0x6D => 0x56, 0x6B => 0x57, 0x60 => 0x62, 0x6E => 0x63,
            0xE2 => 0x64, 0x5D => 0x65, 0x15 => 0x90, 0x19 => 0x91,
            0xA2 => 0xE0, 0xA0 => 0xE1, 0xA4 => 0xE2, 0x5B => 0xE3,
            0xA3 => 0xE4, 0xA1 => 0xE5, 0xA5 => 0xE6, 0x5C => 0xE7,
            0x10 => scan == 0x36 ? (byte)0xE5 : (byte)0xE1,
            0x11 => extended ? (byte)0xE4 : (byte)0xE0,
            0x12 => extended ? (byte)0xE6 : (byte)0xE2,
            _ => 0
        };
    }
}

public sealed class KeyboardState
{
    private readonly HashSet<byte> _pressed = [];
    public byte[]? Update(byte usage, bool down)
    {
        if (usage == 0) return null;
        bool changed = down ? _pressed.Add(usage) : _pressed.Remove(usage);
        return changed ? Snapshot() : null; // Host, not Windows repeat events, owns key repeat.
    }
    public byte[] Snapshot()
    {
        byte[] bytes = new byte[8];
        foreach (var key in _pressed.Where(k => k is >= 0xE0 and <= 0xE7))
            bytes[0] |= (byte)(1 << (key - 0xE0));
        var ordinary = _pressed.Where(k => k is < 0xE0 or > 0xE7).Order().ToArray();
        if (ordinary.Length > 6) Array.Fill(bytes, (byte)1, 2, 6); // ErrorRollOver, not silent loss.
        else ordinary.CopyTo(bytes, 2);
        return bytes;
    }
    public void Clear() => _pressed.Clear();
}
