namespace ConPhone.Core;

public static class PointerPacing
{
    public const int FallbackIntervalMs = 16;

    // BluetoothLEConnectionParameters.ConnectionInterval uses 1.25 ms units.
    public static int FromBleConnectionInterval(ushort intervalUnits)
    {
        if (intervalUnits == 0) return FallbackIntervalMs;

        var milliseconds = (int)Math.Ceiling(intervalUnits * 1.25);
        return Math.Clamp(milliseconds, 8, 4000);
    }
}
