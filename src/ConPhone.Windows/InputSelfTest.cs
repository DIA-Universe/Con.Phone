using System.Runtime.InteropServices;

namespace ConPhone.Windows;

internal static class InputSelfTest
{
    // Runs only on explicit CLI request. Always release simulated keys and local capture.
    public static void Run(bool injectOsInput = true)
    {
        var saved = new ConPhoneSettings
        {
            ClipboardSyncEnabled = true,
            MouseSpeedPercent = 125,
            HangulKeyMode = ConPhone.Core.HangulKeyMode.CtrlSpace,
            ModifierProfile = ConPhone.Core.ModifierProfile.MacCtrlCommandSwap
        };
        var restored = SettingsStore.ReadJson(System.Text.Json.JsonSerializer.Serialize(saved, SettingsStore.JsonOptions));
        if (!restored.ClipboardSyncEnabled || restored.MouseSpeedPercent != 125 ||
            restored.HangulKeyMode != saved.HangulKeyMode || restored.ModifierProfile != saved.ModifierProfile)
            throw new InvalidOperationException("Saved settings did not roundtrip; clipboard sync was reset.");
        var readValue = Enumerable.Range(0, 64).Select(x => (byte)x).ToArray();
        if (!BleClipboardService.ReadSlice(readValue, 22).SequenceEqual(readValue.Skip(22)) ||
            BleClipboardService.ReadSlice(readValue, 64).Length != 0)
            throw new InvalidOperationException("BLE long-read continuation is incorrect.");
        var input = new InputCapture();
        void Key(byte key, bool up)
        {
            if (injectOsInput)
            {
                keybd_event(key, 0, up ? 2u : 0u, 0);
                // Let Windows deliver each injected transition before changing
                // registrations/mode; keybd_event only queues input.
                WaitFor(() => false, 30);
            }
            else DispatchHook(input, key, up);
        }
        using var localProbe = injectOsInput ? null : new HotkeyWindow();
        using var returned = new ManualResetEventSlim();
        using var toggled = new AutoResetEvent(false);
        using var locked = new AutoResetEvent(false);
        var faults = new System.Collections.Concurrent.ConcurrentQueue<string>();
        input.Fault += faults.Enqueue;
        input.Emergency += returned.Set;
        input.Toggle += () => toggled.Set();
        input.LockAndReturn += () => locked.Set();
        try
        {
            input.Started.GetAwaiter().GetResult();
            if (!injectOsInput)
            {
                AssertLocalShortcutAvailable(localProbe, 0x78);
                Key(0x78, false); Key(0x78, true);
                if (WaitFor(() => returned.IsSet, 100)) throw new InvalidOperationException("Local F9 triggered emergency return.");
            }
            input.Activate();
            if (!injectOsInput) AssertRemoteShortcutReserved(localProbe, 0x78);
            Key(0x78, false);
            Key(0x78, true);
            if (!WaitFor(() => returned.IsSet) || input.IsRemote)
                throw new InvalidOperationException($"F9 did not release input through the keyboard hook. Remote={input.IsRemote}; faults={string.Join("; ", faults)}");
            AssertLocalShortcutAvailable(localProbe, 0x78);

            returned.Reset();
            input.Activate();
            Key(0xA2, false); // Held Ctrl must never prevent emergency return.
            Key(0x78, false);
            Key(0x78, true);
            Key(0xA2, true);
            if (!WaitFor(() => returned.IsSet) || input.IsRemote)
                throw new InvalidOperationException("Ctrl held during F9 prevented emergency return.");

            for (var cycle = 0; cycle < 20; cycle++)
            {
                input.RefreshHooksAsync().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                Key(0x77, false); Key(0x77, true);
                if (!WaitFor(() => toggled.WaitOne(0))) throw new InvalidOperationException($"F8 lost after return at cycle {cycle}.");
                input.Activate();
                returned.Reset();
                Key(0x78, false); Key(0x78, true);
                if (!WaitFor(() => returned.IsSet) || input.IsRemote) throw new InvalidOperationException("F9 cycle failed.");
                input.RefreshHooksAsync().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                Key(0x77, false); Key(0x77, true);
                if (!WaitFor(() => toggled.WaitOne(0))) throw new InvalidOperationException("F8 after F9 was lost.");
                input.Activate();
                Key(0xA2, false); Key(0xA4, false); Key(0x4C, false);
                Key(0x4C, true); Key(0xA4, true); Key(0xA2, true);
                if (!WaitFor(() => locked.WaitOne(0)) || input.IsRemote) throw new InvalidOperationException("Lock/return after F9/F8 was lost.");
            }

            // Exercise the real production fallback, with the low-level hook removed.
            // Windows may remove a timed-out hook without informing the app.
            input.Activate();
            var hookField = typeof(InputCapture).GetField("_keyboardHook",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            if (!UnhookWindowsHookEx((nint)hookField.GetValue(input)!))
                throw new InvalidOperationException("Could not simulate keyboard hook loss.");
            hookField.SetValue(input, (nint)0);
            returned.Reset();
            if (injectOsInput) { Key(0x78, false); Key(0x78, true); }
            else
            {
                var sink = (NativeWindow)typeof(InputCapture).GetField("_sink",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(input)!;
                SendMessage(sink.Handle, 0x0312, 0x4350, 0);
            }
            if (!WaitFor(() => returned.IsSet) || input.IsRemote)
                throw new InvalidOperationException("Remote WM_HOTKEY fallback did not return locally.");
            AssertLocalShortcutAvailable(localProbe, 0x78);
            input.RefreshHooksAsync().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();

            input.ConfigureHotkeys(SettingsStore.DefaultNextDevice(), SettingsStore.DefaultToggle(),
                new HotkeyBinding { Key = 0x79, Modifiers = HotkeyModifiers.Alt }, SettingsStore.DefaultLockAndReturn());
            AssertLocalShortcutAvailable(localProbe, 0x79, 1);
            input.Activate();
            returned.Reset();
            Key(0xA4, false); Key(0x79, false); Key(0x79, true); Key(0xA4, true);
            if (!WaitFor(() => returned.IsSet) || input.IsRemote)
                throw new InvalidOperationException("Custom Alt+F10 emergency failed.");
            AssertLocalShortcutAvailable(localProbe, 0x79, 1);
            Console.WriteLine(injectOsInput
                ? "PASS: OS-injected keys and remote WM_HOTKEY fallback."
                : "PASS: native shortcut ownership, simulated hook dispatch and actual WM_HOTKEY handler; OS key injection not tested.");
        }
        finally
        {
            input.Deactivate();
            Key(0x78, true);
            Key(0xA2, true);
            Key(0xA4, true);
            Key(0x4C, true);
            Key(0x77, true);
            Key(0x79, true);
            input.Dispose();
        }

    }

    private static bool WaitFor(Func<bool> signaled, int milliseconds = 2000)
    {
        var until = Environment.TickCount64 + milliseconds;
        do
        {
            Application.DoEvents();
            if (signaled()) return true;
            Thread.Sleep(5);
        } while (Environment.TickCount64 < until);
        return false;
    }

    private static void AssertLocalShortcutAvailable(HotkeyWindow? window, uint key, uint modifiers = 0)
    {
        if (window is null) return;
        if (!RegisterHotKey(window.Handle, 1, 0x4000 | modifiers, key))
            throw new InvalidOperationException("A phone-only shortcut is still globally reserved while input is local.");
        UnregisterHotKey(window.Handle, 1);
    }

    private static void AssertRemoteShortcutReserved(HotkeyWindow? window, uint key)
    {
        if (window is null) return;
        if (RegisterHotKey(window.Handle, 1, 0x4000, key))
        {
            UnregisterHotKey(window.Handle, 1);
            throw new InvalidOperationException("Remote emergency fallback was not registered.");
        }
    }

    private static void DispatchHook(InputCapture input, byte key, bool up)
    {
        // Deterministic scope regression for CI/noninteractive desktops. Invoke the
        // production hook with KBDLLHOOKSTRUCT, without typing into the user's app.
        var data = Marshal.AllocHGlobal(24);
        try
        {
            for (var offset = 0; offset < 24; offset += 4) Marshal.WriteInt32(data, offset, 0);
            Marshal.WriteInt32(data, key);
            Marshal.WriteInt32(data, 8, 0x10 | (up ? 0x80 : 0));
            input.OnInputThreadAsync(() => typeof(InputCapture).GetMethod("KeyboardHook",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(input, [0, (nint)(up ? 0x101 : 0x100), data])).GetAwaiter().GetResult();
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public bool Received { get; private set; }
        public HotkeyWindow() => CreateHandle(new CreateParams { Parent = new nint(-3) });
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312) Received = true;
            base.WndProc(ref m);
        }
        public void Dispose() => DestroyHandle();
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
