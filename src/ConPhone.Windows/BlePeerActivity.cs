using ConPhone.Core;

namespace ConPhone.Windows;

internal static class BlePeerActivity
{
    private static readonly BleRecoveryQuietPeriod QuietPeriod = new();
    public static void Observe() => QuietPeriod.Observe(Environment.TickCount64);
    public static bool IsQuiet => QuietPeriod.IsQuiet(Environment.TickCount64);
}
