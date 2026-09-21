using ConPhone.Core;

namespace ConPhone.Windows;

internal static class ReleaseSelfTest
{
    public static async Task RunAsync()
    {
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
