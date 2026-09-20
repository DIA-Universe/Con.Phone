using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ConPhone.Windows;

internal static class PrecisionDelay
{
    // Task.Delay can round a 16 ms mouse cadence up to the next system timer tick.
    // A high-resolution waitable timer avoids a process-wide timer-resolution change.
    public static async Task WaitAsync(TimeSpan delay, CancellationToken token)
    {
        using var timer = CreateWaitableTimerEx(0, null, 2, 0x1F0003);
        if (timer.IsInvalid)
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            return;
        }
        long due = -Math.Max(1, delay.Ticks);
        if (!SetWaitableTimer(timer, ref due, 0, 0, 0, false))
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            return;
        }
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
        signal.SafeWaitHandle = timer;
        await Task.Run(() =>
        {
            if (WaitHandle.WaitAny([signal, token.WaitHandle]) == 1)
                token.ThrowIfCancellationRequested();
        }, token).ConfigureAwait(false);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, nint callback, nint argument, bool resume);
}
