namespace ConPhone.Core;

[Flags]
public enum ChordModifiers { None = 0, Control = 1, Alt = 2, Shift = 4, Windows = 8 }
public readonly record struct KeyChord(int Key, ChordModifiers Modifiers = ChordModifiers.None);
public enum InputCommand { None, EnterPhone, ReturnLocal, LockAndReturn, CancelRecording }
public readonly record struct KeyRoute(bool Suppress, InputCommand Command = InputCommand.None,
    byte[]? Report = null, KeyChord? Captured = null);

// Pure state machine: no Win32, WinRT, clipboard, UI, timers or asynchronous key-state reads.
// The caller owns synchronization. Every key-up belongs to the destination that received its down.
public sealed class KeyboardRouter
{
    private readonly HashSet<int> _pressed = [];
    private readonly HashSet<int> _localOwned = [];
    private readonly HashSet<int> _remoteOwned = [];
    private readonly HashSet<int> _eatUntilUp = [];
    private readonly KeyboardState _keys = new();
    public KeyChord Toggle { get; private set; } = new(0x77);
    public KeyChord Emergency { get; private set; } = new(0x78);
    public KeyChord LockReturn { get; private set; } = new(0x4C, ChordModifiers.Control | ChordModifiers.Alt);
    public bool Remote { get; private set; }
    public bool Recording { get; private set; }
    public bool Released => _pressed.Count == 0;
    public bool LockEnabled { get; private set; }

    public void Configure(KeyChord toggle, KeyChord emergency, KeyChord lockReturn, bool lockEnabled)
    {
        Validate(toggle); Validate(emergency); Validate(lockReturn);
        if (toggle == emergency || toggle == lockReturn || emergency == lockReturn)
            throw new ArgumentException("단축키 조합은 서로 달라야 합니다.");
        Toggle = toggle; Emergency = emergency; LockReturn = lockReturn; LockEnabled = lockEnabled;
    }
    public static bool IsModifier(int key) => key is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;
    public static void Validate(KeyChord chord)
    {
        if (chord.Key < 8 || chord.Key > 0xFE || IsModifier(chord.Key) || chord.Key is 0xE5 or 0xE7 || ((int)chord.Modifiers & ~15) != 0)
            throw new ArgumentException("일반 키 하나와 Ctrl / Alt / Shift / Win 조합을 입력하세요.");
        if ((chord.Key == 0x4C && chord.Modifiers.HasFlag(ChordModifiers.Windows)) ||
            (chord.Key == 0x2E && chord.Modifiers.HasFlag(ChordModifiers.Control | ChordModifiers.Alt)))
            throw new ArgumentException("Win+L과 Ctrl+Alt+Delete는 Windows 예약 조합입니다. 다른 조합을 사용하세요.");
    }
    public void SetRecording(bool enabled)
    {
        StopRemote();
        Recording = enabled;
    }
    public bool TryEnterPhone()
    {
        if (Recording || !Released) return false;
        _keys.Clear(); _remoteOwned.Clear(); Remote = true; return true;
    }
    public void StopRemote()
    {
        Remote = false;
        _eatUntilUp.UnionWith(_remoteOwned);
        _remoteOwned.Clear(); _keys.Clear();
    }
    private ChordModifiers Modifiers()
    {
        ChordModifiers m = 0;
        if (_pressed.Contains(0x11) || _pressed.Contains(0xA2) || _pressed.Contains(0xA3)) m |= ChordModifiers.Control;
        if (_pressed.Contains(0x12) || _pressed.Contains(0xA4) || _pressed.Contains(0xA5)) m |= ChordModifiers.Alt;
        if (_pressed.Contains(0x10) || _pressed.Contains(0xA0) || _pressed.Contains(0xA1)) m |= ChordModifiers.Shift;
        if (_pressed.Contains(0x5B) || _pressed.Contains(0x5C)) m |= ChordModifiers.Windows;
        return m;
    }
    public KeyRoute Process(int key, bool up, int scan = 0, bool extended = false)
    {
        bool first = up ? false : _pressed.Add(key);
        if (up) _pressed.Remove(key);
        if (_eatUntilUp.Contains(key))
        {
            if (up) _eatUntilUp.Remove(key);
            return new(true); // Includes auto-repeat, not just key-up.
        }
        if (up && _localOwned.Remove(key)) return new(false);
        if (Recording)
        {
            if (!up) _eatUntilUp.Add(key);
            if (first && key == 0x1B) return new(true, InputCommand.CancelRecording);
            return first && !IsModifier(key) ? new(true, Captured: new KeyChord(key, Modifiers())) : new(true);
        }
        if (first)
        {
            var chord = new KeyChord(key, Modifiers());
            InputCommand command = InputCommand.None;
            if (chord == Emergency) command = InputCommand.ReturnLocal;
            else if (chord == Toggle) command = Remote ? InputCommand.ReturnLocal : InputCommand.EnterPhone;
            else if (Remote && LockEnabled && chord == LockReturn) command = InputCommand.LockAndReturn;
            if (command != InputCommand.None)
            {
                _eatUntilUp.Add(key);
                if (command != InputCommand.EnterPhone) StopRemote();
                return new(true, command);
            }
        }
        if (!Remote)
        {
            if (!up) _localOwned.Add(key);
            return new(false);
        }
        if (up) _remoteOwned.Remove(key); else _remoteOwned.Add(key);
        return new(true, Report: _keys.Update(Hid.Usage(key, scan, extended), !up));
    }
}
