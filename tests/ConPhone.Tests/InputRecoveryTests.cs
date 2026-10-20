using ConPhone.Core;

internal static class InputRecoveryTests
{
    public static void Run(Action<string, bool> check)
    {
        var queue = new Outbox();
        queue.TryAdd(new(10, new PointerPacket(0, 10)) { CaptureGeneration = 3, CreatedAt = 42 });
        queue.TryAdd(new(10, new PointerPacket(0, 20)) { CaptureGeneration = 3, CreatedAt = 999 });
        var merged = queue.TakeAsync(CancellationToken.None).GetAwaiter().GetResult();
        check("coalescing preserves oldest deadline and capture generation", merged.CreatedAt == 42 && merged.CaptureGeneration == 3);
        queue.TryAdd(new(10, new PointerPacket(0, 20)) { CaptureGeneration = 5 });
        check("coalescing cannot cross input activation", queue.CoalescePendingMotion(merged).CaptureGeneration == 3 && queue.Count == 1);
        queue.Clear();
        queue.TryAdd(new(10, new KeyboardPacket(new byte[Hid.KeyboardReportLength])) { CaptureGeneration = 3 });
        check("key release is an ordering barrier", queue.CoalescePendingMotion(merged).Packet is PointerPacket { X: 30 } && queue.Count == 1);
        var quiet = new BleRecoveryQuietPeriod();
        check("new process can advertise immediately", quiet.IsQuiet(0));
        quiet.Observe(100);
        check("transient native disconnect does not republish GATT", !quiet.IsQuiet(5099));
        check("quiet recovery does not wait forever", quiet.IsQuiet(5100));
        quiet.Observe(5200);
        check("new session activity restarts quiet window", !quiet.IsQuiet(9000));
        quiet.Observe(100);
        check("late callback cannot shorten the publication quiet window", !quiet.IsQuiet(10199) && quiet.IsQuiet(10200));
    }
}
