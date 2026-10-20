using ConPhone.Core;

internal static class EdgeSwitchTests
{
    public static void Run(Action<string, bool> check)
    {
        ScreenBounds[] screens = [new(-1920, 0, 0, 1080), new(0, 0, 2560, 1440)];
        var detector = new EdgeEntryDetector();
        check("startup on edge is not a crossing", detector.Observe(screens, PhoneSide.Right, 2559, 100, 5, false) is null);
        detector.Observe(screens, PhoneSide.Right, -50, 100, 5, false);
        check("inner monitor seam stays local", detector.Observe(screens, PhoneSide.Right, -1, 100, 5, false) is null);
        check("outermost right edge enters phone", detector.Observe(screens, PhoneSide.Right, 2559, 100, 5, false)?.Screen == screens[1]);
        check("repeated edge motion does not queue more switches", detector.Observe(screens, PhoneSide.Right, 2559, 100, 5, false) is null);
        detector.Observe(screens, PhoneSide.Right, 2500, 100, -5, false);
        check("inward motion at edge cannot enter", detector.Observe(screens, PhoneSide.Right, 2559, 100, -1, false) is null);
        check("held input prevents entry", detector.Observe(screens, PhoneSide.Right, 2559, 100, 5, true) is null);
        check("key release cannot activate a stale edge", detector.Observe(screens, PhoneSide.Right, 2559, 100, 5, false) is null);
        detector.Observe(screens, PhoneSide.Right, 2500, 100, -5, false);
        check("fresh approach re-arms after held input", detector.Observe(screens, PhoneSide.Right, 2559, 100, 5, false) is not null);
        detector.Observe(screens, PhoneSide.Left, -1800, 100, -5, false);
        check("left edge supports negative coordinates", detector.Observe(screens, PhoneSide.Left, -1920, 100, -5, false)?.Screen == screens[0]);
        detector.Observe(screens, PhoneSide.Right, 2000, 1300, 5, false);
        check("staggered monitor topology uses current row", detector.Observe(screens, PhoneSide.Right, 2559, 1300, 5, false) is not null);
        check("off-screen gap is not an entry", detector.Observe(screens, PhoneSide.Right, -100, 1300, 5, false) is null);
        detector.Disarm();
        check("layout changes disarm the old edge", detector.Observe(screens, PhoneSide.Right, 2559, 100, 5, false) is null);

        foreach (var side in new[] { PhoneSide.Left, PhoneSide.Right })
        {
            var sign = side == PhoneSide.Right ? 1 : -1;
            var pointer = new EstimatedPhonePointer(1000, side);
            check($"{side}: entry cannot immediately bounce back", !pointer.Advance(-sign * 100, false));
            check($"{side}: move inward arms return", !pointer.Advance(sign * 100, false));
            check($"{side}: return crosses the estimated near edge", pointer.Advance(-sign * 101, false));
            check($"{side}: duplicate return suppressed", !pointer.Advance(-sign * 100, false));
            pointer.Advance(sign * 2000, false);
            check($"{side}: opposite screen edge saturates", pointer.Position == 1000);
            check($"{side}: clicks prevent return", !pointer.Advance(-sign * 1100, true));
            check($"{side}: release at edge does not return late", !pointer.Advance(-sign, false));
            pointer.Advance(sign * 100, false);
            check($"{side}: fresh return after button release", pointer.Advance(-sign * 100, false));
            var calibration = new PointerCalibration(side);
            calibration.Advance(sign * 9000);
            check($"{side}: calibration first mark starts measurement", calibration.Mark() is null && calibration.Stage == 1);
            calibration.Advance(sign * 1000);
            check($"{side}: calibration opposite mark waits for return", calibration.Mark() is null && calibration.Stage == 2);
            calibration.Advance(-sign * 1100);
            check($"{side}: calibration stores average delivered travel", calibration.Mark() == 1050);
        }
        var invalid = new PointerCalibration(PhoneSide.Right);
        invalid.Mark(); invalid.Advance(-100);
        try { invalid.Mark(); check("wrong calibration direction rejected", false); }
        catch (InvalidOperationException) { check("wrong calibration direction rejected", true); }
        invalid = new PointerCalibration(PhoneSide.Right);
        invalid.Mark(); invalid.Advance(100); invalid.Mark(); invalid.Advance(-1000);
        try { invalid.Mark(); check("asymmetric calibration rejected", false); }
        catch (InvalidOperationException) { check("asymmetric calibration rejected", true); }
        check("uncalibrated profile cannot enable automatic capture", !new EdgeSwitchProfile { Enabled = true }.Normalized().Enabled);
        check("invalid profile values normalize safely", new EdgeSwitchProfile { Side = (PhoneSide)99, Travel = int.MaxValue, Enabled = true }.Normalized() is { Side: PhoneSide.Right, Travel: 0, Enabled: false });
        var queue = new Outbox();
        queue.TryAdd(new(10, new PointerPacket(0, 10)) { CaptureGeneration = 3, CreatedAt = 42 });
        queue.TryAdd(new(10, new PointerPacket(0, 20)) { CaptureGeneration = 3, CreatedAt = 999 });
        var merged = queue.TakeAsync(CancellationToken.None).GetAwaiter().GetResult();
        check("coalescing preserves oldest deadline and capture generation", merged.CreatedAt == 42 && merged.CaptureGeneration == 3);
        queue.TryAdd(new(10, new PointerPacket(0, 20)) { CaptureGeneration = 5 });
        check("coalescing cannot cross input activation", queue.CoalescePendingMotion(merged).CaptureGeneration == 3 && queue.Count == 1);
        queue.Clear();
        queue.TryAdd(new(10, new CalibrationMarkPacket()));
        check("calibration mark is an ordering barrier", queue.CoalescePendingMotion(merged).Packet is PointerPacket { X: 30 } && queue.Count == 1);
        var quiet = new BleRecoveryQuietPeriod();
        check("new process can advertise immediately", quiet.IsQuiet(0));
        quiet.Observe(100);
        check("transient native disconnect does not republish GATT", !quiet.IsQuiet(5099));
        check("quiet recovery does not wait forever", quiet.IsQuiet(5100));
        quiet.Observe(5200);
        check("new session activity restarts quiet window", !quiet.IsQuiet(9000));
    }
}
