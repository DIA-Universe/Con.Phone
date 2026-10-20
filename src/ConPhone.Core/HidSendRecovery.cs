namespace ConPhone.Core;

public static class HidSendRecovery
{
    public static readonly TimeSpan GracePeriod = TimeSpan.FromMilliseconds(500);

    // false from trySend means no notification was submitted (e.g. subscriptions
    // are being restored). Never replay a submitted report: mouse deltas and
    // scrolls are relative and delivery can be ambiguous after a timeout.
    public static async Task<bool> SendAsync(
        Func<bool> current,
        Func<CancellationToken, Task<bool>> trySend,
        CancellationToken token,
        TimeSpan? gracePeriod = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(gracePeriod ?? GracePeriod);
        try
        {
            while (current())
            {
                deadline.Token.ThrowIfCancellationRequested();
                // Bound even a native operation which ignores cancellation. Its late
                // completion is fenced by the caller's input/target generation.
                if (await trySend(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false))
                    return current();
                if (!current()) return false;
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
            return false;
        }
        catch (Exception) when (!current() && !token.IsCancellationRequested)
        {
            // An old send must not cancel a new target or a fresh F8 activation.
            return false;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Bluetooth 입력 연결/전송 복구 대기 시간이 초과되었습니다.");
        }
    }
}
