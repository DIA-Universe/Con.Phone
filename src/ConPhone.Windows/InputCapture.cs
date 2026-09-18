using System.ComponentModel;
using System.Runtime.InteropServices;
using ConPhone.Core;

namespace ConPhone.Windows;

// A dedicated message-loop thread. No BLE/clipboard/UI calls and no event invocation under _gate.
public sealed class InputCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly KeyboardRouter _router = new();
    private readonly Thread _thread;
    private readonly bool _installHooks;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Hook? _keyboardDelegate, _mouseDelegate;
    private nint _keyboardHook, _mouseHook;
    private Sink? _sink;
    private volatile bool _remote;
    private volatile bool _disposed;
    private Action<KeyChord?>? _recorder;
    private byte _buttons;
    private int _wheel;
    public event Action<InputPacket>? Packet;
    public event Action<InputCommand>? Command;
    public event Action<string>? Fault;
    public bool IsRemote => _remote;
    public Task Started => _started.Task;
    public bool Released { get { lock (_gate) return _router.Released; } }

    public InputCapture(bool installHooks = true)
    {
        _installHooks = installHooks;
        _thread = new Thread(Run) { IsBackground = true, Name = "ConPhone.Input" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
    }
    public void Configure(ConPhoneSettings settings)
    {
        lock (_gate) _router.Configure(settings.ToggleHotkey.Chord, settings.EmergencyHotkey.Chord,
            settings.LockAndReturnHotkey.Chord, settings.EnablePhoneLock);
    }
    public void BeginRecording(Action<KeyChord?> receive)
    {
        Deactivate();
        lock (_gate) { _recorder = receive; _router.SetRecording(true); }
    }
    public void EndRecording()
    {
        lock (_gate) { _recorder = null; _router.SetRecording(false); }
    }
    public bool Activate()
    {
        if (_disposed || !_started.Task.IsCompletedSuccessfully) return false;
        if (_installHooks)
            for (int vk = 1; vk < 255; vk++)
                if ((GetAsyncKeyState(vk) & 0x8000) != 0) return false;
        lock (_gate)
        {
            if (!_router.TryEnterPhone()) return false;
            _buttons = 0; _wheel = 0; _remote = true; return true;
        }
    }
    public void Deactivate()
    {
        _remote = false; // Mouse pass-through never waits for BLE or UI.
        lock (_gate) { _router.StopRemote(); _buttons = 0; _wheel = 0; }
    }
    private void Run()
    {
        try
        {
            _sink = new Sink(this);
            if (_installHooks)
            {
                if (!RegisterRawInputDevices([new RawDevice { Page = 1, Usage = 2, Flags = 0x100, Target = _sink.Handle }], 1, (uint)Marshal.SizeOf<RawDevice>())) throw new Win32Exception();
                _keyboardDelegate = KeyboardHook; _mouseDelegate = MouseHook;
                _keyboardHook = SetWindowsHookEx(13, _keyboardDelegate, GetModuleHandle(null), 0);
                _mouseHook = SetWindowsHookEx(14, _mouseDelegate, GetModuleHandle(null), 0);
                if (_keyboardHook == 0 || _mouseHook == 0) throw new Win32Exception();
            }
            _started.TrySetResult(); Application.Run();
        }
        catch (Exception ex) { _started.TrySetException(ex); Fault?.Invoke($"입력 스레드: {ex.GetType().Name} 0x{ex.HResult:X8}"); }
        finally
        {
            Deactivate();
            if (_keyboardHook != 0) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
            if (_installHooks) RegisterRawInputDevices([new RawDevice { Page = 1, Usage = 2, Flags = 1 }], 1, (uint)Marshal.SizeOf<RawDevice>());
            _sink?.Destroy();
        }
    }
    private nint KeyboardHook(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(0, code, message, data);
        try
        {
            var k = Marshal.PtrToStructure<KeyData>(data);
            if ((k.Flags & 0x10) != 0) return CallNextHookEx(0, code, message, data);
            return Route((int)k.Vk, (k.Flags & 0x80) != 0, (int)k.Scan, (k.Flags & 1) != 0)
                ? 1 : CallNextHookEx(0, code, message, data);
        }
        catch (Exception ex) { Deactivate(); Fault?.Invoke($"키보드 처리: {ex.GetType().Name}"); return CallNextHookEx(0, code, message, data); }
    }
    private bool Route(int key, bool up, int scan = 0, bool extended = false)
    {
        KeyRoute result; Action<KeyChord?>? recorder;
        lock (_gate)
        {
            result = _router.Process(key, up, scan, extended);
            _remote = _router.Remote; recorder = _recorder;
        }
        if (result.Captured is { } chord) recorder?.Invoke(chord);
        if (result.Command == InputCommand.CancelRecording) recorder?.Invoke(null);
        else if (result.Command != InputCommand.None) Command?.Invoke(result.Command);
        if (result.Report is { } bytes && _remote) Packet?.Invoke(new KeyboardPacket(bytes));
        return result.Suppress;
    }
    internal bool TestKey(int key, bool up)
    {
        if (_installHooks) throw new InvalidOperationException("Test injection is available only without hardware hooks.");
        return Route(key, up);
    }
    private nint MouseHook(int code, nint message, nint data)
    {
        if (code < 0 || !_remote) return CallNextHookEx(0, code, message, data);
        var m = Marshal.PtrToStructure<MouseData>(data);
        return (m.Flags & 1) == 0 && _remote ? 1 : CallNextHookEx(0, code, message, data);
    }
    private void Raw(nint handle)
    {
        if (!_remote) return;
        uint size = (uint)Marshal.SizeOf<RawInput>();
        if (GetRawInputData(handle, 0x10000003, out var raw, ref size, (uint)Marshal.SizeOf<RawHeader>()) == uint.MaxValue || raw.Header.Type != 0) return;
        if ((raw.Mouse.Flags & 1) != 0) { Deactivate(); Fault?.Invoke("절대 좌표 입력은 지원하지 않습니다. 노트북으로 복귀했습니다."); return; }
        PointerPacket? packet = null;
        lock (_gate)
        {
            if (!_remote) return;
            var m = raw.Mouse;
            ushort flags = (ushort)m.Buttons;
            for (int b = 0; b < 3; b++)
            {
                if ((flags & (1 << (b * 2))) != 0) _buttons |= (byte)(1 << b);
                if ((flags & (2 << (b * 2))) != 0) _buttons &= (byte)~(1 << b);
            }
            if ((flags & 0x400) != 0) _wheel += unchecked((short)(m.Buttons >> 16));
            int wheel = _wheel / 120; _wheel %= 120;
            if (m.X != 0 || m.Y != 0 || flags != 0) packet = new(_buttons, m.X, m.Y, wheel);
        }
        if (packet is not null) Packet?.Invoke(packet);
    }
    private sealed class Sink : NativeWindow
    {
        private readonly InputCapture _owner;
        public Sink(InputCapture owner) { _owner = owner; CreateHandle(new CreateParams { Caption = "ConPhone.InputSink", Parent = new nint(-3) }); }
        protected override void WndProc(ref Message m)
        {
            try { if (m.Msg == 0xFF) _owner.Raw(m.LParam); if (m.Msg == 0x8001) Application.ExitThread(); }
            catch (Exception ex) { _owner.Deactivate(); _owner.Fault?.Invoke($"마우스 처리: {ex.GetType().Name}"); }
            base.WndProc(ref m);
        }
        public void Destroy() => DestroyHandle();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; Deactivate();
        if (_sink is { } s) PostMessage(s.Handle, 0x8001, 0, 0);
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
