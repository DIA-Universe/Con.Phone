namespace ConPhone.Core;

public static class PointerPacing
{
    // One outstanding notification plus start-to-start pacing. A slow radio still
    // applies backpressure; fast 7.5/15 ms links are no longer limited to 16 ms.
    public const int FallbackIntervalMs = 8;

    // BluetoothLEConnectionParameters.ConnectionInterval uses 1.25 ms units.
    public static int FromBleConnectionInterval(ushort intervalUnits)
    {
        if (intervalUnits == 0) return FallbackIntervalMs;

        var milliseconds = (int)Math.Ceiling(intervalUnits * 1.25);
        return Math.Clamp(milliseconds, 8, 4000);
    }
}
