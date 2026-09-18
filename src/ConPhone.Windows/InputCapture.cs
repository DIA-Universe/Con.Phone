using System.ComponentModel;
using System.Runtime.InteropServices;
using ConPhone.Core;

namespace ConPhone.Windows;

// Hooks run on their own message-loop thread. They never await Bluetooth or touch the UI.
public sealed class InputCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly KeyboardState _keys = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Hook? _keyboardDelegate, _mouseDelegate;
    private nint _keyboardHook, _mouseHook;
    private Sink? _sink;

    private volatile bool _remote;
    private HotkeyBinding _toggleHotkey = SettingsStore.DefaultToggle();
    private HotkeyBinding _emergencyHotkey = SettingsStore.DefaultEmergency();
    private HotkeyBinding _lockAndReturnHotkey = SettingsStore.DefaultLockAndReturn();

    private Action<HotkeyBinding>? _captureCallback;
    private readonly HashSet<int> _downKeys = [];
    private readonly HashSet<int> _suppressUntilUp = [];
    private readonly HashSet<int> _localPassthroughUntilUp = [];

    private byte _buttons;
    private int _wheel, _pan;

    public event Action<InputPacket>? Packet;
    public event Action? Toggle;
    public event Action? Emergency;
    public event Action? LockAndReturn;
    public event Action<string>? Fault;

    public bool IsRemote => _remote;
    public Task Started => _started.Task;

    public InputCapture()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Con.Phone.Input" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void ConfigureHotkeys(HotkeyBinding toggle, HotkeyBinding emergency, HotkeyBinding lockAndReturn)
    {
        if (!HotkeyCatalog.IsAllowedKey(toggle.Key) ||
            !HotkeyCatalog.IsAllowedKey(emergency.Key) ||
            !HotkeyCatalog.IsAllowedKey(lockAndReturn.Key))
            throw new ArgumentOutOfRangeException(nameof(toggle), "지원하지 않는 단축키입니다.");

        if (HotkeyCatalog.Same(toggle, emergency) ||
            HotkeyCatalog.Same(toggle, lockAndReturn) ||
            HotkeyCatalog.Same(emergency, lockAndReturn))
            throw new InvalidOperationException("각 동작의 단축키 조합은 서로 다르게 지정해 주세요.");

        lock (_gate)
        {
            _toggleHotkey = toggle.Clone();
            _emergencyHotkey = emergency.Clone();
            _lockAndReturnHotkey = lockAndReturn.Clone();
        }
    }

    // Capturing is performed inside the low-level hook, not in a TextBox. This prevents the
    // currently configured shortcut or Windows from stealing the key combination being edited.
    public void BeginHotkeyCapture(Action<HotkeyBinding> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        lock (_gate)
        {
            if (_remote) throw new InvalidOperationException("휴대폰 입력 중에는 단축키를 변경할 수 없습니다. 먼저 노트북으로 복귀해 주세요.");
            _captureCallback = completed;
            _suppressUntilUp.Clear();
        }
    }

    public void CancelHotkeyCapture()
    {
        lock (_gate) _captureCallback = null;
    }

    private void Run()
    {
        try
        {
            _sink = new Sink(this);
            var devices = new[] { new RawDevice { Page = 1, Usage = 2, Flags = 0x100, Target = _sink.Handle } };
            if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawDevice>())) throw new Win32Exception();

            _keyboardDelegate = KeyboardHook;
            _mouseDelegate = MouseHook;
            _keyboardHook = SetWindowsHookEx(13, _keyboardDelegate, GetModuleHandle(null), 0);
            _mouseHook = SetWindowsHookEx(14, _mouseDelegate, GetModuleHandle(null), 0);
            if (_keyboardHook == 0 || _mouseHook == 0) throw new Win32Exception();

            _started.TrySetResult();
            Application.Run();
        }
        catch (Exception ex)
        {
            _started.TrySetException(ex);
            Fault?.Invoke($"입력 훅 시작 실패: {ex.Message}");
        }
        finally
        {
            Deactivate();
            if (_keyboardHook != 0) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
            if (_sink is not null)
            {
                RegisterRawInputDevices(
                    [new RawDevice { Page = 1, Usage = 2, Flags = 1, Target = 0 }],
                    1,
                    (uint)Marshal.SizeOf<RawDevice>());
                _sink.Destroy();
            }
        }
    }

    public void Activate()
    {
        lock (_gate)
        {
            if (!_started.Task.IsCompletedSuccessfully)
                throw new InvalidOperationException("입력 수집이 준비되지 않았습니다.");

            var allowed = HotkeyPhysicalKeys(_toggleHotkey);
            _localPassthroughUntilUp.Clear();

            for (int vk = 1; vk < 255; vk++)
            {
                if ((GetAsyncKeyState(vk) & 0x8000) == 0) continue;
                if (!allowed.Contains(vk))
                    throw new InvalidOperationException("입력 전환 단축키 외의 키와 마우스 버튼을 모두 놓아 주세요.");

                // Modifier down events were delivered to Windows while input was local. Once remote
                // mode starts their key-up events must still reach Windows or Ctrl/Alt can appear stuck.
                if (vk != _toggleHotkey.Key && IsPhysicalModifier(vk))
                    _localPassthroughUntilUp.Add(vk);
            }

            _keys.Clear();
            _buttons = 0;
            _wheel = 0;
            _pan = 0;
            _remote = true;
        }
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            _remote = false;
            _keys.Clear();
            _buttons = 0;
            _wheel = 0;
            _pan = 0;
        }
    }

    private nint KeyboardHook(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(0, code, message, data);

        try
        {
            var k = Marshal.PtrToStructure<KeyData>(data);
            if ((k.Flags & 0x10) != 0) return CallNextHookEx(0, code, message, data); // injected

            int key = (int)k.Vk;
            bool up = (k.Flags & 0x80) != 0;
            bool firstDown;
            Action<HotkeyBinding>? capturedCallback = null;
            HotkeyBinding? capturedBinding = null;
            HotkeyBinding toggle, emergency, lockAndReturn;

            lock (_gate)
            {
                firstDown = !up && _downKeys.Add(key);
                if (up) _downKeys.Remove(key);

                if (_suppressUntilUp.Contains(key))
                {
                    if (up) _suppressUntilUp.Remove(key);
                    return 1;
                }

                if (up && _localPassthroughUntilUp.Remove(key))
                    return CallNextHookEx(0, code, message, data);

                if (_captureCallback is not null)
                {
                    // Suppress the complete capture chord so neither Windows nor an old shortcut acts on it.
                    if (!up && firstDown && !HotkeyCatalog.IsModifierKey(key) && HotkeyCatalog.IsAllowedKey(key))
                    {
                        capturedBinding = new HotkeyBinding { Key = key, Modifiers = ModifiersFromDownKeysLocked() };
                        capturedCallback = _captureCallback;
                        _captureCallback = null;

                        foreach (var down in _downKeys) _suppressUntilUp.Add(down);
                        _suppressUntilUp.Add(key);
                    }

                    // Even while only modifiers are held, keep them out of Windows during capture.
                    if (capturedBinding is null) return 1;
                }

                toggle = _toggleHotkey.Clone();
                emergency = _emergencyHotkey.Clone();
                lockAndReturn = _lockAndReturnHotkey.Clone();
            }

            if (capturedBinding is not null && capturedCallback is not null)
            {
                capturedCallback(capturedBinding);
                return 1;
            }

            if (!up && firstDown)
            {
                HotkeyModifiers modifiers;
                lock (_gate) modifiers = ModifiersFromDownKeysLocked();

                if (Matches(toggle, key, modifiers))
                {
                    lock (_gate)
                    {
                        _suppressUntilUp.Add(key);
                        if (!_remote)
                        {
                            foreach (var down in _downKeys)
                                if (down != key && IsPhysicalModifier(down))
                                    _localPassthroughUntilUp.Add(down);
                        }
                    }
                    Toggle?.Invoke();
                    return 1;
                }

                if (_remote && Matches(emergency, key, modifiers))
                {
                    lock (_gate) _suppressUntilUp.Add(key);
                    Deactivate();
                    Emergency?.Invoke();
                    return 1;
                }

                if (_remote && Matches(lockAndReturn, key, modifiers))
                {
                    lock (_gate) _suppressUntilUp.Add(key);
                    Deactivate();
                    LockAndReturn?.Invoke();
                    return 1;
                }
            }

            lock (_gate)
            {
                if (!_remote) return CallNextHookEx(0, code, message, data);

                var report = _keys.Update(
                    Hid.Usage(key, (int)k.Scan, (k.Flags & 1) != 0),
                    !up);

                if (report is not null) Packet?.Invoke(new KeyboardPacket(report));
                return _remote ? 1 : CallNextHookEx(0, code, message, data);
            }
        }
        catch (Exception ex)
        {
            Deactivate();
            Fault?.Invoke($"키보드 처리 오류 — 로컬 복귀: {ex.Message}");
            return CallNextHookEx(0, code, message, data);
        }
    }

    private static bool Matches(HotkeyBinding binding, int key, HotkeyModifiers modifiers) =>
        binding.Key == key && binding.Modifiers == modifiers;

    private HotkeyModifiers ModifiersFromDownKeysLocked()
    {
        HotkeyModifiers result = HotkeyModifiers.None;
        if (_downKeys.Any(k => k is 0x11 or 0xA2 or 0xA3)) result |= HotkeyModifiers.Control;
        if (_downKeys.Any(k => k is 0x12 or 0xA4 or 0xA5)) result |= HotkeyModifiers.Alt;
        if (_downKeys.Any(k => k is 0x10 or 0xA0 or 0xA1)) result |= HotkeyModifiers.Shift;
        if (_downKeys.Any(k => k is 0x5B or 0x5C)) result |= HotkeyModifiers.Windows;
        return result;
    }

    private static bool IsPhysicalModifier(int key) => key is
        0x10 or 0xA0 or 0xA1 or
        0x11 or 0xA2 or 0xA3 or
        0x12 or 0xA4 or 0xA5 or
        0x5B or 0x5C;

    private static HashSet<int> HotkeyPhysicalKeys(HotkeyBinding binding)
    {
        var keys = new HashSet<int> { binding.Key };
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            keys.Add(0x11); keys.Add(0xA2); keys.Add(0xA3);
        }
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            keys.Add(0x12); keys.Add(0xA4); keys.Add(0xA5);
        }
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            keys.Add(0x10); keys.Add(0xA0); keys.Add(0xA1);
        }
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            keys.Add(0x5B); keys.Add(0x5C);
        }
        return keys;
    }

    private nint MouseHook(int code, nint message, nint data)
    {
        if (code < 0 || !_remote) return CallNextHookEx(0, code, message, data);
        var m = Marshal.PtrToStructure<MouseData>(data);
        if ((m.Flags & 1) != 0) return CallNextHookEx(0, code, message, data);
        return _remote ? 1 : CallNextHookEx(0, code, message, data);
    }

    private void Raw(nint handle)
    {
        if (!_remote) return;

        uint size = (uint)Marshal.SizeOf<RawInput>();
        uint read = GetRawInputData(
            handle,
            0x10000003,
            out RawInput raw,
            ref size,
            (uint)Marshal.SizeOf<RawHeader>());

        if (read == uint.MaxValue || raw.Header.Type != 0) return;

        lock (_gate)
        {
            if (!_remote) return;

            var m = raw.Mouse;
            if ((m.Flags & 1) != 0)
            {
                Deactivate();
                Fault?.Invoke("절대 좌표 입력 장치 미지원 — 로컬 복귀");
                return;
            }

            ushort flags = (ushort)(m.Buttons & 0xFFFF);
            short delta = unchecked((short)(m.Buttons >> 16));

            for (int button = 0; button < 5; button++)
            {
                if ((flags & (1 << (button * 2))) != 0) _buttons |= (byte)(1 << button);
                if ((flags & (2 << (button * 2))) != 0) _buttons &= (byte)~(1 << button);
            }

            if ((flags & 0x400) != 0) _wheel += delta;
            if ((flags & 0x800) != 0) _pan += delta;

            int wheel = _wheel / 120;
            int pan = _pan / 120;
            _wheel %= 120;
            _pan %= 120;

            if (m.X != 0 || m.Y != 0 || flags != 0)
                Packet?.Invoke(new PointerPacket(_buttons, m.X, m.Y, wheel, pan));
        }
    }

    private sealed class Sink : NativeWindow
    {
        private readonly InputCapture _owner;

        public Sink(InputCapture owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Caption = "Con.Phone.InputSink", Parent = new nint(-3) });
        }

        protected override void WndProc(ref Message m)
        {
            try
            {
                if (m.Msg == 0xFF) _owner.Raw(m.LParam);
                if (m.Msg == 0x8001) Application.ExitThread();
            }
            catch (Exception ex)
            {
                _owner.Deactivate();
                _owner.Fault?.Invoke($"마우스 처리 오류 — 로컬 복귀: {ex.Message}");
            }

            base.WndProc(ref m);
        }

        public void Destroy() => DestroyHandle();
    }

    public void Dispose()
    {
        Deactivate();
        CancelHotkeyCapture();

        if (_sink is not null) PostMessage(_sink.Handle, 0x8001, 0, 0);
        if (Thread.CurrentThread != _thread) _thread.Join(1000);
    }

    private delegate nint Hook(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDevice { public ushort Page, Usage; public uint Flags; public nint Target; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawHeader { public uint Type, Size; public nint Device, WParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouse { public ushort Flags; public uint Buttons, RawButtons; public int X, Y; public uint Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInput { public RawHeader Header; public RawMouse Mouse; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyData { public uint Vk, Scan, Flags, Time; public nuint Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int id, Hook proc, nint module, uint thread);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(nint handle, uint command, out RawInput data, ref uint size, uint header);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
