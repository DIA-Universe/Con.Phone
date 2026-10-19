namespace ConPhone.Windows;

/// <summary>Native Windows server teardown/republication. Does not test an Android link.</summary>
internal static class BleLifecycleSelfTest
{
    public static async Task RunAsync()
    {
        using var hid = new BleHidServer();
        using var clipboard = new BleClipboardService(() => null, _ => false, () => false,
            () => 512 * 1024, () => false, () => null);
        hid.Status += Console.WriteLine;
        clipboard.Status += Console.WriteLine;
        for (var cycle = 1; cycle <= 20; cycle++)
        {
            await clipboard.PrepareAsync();
            await hid.StartAsync();
            await clipboard.StartAsync();
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (hid.Advertisement is not ("Started" or "StartedWithoutAllAdvertisementData") && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            if (hid.Advertisement is not ("Started" or "StartedWithoutAllAdvertisementData") || !clipboard.Running)
                throw new InvalidOperationException($"Cycle {cycle}: native server publication failed ({hid.Advertisement}).");
            clipboard.StopAdvertising();
            hid.Stop();
            if (hid.Advertisement != "NotStarted" || clipboard.Running ||
                hid.KeyboardSubscribers != 0 || clipboard.Subscribers != 0)
                throw new InvalidOperationException($"Cycle {cycle}: server references survived cleanup.");
            Console.WriteLine($"PASS: Windows native server lifecycle {cycle}/20");
            await Task.Delay(300);
        }
        hid.Dispose(); clipboard.Dispose(); // Idempotence, including subsequent using cleanup.
        Console.WriteLine("PASS: Windows server creation/publication/cleanup x20. Android connection and clipboard delivery NOT tested.");
    }
}
