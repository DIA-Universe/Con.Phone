using System.Diagnostics;

namespace ConPhone.Windows;

internal static class InputActivationSelfTest
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine($"PASS: {name}");
    }

    internal static async Task RunAsync()
    {
        // Exercise production activation with a controlled Windows key-state
        // reader. No OS key injection, Bluetooth service, or phone is required.
        int[] held = [];
        var recentlyPressed = false;
        using var input = new InputCapture(key => Volatile.Read(ref held).Contains(key)
            ? unchecked((short)0x8000) : recentlyPressed ? (short)1 : (short)0);
        await input.Started;
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        held = [0x11, 0xA2];
        try { input.Activate(); throw new Exception("Expected blocked activation"); }
        catch (InputTransitionBlockedException ex) {
            Check(ex.Keys.Contains(0xA2) && ex.Message.Contains("왼쪽 Ctrl") && !input.IsRemote,
                "Held Ctrl blocks activation with exact key name and input remains local");
        }
        Check(InputTransitionBlockedException.Describe([0x11, 0xA2]) == "왼쪽 Ctrl",
            "Generic and left/right modifier aliases do not duplicate diagnostic names");

        held = [0x01];
        var waiting = 0;
        var clicked = await input.ActivateWhenReleasedAsync(() => true, message => {
            Check(!input.IsRemote && message.Contains("왼쪽 마우스 버튼"),
                "Mouse click remains local while release is pending");
            waiting++;
            held = []; // The next OS snapshot observes the actual release.
        }, token.Token);
        Check(clicked && input.IsRemote && waiting == 1,
            "Mouse release completes the existing transition without a second click");
        input.Deactivate();

        held = [0x11, 0xA2];
        var toggled = await input.ActivateWhenReleasedAsync(() => true, message => {
            Check(message.Contains("왼쪽 Ctrl") && !input.IsRemote,
                "Keyboard shortcut path identifies the same physical blocker");
            held = [];
        }, token.Token);
        Check(toggled && input.IsRemote, "Modifier release completes the existing transition");
        input.Deactivate();

        input.ConfigureHotkeys(SettingsStore.DefaultNextDevice(),
            new HotkeyBinding { Key = 0x77, Modifiers = HotkeyModifiers.Control },
            SettingsStore.DefaultEmergency(), SettingsStore.DefaultLockAndReturn());
        held = [0x11, 0xA2, 0x77];
        Check(await input.ActivateWhenReleasedAsync(() => true,
            _ => throw new Exception("Configured chord was treated as a blocker"), token.Token),
            "Configured Ctrl+F8 chord is allowed, including generic and physical modifier codes");
        input.Deactivate();
        input.ConfigureHotkeys(SettingsStore.DefaultNextDevice(), SettingsStore.DefaultToggle(),
            SettingsStore.DefaultEmergency(), SettingsStore.DefaultLockAndReturn());

        held = [0x02];
        var current = true;
        var cancelled = await input.ActivateWhenReleasedAsync(() => current, _ => { current = false; held = []; }, token.Token);
        Check(!cancelled && !input.IsRemote,
            "Connection/target change cancels a pending switch even when buttons are then released");
        using var cancellation = new CancellationTokenSource();
        held = [0xA0];
        try {
            await input.ActivateWhenReleasedAsync(() => true, _ => cancellation.Cancel(), cancellation.Token);
            throw new Exception("Expected cancellation");
        }
        catch (OperationCanceledException) { Check(!input.IsRemote, "Shutdown cancellation never activates capture"); }

        held = [0x05];
        var time = Stopwatch.StartNew();
        try {
            await input.ActivateWhenReleasedAsync(() => true, null, token.Token);
            throw new Exception("Expected held-button deadline");
        }
        catch (InputTransitionBlockedException ex) {
            Check(!input.IsRemote && ex.Message.Contains("마우스 추가 버튼 1") && time.Elapsed < TimeSpan.FromSeconds(3),
                "Persistently held mouse button times out with a named blocker and stays local");
        }
        held = [];
        recentlyPressed = true;
        Check(await input.ActivateWhenReleasedAsync(() => true, null, token.Token),
            "GetAsyncKeyState low bit alone cannot block activation");
        input.Deactivate();
        Console.WriteLine("PASS: Activation release-wait regression. User hardware stuck-key cause NOT reproduced.");
    }
}
