namespace ConPhone.Windows;

public sealed partial class MainForm
{
    private volatile bool _inputConnectionReady;
    private volatile bool _desktopSuspended, _desktopLocked;
    private bool _desktopInteractive => !_desktopSuspended && !_desktopLocked;
    private long _remoteSessionEpoch;

    private async Task EnterRemoteAsync()
    {
        _inputConnectionReady = _ble.Ready;
        if (!_inputConnectionReady)
            throw new InvalidOperationException("휴대폰 키보드·마우스 연결이 준비되면 다시 전환해 주세요.");
        var target = _ble.ApprovedId!;
        ImmediateLocal();
        var epoch = Volatile.Read(ref _epoch);
        var generation = _input.Generation;
        bool Current() => !_closing && _desktopInteractive && epoch == Volatile.Read(ref _epoch) &&
            generation == _input.Generation && _inputConnectionReady && target == _ble.ApprovedId;
        if (!Current()) return;
        if (!await ReleaseAsync())
        {
            Log("휴대폰의 입력 해제를 확인하지 못해 PC 입력을 유지해요. 연결이 준비되면 다시 전환해 주세요.");
            return;
        }
        if (!Current()) return;
        Interlocked.Exchange(ref _remoteSessionEpoch, epoch);
        _pointerScaler.Reset();
        if (!await ActivateInputWhenReleasedAsync(epoch))
        {
            Interlocked.CompareExchange(ref _remoteSessionEpoch, 0, epoch);
            return;
        }
        SessionDiagnostics.Write($"input=remote source=manual epoch={epoch}");
        Log($"휴대폰 조작 중 · {HotkeyCatalog.Display(_settings.EmergencyHotkey)}로 PC에 바로 돌아와요.");
        _ = SyncLatestClipboardToPhoneSafeAsync(epoch, target);
    }

    private void ObserveInputConnection()
    {
        var generation = _input.Generation;
        var target = _ble.ApprovedId;
        var ready = _ble.Ready;
        if (target != _ble.ApprovedId) return;
        _inputConnectionReady = ready;
        if (ready && !_input.IsRemote) RetryPendingInputRelease();
        if (ready || !_input.IsRemote) return;
        if (!TryImmediateLocal(generation)) return;
        SessionDiagnostics.Write("input-fallback reason=hid-not-ready");
        Post(() => Log("휴대폰 입력 연결을 사용할 수 없어 PC로 바로 돌아왔어요. Bluetooth 복구는 별도로 진행해요."));
    }
}
