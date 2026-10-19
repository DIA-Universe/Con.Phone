using ConPhone.Core;

namespace ConPhone.Windows;

internal static class ReleaseSelfTest
{
    public static async Task RunAsync()
    {
        foreach (var hresult in new[] { 0x80010108u, 0x8000000Eu, 0x80000013u })
        {
            if (BleNativeState.Read<bool>(() => throw new System.Runtime.InteropServices.COMException("peer unavailable", unchecked((int)hresult)), false))
                throw new InvalidOperationException("A disconnected WinRT object was reported as live.");
        }
        try {
            BleNativeState.Read<bool>(() => throw new InvalidOperationException("unrelated bug"), false);
            throw new Exception("Unrelated programming errors must not be hidden.");
        }
        catch (InvalidOperationException) { }
        Console.WriteLine("PASS: RPC_E_DISCONNECTED/closed native snapshots are isolated without hiding unrelated errors.");
        if (!AppleLockShortcut.Report(new HotkeyBinding { Key = (int)Keys.F12 }).SequenceEqual(new byte[] {0,0,0x45,0,0,0,0,0}))
            throw new InvalidOperationException("iOS F12 lock report is incorrect.");
        if (!AppleLockShortcut.Report(new HotkeyBinding { Key = (int)Keys.Q, Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Windows }).SequenceEqual(new byte[] {9,0,0x14,0,0,0,0,0}))
            throw new InvalidOperationException("Custom iOS lock chord differs from Apple input mapping.");
        var htmlOnly = new DataObject();
        htmlOnly.SetData(DataFormats.Html, false, "<html><!--StartFragment--><div>Teams 한글🙂</div><div>두 번째 줄</div><!--EndFragment--></html>");
        if (ClipboardTextReader.Read(htmlOnly, out var converted) != "Teams 한글🙂\n두 번째 줄" || !converted)
            throw new InvalidOperationException("HTML-only clipboard text was ignored.");
        htmlOnly.SetData(DataFormats.UnicodeText, false, "preferred plain text");
        if (ClipboardTextReader.Read(htmlOnly, out converted) != "preferred plain text" || converted)
            throw new InvalidOperationException("HTML displaced an existing Unicode text representation.");
        var streamHtml = new DataObject();
        streamHtml.SetData(DataFormats.Html, false, new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<p>Teams 스트림🙂</p>\0")));
        if (ClipboardTextReader.Read(streamHtml, out converted) != "Teams 스트림🙂" || !converted)
            throw new InvalidOperationException("Stream-backed HTML clipboard was ignored.");
        Console.WriteLine("PASS: HTML-only clipboard source and Unicode-text priority.");
        var startupEvents = new List<string>();
        await ConnectionStartup.RunAsync(
            () => throw new InvalidOperationException("clipboard unavailable"),
            () => { startupEvents.Add("hid"); return Task.CompletedTask; },
            () => { startupEvents.Add("clipboard"); return Task.CompletedTask; },
            (_, _) => startupEvents.Add("optional failure"));
        if (!startupEvents.SequenceEqual(new[] { "optional failure", "hid", "clipboard" }))
            throw new InvalidOperationException("Clipboard preparation blocked HID startup.");
        startupEvents.Clear();
        try
        {
            await ConnectionStartup.RunAsync(
                () => { startupEvents.Add("prepare"); return Task.CompletedTask; },
                () => throw new NotSupportedException("radio unavailable"),
                () => { startupEvents.Add("clipboard"); return Task.CompletedTask; },
                (_, _) => startupEvents.Add("optional failure"));
            throw new InvalidOperationException("HID failure was hidden.");
        }
        catch (NotSupportedException) { }
        if (!startupEvents.SequenceEqual(new[] { "prepare" }))
            throw new InvalidOperationException("Clipboard advertised after failed HID startup.");
        startupEvents.Clear();
        await ConnectionStartup.RunAsync(
            () => { startupEvents.Add("prepare"); return Task.CompletedTask; },
            () => { startupEvents.Add("hid"); return Task.CompletedTask; },
            () => throw new InvalidOperationException("clipboard advertisement unavailable"),
            (_, _) => startupEvents.Add("optional failure"));
        if (!startupEvents.SequenceEqual(new[] { "prepare", "hid", "optional failure" }))
            throw new InvalidOperationException("Optional transport failure disrupted HID startup.");
        var hidAdvertisement = ConnectionStartup.HidAdvertisement();
        var clipboardAdvertisement = ConnectionStartup.ClipboardAdvertisement();
        if (!hidAdvertisement.IsConnectable || !hidAdvertisement.IsDiscoverable ||
            clipboardAdvertisement.IsConnectable || !clipboardAdvertisement.IsDiscoverable)
            throw new InvalidOperationException("Clipboard competes with HID discovery.");
        Console.WriteLine("PASS: optional clipboard failure isolation, HID-first advertisement and startup order.");
        var migrated = SettingsStore.ReadJson("""
            {"ClipboardSyncEnabled":false,"ClipboardAutoSendText":false,
             "CaptureClipboardText":false,"CaptureClipboardImages":true,
             "ClipboardAutoSendImages":true,"MouseSpeedPercent":37}
            """);
        if (!migrated.ClipboardSyncEnabled || !migrated.ClipboardAutoSendText ||
            !migrated.CaptureClipboardText || migrated.CaptureClipboardImages ||
            migrated.ClipboardAutoSendImages || migrated.MouseSpeedPercent != 37)
            throw new InvalidOperationException("Legacy settings migration failed.");
        await using var sync = new ClipboardSyncCoordinator(() => "test", _ => true,
            () => true, () => 65536, () => false, () => false, () => "LINKO");
        var image = ClipboardContent.FromImage(ClipboardContentKind.Png,
            [137,80,78,71,13,10,26,10,0]);
        if (await sync.SendAsync(image, 65536, CancellationToken.None))
            throw new InvalidOperationException("Image transmission was not rejected.");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 30; i++)
            await PrecisionDelay.WaitAsync(TimeSpan.FromMilliseconds(8), CancellationToken.None);
        Console.WriteLine($"High-resolution timer: 30 x 8ms = {watch.Elapsed.TotalMilliseconds:F1}ms");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await PrecisionDelay.WaitAsync(TimeSpan.FromSeconds(1), cancelled.Token);
            throw new InvalidOperationException("Mouse wait ignored cancellation.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: legacy settings, image send rejection, pointer timer and cancellation.");
    }
}
