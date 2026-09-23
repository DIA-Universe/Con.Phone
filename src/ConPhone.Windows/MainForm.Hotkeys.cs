using System.Runtime.InteropServices;

namespace ConPhone.Windows;

public sealed partial class MainForm
{
    private const int ToggleHotkeyId = 0x4351;
    private const int NextHotkeyId = 0x4352;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterShortcutFallbacks();
    }

    private void RegisterShortcutFallbacks()
    {
        if (_previewMode || !IsHandleCreated) return;
        // Remote-only shortcuts belong to InputCapture's thread and lifetime.
        RegisterShortcut(ToggleHotkeyId, _settings.ToggleHotkey);
        RegisterShortcut(NextHotkeyId, _settings.NextDeviceHotkey);
    }

    private void RegisterShortcut(int id, HotkeyBinding binding)
    {
        UnregisterHotKey(Handle, id);
        uint modifiers = 0x4000;
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)) modifiers |= 1;
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Control)) modifiers |= 2;
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift)) modifiers |= 4;
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Windows)) modifiers |= 8;
        if (!RegisterHotKey(Handle, id, modifiers, (uint)binding.Key))
            Post(() => Log($"{HotkeyCatalog.Display(binding)} 전역 단축키 등록 실패 · 다른 앱의 단축키 충돌을 확인해 주세요."));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && !_input.IsCapturingHotkey)
        {
            switch ((int)m.WParam)
            {
                case ToggleHotkeyId:
                    DispatchCritical(ToggleAsync);
                    return;
                case NextHotkeyId:
                    DispatchCritical(SelectNextTrustedTargetAsync);
                    return;
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (!_previewMode)
            foreach (var id in new[] { ToggleHotkeyId, NextHotkeyId })
                UnregisterHotKey(Handle, id);
        base.OnHandleDestroyed(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
