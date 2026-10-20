using System.ComponentModel;
using System.Runtime.InteropServices;
using ConPhone.Core;

namespace ConPhone.Windows;

// Hooks run on their own message-loop thread. They never await Bluetooth or touch the UI.
public sealed partial class InputCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly KeyboardState _keys = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<int, short> _readKeyState;

    private Hook? _keyboardDelegate, _mouseDelegate;
    private nint _keyboardHook, _mouseHook;
    private Sink? _sink;

    private bool _remote => (Generation & 1) != 0;
    private HotkeyBinding _nextDeviceHotkey = SettingsStore.DefaultNextDevice();
    private HotkeyBinding _toggleHotkey = SettingsStore.DefaultToggle();
    private HotkeyBinding _emergencyHotkey = SettingsStore.DefaultEmergency();
    private HotkeyBinding _lockAndReturnHotkey = SettingsStore.DefaultLockAndReturn();
    private HangulKeyMode _hangulKeyMode = HangulKeyMode.RightAlt;
    private ModifierProfile _modifierProfile = ModifierProfile.Standard;

    private Action<HotkeyBinding>? _captureCallback;
    private readonly HashSet<int> _downKeys = [];
    private readonly HashSet<int> _suppressUntilUp = [];
    private readonly HashSet<int> _localPassthroughUntilUp = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<TaskCompletionSource> _hookRefreshes = new();
    private volatile bool _disposed;

    private byte _buttons;
    private int _wheel, _pan;

    public event Action<CapturedInput>? Packet;
    public event Action? NextDevice;
    public event Action? Toggle;
    public event Action? Emergency;
    public event Action? LockAndReturn;
    public event Action<ConsumerAction>? Consumer;
    public event Action<string>? Fault;

    public bool IsRemote => _remote;
    public bool IsCapturingHotkey => Volatile.Read(ref _captureCallback) is not null;
    public Task Started => _started.Task;

    public InputCapture() : this(GetAsyncKeyState) { }

    internal InputCapture(Func<int, short> readKeyState)
    {
        _readKeyState = readKeyState;
        _dispatchTask = Task.Run(DispatchInputAsync);
        _dispatchWatchdog = new System.Threading.Timer(CheckDispatchDeadline, null, 100, 100);
        _thread = new Thread(Run) { IsBackground = true, Name = "Con.Phone.Input" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void ConfigureHotkeys(
        HotkeyBinding nextDevice,
        HotkeyBinding toggle,
        HotkeyBinding emergency,
        HotkeyBinding lockAndReturn)
    {
        var bindings = new[] { nextDevice, toggle, emergency, lockAndReturn };
        if (bindings.Any(binding => !HotkeyCatalog.IsAllowedKey(binding.Key)))
            throw new ArgumentOutOfRangeException(nameof(toggle), "지원하지 않는 단축키입니다.");

        for (var i = 0; i < bindings.Length; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (HotkeyCatalog.Same(bindings[i], bindings[j]))
                    throw new InvalidOperationException(
                        "각 동작의 단축키 조합은 서로 다르게 지정해 주세요.");
            }
        }

        PostInput(() =>
        {
            _nextDeviceHotkey = nextDevice.Clone();
            _toggleHotkey = toggle.Clone();
            _emergencyHotkey = emergency.Clone();
            _lockAndReturnHotkey = lockAndReturn.Clone();
        });
    }

    public void ConfigureKeyboard(
        HangulKeyMode hangulKeyMode,
        ModifierProfile modifierProfile)
    {
        PostInput(() =>
        {
            _hangulKeyMode = hangulKeyMode;
            _modifierProfile = modifierProfile;
        });
    }

    // Capturing is performed inside the low-level hook, not in a TextBox. This prevents the
    // currently configured shortcut or Windows from stealing the key combination being edited.
    public void BeginHotkeyCapture(Action<HotkeyBinding> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (_remote) throw new InvalidOperationException("먼저 PC로 복귀한 뒤 단축키를 변경해 주세요.");
        PostInput(() =>
        {
            if (_remote) return;
            _captureCallback = completed;
            _suppressUntilUp.Clear();
        });
    }

    public void CancelHotkeyCapture()
    {
        PostInput(() => _captureCallback = null);
    }

    private void Run()
    {
        try
        {
            DesktopGeometry.UsePhysicalCoordinates();
            _sink = new Sink(this);
            var devices = new[] { new RawDevice { Page = 1, Usage = 2, Flags = 0x100, Target = _sink.Handle } };
            if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawDevice>())) throw new Win32Exception();

            _keyboardDelegate = KeyboardHook;
            _mouseDelegate = MouseHook;
            _keyboardHook = SetWindowsHookEx(13, _keyboardDelegate, GetModuleHandle(null), 0);
            _mouseHook = SetWindowsHookEx(14, _mouseDelegate, GetModuleHandle(null), 0);
            if (_keyboardHook == 0 || _mouseHook == 0) throw new Win32Exception();

            _started.TrySetResult();
            if (!_commands.IsEmpty) PostMessage(_sink.Handle, 0x8004, 0, 0);
            Application.Run();
        }
        catch (Exception ex)
        {
            _started.TrySetException(ex);
            Notify(() => Fault?.Invoke($"입력 훅 시작 실패: {ex.GetType().Name}"));
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

    public void Activate(bool preserveLocalModifierUps = true)
    {
        OnInputThreadAsync(() =>
        {
            if (!TryActivateCore(preserveLocalModifierUps, out var blocked))
                throw new InputTransitionBlockedException(blocked);
            return true;
        }).GetAwaiter().GetResult();
    }

    public async Task<bool> ActivateWhenReleasedAsync(
        Func<bool> isCurrent, Action<string>? waiting, CancellationToken token,
        bool preserveLocalModifierUps = true)
    {
        var deadline = Environment.TickCount64 + 2000;
        string? previous = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!isCurrent()) return false;
            var attempt = await OnInputThreadAsync(() =>
            {
                if (!isCurrent()) return (Active: false, Blocked: Array.Empty<int>());
                var active = TryActivateCore(preserveLocalModifierUps, out var keys);
                return (Active: active, Blocked: keys);
            });
            if (attempt.Active) return true;
            if (!isCurrent()) return false;
            var blocked = attempt.Blocked;
            if (Environment.TickCount64 >= deadline)
                throw new InputTransitionBlockedException(blocked);
            var message = $"전환 대기 · {InputTransitionBlockedException.Describe(blocked)}을(를) 놓으면 전환합니다.";
            if (message != previous) { previous = message; waiting?.Invoke(message); }
            await Task.Delay(25, token);
        }
    }

    private bool TryActivateCore(bool preserveLocalModifierUps, out int[] blocked, bool allowSwitchChord = true)
    {
        var before = Generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started.Task.IsCompletedSuccessfully)
                throw new InvalidOperationException("입력 수집이 준비되지 않았습니다.");

            var allowed = new HashSet<int>(
                HotkeyPhysicalKeys(_toggleHotkey));
            allowed.UnionWith(
                HotkeyPhysicalKeys(_nextDeviceHotkey));
            if (!allowSwitchChord) allowed.Clear();
            var pressed = new List<int>();
            for (int vk = 1; vk < 255; vk++)
                if ((_readKeyState(vk) & 0x8000) != 0) pressed.Add(vk);
            blocked = pressed.Where(vk => !allowed.Contains(vk)).ToArray();
            if (blocked.Length != 0) return false;

            // Commit mode/keyup bookkeeping only after a complete successful
            // snapshot. A blocked attempt leaves input local and state intact.
            _localPassthroughUntilUp.Clear();
            foreach (var vk in pressed)
            {
                // Modifier down events were delivered to Windows while input was local. Once remote
                // mode starts their key-up events must still reach Windows or Ctrl/Alt can appear stuck.
                if (preserveLocalModifierUps &&
                    vk != _toggleHotkey.Key &&
                    vk != _nextDeviceHotkey.Key &&
                    IsPhysicalModifier(vk))
                    _localPassthroughUntilUp.Add(vk);
            }

            _keys.Clear();
            _buttons = 0;
            _wheel = 0;
            _pan = 0;
            if ((before & 1) != 0 || Interlocked.CompareExchange(ref _captureState, before + 1, before) != before)
            {
                blocked = [];
                return false;
            }
        }
        UpdateRemoteShortcutFallbacks();
        return true;
    }

    public void Deactivate()
    {
        // Fail open before any lock, message, UI or Bluetooth work. Cleanup is
        // ordered on the owner thread, never a synchronous cross-thread SendMessage.
        long before;
        do { before = Generation; }
        while (!DeactivateIfGeneration(before));
    }

    public bool DeactivateIfGeneration(long before)
    {
        if (Interlocked.CompareExchange(ref _captureState, (before | 1) + 1, before) != before) return false;
        _calibrating = false;
        PostInput(() =>
        {
            if (_remote) return; // A newer activation owns its own clean state.
            _keys.Clear();
            _buttons = 0;
            _wheel = 0;
            _pan = 0;
            _edgeEntry.Disarm();
            UpdateRemoteShortcutFallbacks();
        });
        return true;
    }

    private void UpdateRemoteShortcutFallbacks()
    {
        // Synchronous on the owning input thread: local return must release the OS
        // registration before the next key press, even if the UI/Bluetooth is busy.
        // Never call while holding _gate from another thread.
        if (_sink is not { } sink || sink.Handle == 0) return;
        if (Thread.CurrentThread == _thread) { sink.UpdateRemoteShortcuts(); return; }
        PostMessage(sink.Handle, 0x8003, 0, 0);
    }

    public async void RefreshHooks()
    {
        try { await RefreshHooksAsync().ConfigureAwait(false); }
        catch (Exception ex) { if (!_disposed) Fault?.Invoke($"입력 훅 복구 실패: {ex.Message}"); }
    }

    public Task RefreshHooksAsync()
    {
        if (_disposed) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _hookRefreshes.Enqueue(completion);
        if (_sink is null || !PostMessage(_sink.Handle, 0x8002, 0, 0))
            completion.TrySetException(new InvalidOperationException("입력 훅 스레드가 준비되지 않았습니다."));
        return completion.Task;
    }

    private void ReinstallHooks()
    {
        // Windows may silently remove a low-level hook after a timeout/suspend.
        // Restore it on its owning thread; never install hooks on the UI thread.
        if (_remote) return;
        if (_keyboardHook != 0) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = SetWindowsHookEx(13, _keyboardDelegate!, GetModuleHandle(null), 0);
        _mouseHook = SetWindowsHookEx(14, _mouseDelegate!, GetModuleHandle(null), 0);
        if (_keyboardHook == 0 || _mouseHook == 0) throw new Win32Exception();
        lock (_gate)
        {
            // A missed key-up must not permanently hide subsequent shortcut presses.
            _downKeys.Clear();
            _physicalMouseButtons = 0;
            foreach (var key in new[] { 0x01, 0x02, 0x04, 0x05, 0x06 })
                if ((_readKeyState(key) & 0x8000) != 0) _physicalMouseButtons = 1;
            UpdateHeldInputs();
            _suppressUntilUp.RemoveWhere(key => (_readKeyState(key) & 0x8000) == 0);
            _localPassthroughUntilUp.RemoveWhere(key => (_readKeyState(key) & 0x8000) == 0);
        }
    }

    private nint KeyboardHook(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(0, code, message, data);

        try
        {
            var k = Marshal.PtrToStructure<KeyData>(data);
            bool injected = (k.Flags & 0x10) != 0;

            int key = (int)k.Vk;
            bool up = (k.Flags & 0x80) != 0;
            bool firstDown;
            Action<HotkeyBinding>? capturedCallback = null;
            HotkeyBinding? capturedBinding = null;
            HotkeyBinding nextDevice, toggle, emergency, lockAndReturn;

            lock (_gate)
            {
                firstDown = !up && _downKeys.Add(key);
                if (up) _downKeys.Remove(key);
                UpdateHeldInputs();

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

                nextDevice = _nextDeviceHotkey.Clone();
                toggle = _toggleHotkey.Clone();
                emergency = _emergencyHotkey.Clone();
                lockAndReturn = _lockAndReturnHotkey.Clone();
            }

            if (capturedBinding is not null && capturedCallback is not null)
            {
                Notify(() => capturedCallback(capturedBinding));
                return 1;
            }

            if (!up && firstDown)
            {
                HotkeyModifiers modifiers;
                lock (_gate) modifiers = ModifiersFromDownKeysLocked();

                if (Matches(nextDevice, key, modifiers))
                {
                    lock (_gate)
                    {
                        _suppressUntilUp.Add(key);

                        // While Windows owns input, let already-held physical modifiers
                        // complete locally. While the phone owns input, the switch flow
                        // sends explicit release reports to the old target instead.
                        if (!_remote)
                        {
                            foreach (var down in _downKeys)
                                if (down != key && IsPhysicalModifier(down))
                                    _localPassthroughUntilUp.Add(down);
                        }
                    }
                    Notify(() => NextDevice?.Invoke());
                    return 1;
                }

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
                    if (_remote && _calibrating)
                    {
                        Emit(new CalibrationMarkPacket());
                        return 1;
                    }
                    // Returning to the PC must not wait for the UI/action queue or Bluetooth.
                    if (_remote)
                    {
                        Deactivate();
                        Notify(() => Emergency?.Invoke());
                    }
                    else Notify(() => Toggle?.Invoke());
                    return 1;
                }

                // Emergency must work even while Ctrl/Shift is held (or a key-up was missed).
                if (_remote && key == emergency.Key &&
                    (modifiers & emergency.Modifiers) == emergency.Modifiers)
                {
                    lock (_gate) _suppressUntilUp.Add(key);
                    Deactivate();
                    Notify(() => Emergency?.Invoke());
                    return 1;
                }

                if (_remote && Matches(lockAndReturn, key, modifiers))
                {
                    lock (_gate) _suppressUntilUp.Add(key);
                    Deactivate();
                    Notify(() => LockAndReturn?.Invoke());
                    return 1;
                }
            }

            lock (_gate)
            {
                // Keyboard utilities may synthesize shortcut keys. Honor those above,
                // but never forward synthetic text/input back to the phone.
                if (injected) return CallNextHookEx(0, code, message, data);
                if (!_remote) return CallNextHookEx(0, code, message, data);

                if (TryConsumerAction(key, out var consumerAction))
                {
                    if (!up && firstDown)
                        Notify(() => Consumer?.Invoke(consumerAction));
                    return 1;
                }

                var isHangulKey =
                    key == 0x15 ||
                    ((int)k.Scan & 0xFF) == 0xF2;

                if (isHangulKey)
                {
                    switch (_hangulKeyMode)
                    {
                        case HangulKeyMode.CtrlSpace:
                        case HangulKeyMode.ShiftSpace:
                            if (!up && firstDown)
                            {
                                var modifier = _hangulKeyMode == HangulKeyMode.CtrlSpace
                                    ? (byte)0xE0
                                    : (byte)0xE1;
                                Emit(new KeyboardPacket(_keys.SnapshotWith(modifier, 0x2C)));
                                Emit(new KeyboardPacket(_keys.Snapshot()));
                            }
                            return 1;

                        case HangulKeyMode.RightAlt:
                        {
                            var rightAlt = _keys.Update(0xE6, !up);
                            if (rightAlt is not null)
                                Emit(new KeyboardPacket(rightAlt));
                            return 1;
                        }

                        default:
                        {
                            var lang1 = _keys.Update(0x90, !up);
                            if (lang1 is not null)
                                Emit(new KeyboardPacket(lang1));
                            return 1;
                        }
                    }
                }

                var usage = Hid.Usage(
                    key,
                    (int)k.Scan,
                    (k.Flags & 1) != 0);
                usage = Hid.MapModifierUsage(usage, _modifierProfile);

                var report = _keys.Update(usage, !up);

                if (report is not null) Emit(new KeyboardPacket(report));
                return _remote ? 1 : CallNextHookEx(0, code, message, data);
            }
        }
        catch (Exception ex)
        {
            Deactivate();
            Notify(() => Fault?.Invoke($"키보드 처리 오류 — PC 복귀: {ex.GetType().Name}"));
            return CallNextHookEx(0, code, message, data);
        }
    }

    private static bool TryConsumerAction(int key, out ConsumerAction action)
    {
        switch (key)
        {
            case 0xAD:
                action = ConsumerAction.Mute;
                return true;
            case 0xAE:
                action = ConsumerAction.VolumeDown;
                return true;
            case 0xAF:
                action = ConsumerAction.VolumeUp;
                return true;
            case 0xB0:
                action = ConsumerAction.NextTrack;
                return true;
            case 0xB1:
                action = ConsumerAction.PreviousTrack;
                return true;
            case 0xB3:
                action = ConsumerAction.PlayPause;
                return true;
            default:
                action = default;
                return false;
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
            var m = raw.Mouse;
            var physicalFlags = m.Buttons & 0xFFFF;
            for (var i = 0; i < 5; i++)
            {
                if ((physicalFlags & (1u << (i * 2))) != 0) _physicalMouseButtons |= (byte)(1 << i);
                if ((physicalFlags & (2u << (i * 2))) != 0) _physicalMouseButtons &= (byte)~(1 << i);
            }
            UpdateHeldInputs();
            if (!_remote)
            {
                if (_automaticEnabled && (m.Flags & 1) == 0 && DesktopGeometry.Cursor(out var point))
                {
                    var edge = _edgeEntry.Observe(_screens, _phoneSide, point.X, point.Y, m.X,
                        !_automaticReady || _captureCallback is not null || HasHeldInput);
                    if (edge is not null)
                    {
                        var generation = Generation;
                        Notify(() => EdgeEntered?.Invoke(generation, edge));
                    }
                }
                return;
            }
            if ((m.Flags & 1) != 0)
            {
                Deactivate();
                Notify(() => Fault?.Invoke("절대 좌표 입력 장치 미지원 — PC 복귀"));
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
                Emit(new PointerPacket(_buttons, m.X, m.Y, wheel, pan));
        }
    }

    private sealed class Sink : NativeWindow
    {
        private readonly InputCapture _owner;
        private const int EmergencyId = 0x4350, LockId = 0x4353;

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
                if (m.Msg == 0x8003) UpdateRemoteShortcuts();
                if (m.Msg == 0x8004)
                    while (_owner._commands.TryDequeue(out var command)) command();
                if (m.Msg == 0x0312 && _owner._remote && !_owner.IsCapturingHotkey)
                {
                    if ((int)m.WParam == EmergencyId)
                    {
                        _owner.Deactivate();
                        _owner.Notify(() => _owner.Emergency?.Invoke());
                    }
                    else if ((int)m.WParam == LockId)
                    {
                        _owner.Deactivate();
                        _owner.Notify(() => _owner.LockAndReturn?.Invoke());
                    }
                }
                if (m.Msg == 0x8002)
                {
                    while (_owner._hookRefreshes.TryDequeue(out var completion))
                    {
                        try { _owner.ReinstallHooks(); completion.TrySetResult(); }
                        catch (Exception ex) { completion.TrySetException(ex); }
                    }
                }
            }
            catch (Exception ex)
            {
                _owner.Deactivate();
                _owner.Notify(() => _owner.Fault?.Invoke($"마우스 처리 오류 — PC 복귀: {ex.GetType().Name}"));
            }

            base.WndProc(ref m);
        }

        public void UpdateRemoteShortcuts()
        {
            UnregisterHotKey(Handle, EmergencyId);
            UnregisterHotKey(Handle, LockId);
            lock (_owner._gate)
            {
                if (!_owner._remote) return;
                Register(EmergencyId, _owner._emergencyHotkey);
                Register(LockId, _owner._lockAndReturnHotkey);
            }
        }

        private void Register(int id, HotkeyBinding binding)
        {
            uint modifiers = 0x4000;
            if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)) modifiers |= 1;
            if (binding.Modifiers.HasFlag(HotkeyModifiers.Control)) modifiers |= 2;
            if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift)) modifiers |= 4;
            if (binding.Modifiers.HasFlag(HotkeyModifiers.Windows)) modifiers |= 8;
            // The low-level hook still handles remote return if another app owns the chord.
            RegisterHotKey(Handle, id, modifiers, (uint)binding.Key);
        }

        public void Destroy()
        {
            UnregisterHotKey(Handle, EmergencyId);
            UnregisterHotKey(Handle, LockId);
            DestroyHandle();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dispatchWatchdog?.Dispose();
        _dispatch.Writer.TryComplete();
        while (_hookRefreshes.TryDequeue(out var completion)) completion.TrySetCanceled();
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

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);
}
