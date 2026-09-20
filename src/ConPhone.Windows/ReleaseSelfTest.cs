using ConPhone.Core;

namespace ConPhone.Windows;

internal static class ReleaseSelfTest
{
    public static async Task RunAsync()
    {
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
