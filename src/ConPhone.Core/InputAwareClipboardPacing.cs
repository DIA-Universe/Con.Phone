namespace ConPhone.Core;

public static class InputAwareClipboardPacing
{
    // Leave radio opportunities for HID without waiting indefinitely for idle.
    // Apply before each notification, including retries, not before receipt waits.
    public static Task YieldAsync(Func<bool>? inputBusy, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        token.ThrowIfCancellationRequested();
        return inputBusy?.Invoke() == true
            ? (delay ?? Task.Delay)(TimeSpan.FromMilliseconds(48), token)
            : Task.CompletedTask;
    }
}
