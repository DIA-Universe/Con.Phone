using ConPhone.Core;

namespace ConPhone.Windows;

internal static class AppleLockShortcut
{
    // Bindings are entered on the PC keyboard, matching normal Apple-profile input.
    public static byte[] Report(HotkeyBinding binding)
    {
        var usage = Hid.Usage(binding.Key);
        if (usage == 0 || usage > 0x65) throw new InvalidOperationException("iOS에 전송할 수 없는 잠금 키입니다.");
        var state = new KeyboardState();
        foreach (var (flag, key) in new[]
        {
            (HotkeyModifiers.Control, (byte)0xE0), (HotkeyModifiers.Shift, (byte)0xE1),
            (HotkeyModifiers.Alt, (byte)0xE2), (HotkeyModifiers.Windows, (byte)0xE3)
        })
            if (binding.Modifiers.HasFlag(flag)) state.Update(Hid.MapModifierUsage(key, ModifierProfile.MacCtrlCommandSwap), true);
        state.Update(usage, true);
        return state.Snapshot();
    }
}
