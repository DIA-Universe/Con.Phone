using System.Threading.Channels;
using ConPhone.Core;

namespace ConPhone.Windows;

public sealed record CapturedInput(long Generation, InputPacket Packet, long CreatedAt);

public sealed partial class InputCapture
{
    private readonly Channel<(long Generation, InputPacket? Packet, Action? Action, long CreatedAt)> _dispatch =
        Channel.CreateBounded<(long, InputPacket?, Action?, long)>(new BoundedChannelOptions(512)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _commands = new();
    private Task? _dispatchTask;
    private System.Threading.Timer? _dispatchWatchdog;
    private long _dispatchBegan, _dispatchGeneration;
    // Even = local, odd = remote. One CAS fences activation against an emergency
    // return arriving while the owner thread is taking a key-state snapshot.
    private long _captureState;
    public long Generation => Interlocked.Read(ref _captureState);
    public event Action<long, string>? SafetyReturn;

    private async Task DispatchInputAsync()
    {
        await foreach (var item in _dispatch.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_disposed || item.Generation != Generation) continue;
            try
            {
                Interlocked.Exchange(ref _dispatchGeneration, item.Generation);
                Interlocked.Exchange(ref _dispatchBegan, item.CreatedAt);
                if (item.Packet is { } packet)
                {
                    if (_remote) Packet?.Invoke(new CapturedInput(item.Generation, packet, item.CreatedAt));
                }
                else item.Action?.Invoke();
            }
            catch (Exception ex)
            {
                if (DeactivateIfGeneration(item.Generation))
                    SignalSafetyReturn((item.Generation | 1) + 1, $"입력 전달 오류 — PC 복귀: {ex.GetType().Name}");
            }
            finally { Interlocked.Exchange(ref _dispatchBegan, 0); }
        }
    }

    private void Notify(Action action) => QueueDispatch(null, action);
    private void Emit(InputPacket packet) => QueueDispatch(packet, null);
    private void QueueDispatch(InputPacket? packet, Action? action)
    {
        if (_dispatch.Writer.TryWrite((Generation, packet, action, Environment.TickCount64))) return;
        // Never wait for a consumer from a Windows hook. Fail open on overload.
        var generation = Generation;
        if ((generation & 1) != 0 && DeactivateIfGeneration(generation)) SignalSafetyReturn((generation | 1) + 1, "입력 처리 지연 — PC로 복귀했어요.");
    }

    private void CheckDispatchDeadline(object? unused)
    {
        var began = Interlocked.Read(ref _dispatchBegan);
        var generation = Interlocked.Read(ref _dispatchGeneration);
        if (!_remote || began == 0 || Environment.TickCount64 - began < 500) return;
        if (DeactivateIfGeneration(generation)) SignalSafetyReturn((generation | 1) + 1, "입력 처리 응답이 늦어 PC로 돌아왔어요.");
    }

    private void SignalSafetyReturn(long generation, string message)
    {
        ThreadPool.QueueUserWorkItem(_ => SafetyReturn?.Invoke(generation, message));
    }

    internal Task<T> OnInputThreadAsync<T>(Func<T> work)
    {
        if (Thread.CurrentThread == _thread) return Task.FromResult(work());
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(() =>
        {
            try { done.TrySetResult(work()); }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        if (_sink is not { } sink || !PostMessage(sink.Handle, 0x8004, 0, 0))
            done.TrySetException(new InvalidOperationException("입력 수집이 아직 준비되지 않았어요."));
        return done.Task;
    }

    private void PostInput(Action work)
    {
        if (Thread.CurrentThread == _thread) { work(); return; }
        _commands.Enqueue(work);
        if (_sink is { } sink) PostMessage(sink.Handle, 0x8004, 0, 0);
    }

}
