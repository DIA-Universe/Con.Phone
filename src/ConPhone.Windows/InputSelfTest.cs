using System.Runtime.InteropServices;

namespace ConPhone.Windows;

internal static class InputSelfTest
{
    // Runs only on explicit CLI request. Always release simulated keys and local capture.
    public static void Run()
    {
        var input = new InputCapture();
        using var returned = new ManualResetEventSlim();
        input.Emergency += returned.Set;
        try
        {
            input.Started.GetAwaiter().GetResult();
            input.Activate();
            Key(0x78, false);
            Key(0x78, true);
            if (!returned.Wait(2000) || input.IsRemote)
                throw new InvalidOperationException("F9 did not release input through the keyboard hook.");

            returned.Reset();
            input.Activate();
            Key(0xA2, false); // Held Ctrl must never prevent emergency return.
            Key(0x78, false);
            Key(0x78, true);
            Key(0xA2, true);
            if (!returned.Wait(2000) || input.IsRemote)
                throw new InvalidOperationException("Ctrl held during F9 prevented emergency return.");
        }
        finally
        {
            input.Deactivate();
            Key(0x78, true);
            Key(0xA2, true);
            input.Dispose();
        }

        // Verify the independent WM_HOTKEY path with the hook gone.
        using var window = new HotkeyWindow();
        if (!RegisterHotKey(window.Handle, 1, 0x4000, 0x78))
            throw new InvalidOperationException("F9 is reserved by another app.");
        try
        {
            Key(0x78, false);
            Key(0x78, true);
            var until = DateTime.UtcNow.AddSeconds(2);
            while (!window.Received && DateTime.UtcNow < until)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
            if (!window.Received) throw new InvalidOperationException("WM_HOTKEY fallback did not arrive.");
        }
        finally
        {
            Key(0x78, true);
            UnregisterHotKey(window.Handle, 1);
        }
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

    private static void Key(byte key, bool up) => keybd_event(key, 0, up ? 2u : 0u, 0);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
