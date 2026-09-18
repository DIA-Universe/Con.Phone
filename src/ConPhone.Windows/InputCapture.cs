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
    private volatile int _toggleKey = 0x77;
    private volatile int _emergencyKey = 0x78;
    private byte _buttons;
    private int _wheel, _pan;
    public event Action<InputPacket>? Packet;
    public event Action? Toggle;
    public event Action? Emergency;
    public event Action<string>? Fault;
    public bool IsRemote => _remote;
    public Task Started => _started.Task;

    public void ConfigureHotkeys(HotkeyBinding toggle, HotkeyBinding emergency)
    {
        if (!HotkeyCatalog.IsAllowedKey(toggle.Key) || !HotkeyCatalog.IsAllowedKey(emergency.Key))
            throw new ArgumentOutOfRangeException(nameof(toggle), "지원하지 않는 단축키입니다.");
        if (toggle.Key == emergency.Key)
            throw new InvalidOperationException("입력 전환 키와 비상 복귀 키는 서로 달라야 합니다.");
        // 0.1.6 UI intentionally uses single-key hotkeys. The settings model keeps modifiers for future versions.
        _toggleKey = toggle.Key;
        _emergencyKey = emergency.Key;
    }

    public InputCapture()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Con.Phone.Input" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }
    private void Run()
    {
        try
        {
            _sink = new Sink(this);
            var devices = new[] { new RawDevice { Page = 1, Usage = 2, Flags = 0x100, Target = _sink.Handle } };
            if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawDevice>())) throw new Win32Exception();
            _keyboardDelegate = KeyboardHook; _mouseDelegate = MouseHook;
            _keyboardHook = SetWindowsHookEx(13, _keyboardDelegate, GetModuleHandle(null), 0);
            _mouseHook = SetWindowsHookEx(14, _mouseDelegate, GetModuleHandle(null), 0);
            if (_keyboardHook == 0 || _mouseHook == 0) throw new Win32Exception();
            _started.TrySetResult();
            Application.Run();
        }
        catch (Exception ex) { _started.TrySetException(ex); Fault?.Invoke("입력 훅 시작 실패"); }
        finally
        {
            Deactivate();
            if (_keyboardHook != 0) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
            if (_sink is not null)
            {
                RegisterRawInputDevices([new RawDevice { Page = 1, Usage = 2, Flags = 1, Target = 0 }], 1, (uint)Marshal.SizeOf<RawDevice>());
                _sink.Destroy();
            }
        }
    }
    public void Activate()
    {
        lock (_gate)
        {
            if (!_started.Task.IsCompletedSuccessfully) throw new InvalidOperationException("입력 수집이 준비되지 않았습니다.");
            var toggleKey = _toggleKey;
            var emergencyKey = _emergencyKey;
            for (int vk = 1; vk < 255; vk++)
                if (vk != toggleKey && vk != emergencyKey && (GetAsyncKeyState(vk) & 0x8000) != 0)
                    throw new InvalidOperationException("누르고 있는 키와 마우스 버튼을 모두 놓은 후 입력 전환 키를 눌러 주세요.");
            _keys.Clear(); _buttons = 0; _wheel = 0; _pan = 0; _remote = true;
        }
    }
    public void Deactivate()
    {
        lock (_gate) { _remote = false; _keys.Clear(); _buttons = 0; _wheel = 0; _pan = 0; }
    }
    private nint KeyboardHook(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(0, code, message, data);
        try
        {
            var k = Marshal.PtrToStructure<KeyData>(data);
            if ((k.Flags & 0x10) != 0) return CallNextHookEx(0, code, message, data);
            bool up = (k.Flags & 0x80) != 0;
            var toggleKey = (uint)_toggleKey;
            var emergencyKey = (uint)_emergencyKey;
            if (k.Vk == toggleKey || k.Vk == emergencyKey)
            {
                if (up)
                {
                    if (k.Vk == emergencyKey) { Deactivate(); Emergency?.Invoke(); }
                    else Toggle?.Invoke();
                }
                return 1;
            }
            lock (_gate)
            {
                if (!_remote) return CallNextHookEx(0, code, message, data);
                var report = _keys.Update(Hid.Usage((int)k.Vk, (int)k.Scan, (k.Flags & 1) != 0), !up);
                if (report is not null) Packet?.Invoke(new KeyboardPacket(report));
                return _remote ? 1 : CallNextHookEx(0, code, message, data);
            }
        }
        catch (Exception) { Deactivate(); Fault?.Invoke("키보드 처리 오류 — 로컬 복귀"); return CallNextHookEx(0, code, message, data); }
    }
    private nint MouseHook(int code, nint message, nint data)
    {
        if (code < 0 || !_remote) return CallNextHookEx(0, code, message, data);
        var m = Marshal.PtrToStructure<MouseData>(data);
        if ((m.Flags & 1) != 0) return CallNextHookEx(0, code, message, data);
        // Buttons and movement are emitted together by Raw Input, preserving their order.
        return _remote ? 1 : CallNextHookEx(0, code, message, data);
    }
    private void Raw(nint handle)
    {
        if (!_remote) return;
        uint size = (uint)Marshal.SizeOf<RawInput>();
        uint read = GetRawInputData(handle, 0x10000003, out RawInput raw, ref size, (uint)Marshal.SizeOf<RawHeader>());
        if (read == uint.MaxValue || raw.Header.Type != 0) return;
        lock (_gate)
        {
            if (!_remote) return;
            var m = raw.Mouse;
            if ((m.Flags & 1) != 0) { Deactivate(); Fault?.Invoke("절대 좌표 입력 장치 미지원 — 로컬 복귀"); return; }
            ushort flags = (ushort)(m.Buttons & 0xFFFF);
            short delta = unchecked((short)(m.Buttons >> 16));
            for (int button = 0; button < 5; button++)
            {
                if ((flags & (1 << (button * 2))) != 0) _buttons |= (byte)(1 << button);
                if ((flags & (2 << (button * 2))) != 0) _buttons &= (byte)~(1 << button);
            }
            if ((flags & 0x400) != 0) _wheel += delta;
            if ((flags & 0x800) != 0) _pan += delta;
            int wheel = _wheel / 120, pan = _pan / 120;
            _wheel %= 120; _pan %= 120;
            if (m.X != 0 || m.Y != 0 || flags != 0)
                Packet?.Invoke(new PointerPacket(_buttons, m.X, m.Y, wheel, pan));
        }
    }
    private sealed class Sink : NativeWindow
    {
        private readonly InputCapture _owner;
        public Sink(InputCapture owner) { _owner = owner; CreateHandle(new CreateParams { Caption = "Con.Phone.InputSink", Parent = new nint(-3) }); }
        protected override void WndProc(ref Message m)
        {
            try
            {
                if (m.Msg == 0xFF) _owner.Raw(m.LParam);
                if (m.Msg == 0x8001) Application.ExitThread();
            }
            catch (Exception) { _owner.Deactivate(); _owner.Fault?.Invoke("마우스 처리 오류 — 로컬 복귀"); }
            base.WndProc(ref m);
        }
        public void Destroy() => DestroyHandle();
    }
    public void Dispose()
    {
        Deactivate();
        if (_sink is not null) PostMessage(_sink.Handle, 0x8001, 0, 0);
        if (Thread.CurrentThread != _thread) _thread.Join(1000);
    }
    private delegate nint Hook(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawHeader { public uint Type, Size; public nint Device, WParam; }
    [StructLayout(LayoutKind.Sequential)] private struct RawMouse { public ushort Flags; public uint Buttons, RawButtons; public int X, Y; public uint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInput { public RawHeader Header; public RawMouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyData { public uint Vk, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, Hook proc, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(nint handle, uint command, out RawInput data, ref uint size, uint header);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
