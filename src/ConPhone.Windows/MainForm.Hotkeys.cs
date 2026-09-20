using System.Runtime.InteropServices;

namespace ConPhone.Windows;

public sealed partial class MainForm
{
    private const int EmergencyHotkeyId = 0x4350;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterEmergencyFallback();
    }

    private void RegisterEmergencyFallback()
    {
        if (_previewMode || !IsHandleCreated) return;
        UnregisterHotKey(Handle, EmergencyHotkeyId);
        var binding = _settings.EmergencyHotkey;
        uint modifiers = 0x4000; // MOD_NOREPEAT; Win32 ALT/CONTROL bits differ from our enum.
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)) modifiers |= 1;
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Control)) modifiers |= 2;
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift)) modifiers |= 4;
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Windows)) modifiers |= 8;
        if (!RegisterHotKey(Handle, EmergencyHotkeyId, modifiers, (uint)binding.Key))
            Post(() => Log("복귀 전역 단축키 등록 실패 · 다른 앱의 단축키와 충돌하는지 확인해 주세요. 입력 훅 복귀는 유지됩니다."));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam == EmergencyHotkeyId)
        {
            // Independent of the hook thread and the serialized UI operation queue.
            ImmediateLocal();
            _ = ReleaseAsync();
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (!_previewMode) UnregisterHotKey(Handle, EmergencyHotkeyId);
        base.OnHandleDestroyed(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
