using ConPhone.Core;

namespace ConPhone.Windows;

public sealed class ClipboardSyncCoordinator : IAsyncDisposable
{
    private readonly Func<string?> _approvedDeviceId;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _interactiveInput;
    private readonly Func<bool> _pointerBusy;
    private readonly DirectClipboardLink _direct;
    private readonly BleClipboardService _ble;
    private readonly P2PClipboardServer _p2p;
    private readonly SemaphoreSlim _send = new(1, 1);
    private Func<string, ClipboardContent, Task<bool>>? _receiver;
    private long _targetGeneration;
    private bool _started;

    public event Action<string>? Status;

    public ClipboardSyncCoordinator(
        Func<string?> approvedDeviceId,
        Func<string, bool> trustedDevice,
        Func<bool> enabled,
        Func<int> bleImageLimitBytes,
        Func<bool> interactiveInput,
        Func<bool> pointerBusy,
        Func<string> p2pLinkName)
    {
        _approvedDeviceId = approvedDeviceId;
        _enabled = enabled;
        _interactiveInput = interactiveInput;
        _pointerBusy = pointerBusy;
        _direct = new DirectClipboardLink(p2pLinkName);
        _ble = new BleClipboardService(
            approvedDeviceId,
            trustedDevice,
            enabled,
            bleImageLimitBytes,
            GetDirectLinkConfig);
        _p2p = new P2PClipboardServer(approvedDeviceId, enabled);

        _direct.Status += text => Status?.Invoke(text);
        _ble.Status += text => Status?.Invoke(text);
        _p2p.Status += text => Status?.Invoke(text);
        _ble.ReceiveContentAsync = OnReceivedAsync;
        _p2p.ReceiveContentAsync = OnReceivedAsync;
    }

    public void SetReceiver(
        Func<string, ClipboardContent, Task<bool>> receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        _receiver = receiver;
    }

    public int BleSubscribers => _ble.Subscribers;
    public bool P2PLinkRunning => _direct.Running;
    public bool P2PChannelConnected => _p2p.Connected;
    public string? P2PHostAddress => _direct.Config?.HostAddress;

    public Task PrepareBleAsync() => _ble.PrepareAsync();

    public async Task StartAsync()
    {
        if (!_started)
        {
            _started = true;
            try
            {
                await _p2p.StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Status?.Invoke($"Clipboard P2P 서버 시작 실패; BLE fallback 유지: {ex.Message}");
            }
        }

        try
        {
            await _ble.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Clipboard BLE fallback 시작 실패: {ex.Message}");
        }

    }

    public async Task EnsureHealthyAsync()
    {
        if (!_enabled())
            return;

        try
        {
            await _p2p.StartAsync().ConfigureAwait(false);
            await _ble.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Clipboard transport 자동 복구 대기: {ex.Message}");
        }
    }

    public void TargetChanged()
    {
        Interlocked.Increment(ref _targetGeneration);
        _ble.CancelPendingSends(
            "활성 휴대폰이 변경되어 이전 BLE Clipboard ACK 대기를 취소했습니다.");
        _p2p.Deactivate();
        _direct.RotateCredentials();
        Status?.Invoke("활성 휴대폰 변경 · P2P 채널과 Wi-Fi 자격 증명을 새 대상으로 재설정합니다.");
    }

    public async Task SetActiveAsync(bool active)
    {
        if (active)
        {
            await StartAsync().ConfigureAwait(false);
            return;
        }

        _ble.CancelPendingSends(
            "기기간 복붙이 비활성화되어 BLE Clipboard ACK 대기를 취소했습니다.");
        _ble.StopAdvertising();
        _direct.Stop();
        await _p2p.StopListeningAsync()
            .ConfigureAwait(false);
        Status?.Invoke("기기간 복붙 비활성화 · Bluetooth/P2P listener까지 중지");
    }

    private DirectLinkConfig? GetDirectLinkConfig()
    {
        if (!_enabled()) return null;

        try
        {
            _direct.Start();
            return _direct.Config;
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Clipboard P2P fast link 시작 실패; BLE fallback 유지: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> SendAsync(
        ClipboardContent content,
        int bleImageLimitBytes,
        CancellationToken token)
    {
        if (!_enabled())
            return false;

        var id = _approvedDeviceId();
        if (id is null)
        {
            Status?.Invoke("Clipboard 동기화 대상 기기가 승인되지 않았습니다.");
            return false;
        }

        var generation = Interlocked.Read(ref _targetGeneration);

        bool TargetStillActive() =>
            generation == Interlocked.Read(ref _targetGeneration) &&
            string.Equals(_approvedDeviceId(), id, StringComparison.Ordinal);

        await _send.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!TargetStillActive())
                return false;

            if (_p2p.HasClient(id))
            {
                try
                {
                    await _p2p.SendAsync(id, content, token).ConfigureAwait(false);
                    Status?.Invoke($"Clipboard 전송 완료 · P2P · {Describe(content)}");
                    return true;
                }
                catch (Exception ex)
                {
                    Status?.Invoke($"P2P 전송 실패, BLE fallback 확인: {ex.Message}");
                }
            }

            var isImage = content.Kind != ClipboardContentKind.TextUtf8;
            var bleAllowed =
                !isImage ||
                content.Data.Length <= bleImageLimitBytes;

            // Images prefer the direct P2P path even when they are small enough for BLE.
            // If P2P is unavailable, small images may still use BLE, but BLE bulk chunks
            // pause whenever recent pointer movement is detected.
            if (isImage && !_p2p.HasClient(id))
            {
                _ = GetDirectLinkConfig();

                var interactive = _interactiveInput();
                Status?.Invoke(
                    interactive
                        ? "입력 품질 보호 · 이미지 P2P 연결을 우선 확인합니다."
                        : "이미지 전송 · P2P 고속 링크를 우선 확인합니다.");

                var wait = !bleAllowed
                    ? TimeSpan.FromSeconds(15)
                    : interactive
                        ? TimeSpan.FromSeconds(2)
                        : TimeSpan.FromSeconds(1);
                var deadline = DateTimeOffset.UtcNow + wait;

                while (!_p2p.HasClient(id) && DateTimeOffset.UtcNow < deadline)
                {
                    token.ThrowIfCancellationRequested();
                    if (!TargetStillActive())
                        return false;

                    await Task.Delay(125, token).ConfigureAwait(false);
                }

                if (!TargetStillActive())
                    return false;

                if (_p2p.HasClient(id))
                {
                    try
                    {
                        await _p2p.SendAsync(id, content, token).ConfigureAwait(false);
                        Status?.Invoke($"Clipboard 전송 완료 · P2P · {Describe(content)}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Status?.Invoke($"P2P 이미지 전송 실패, BLE fallback 확인: {ex.Message}");
                    }
                }
            }

            if (!TargetStillActive())
                return false;

            if (bleAllowed && _ble.HasSubscriber(id))
            {
                try
                {
                    await _ble.SendAsync(
                        id,
                        content,
                        token,
                        isImage && _interactiveInput()
                            ? _pointerBusy
                            : null).ConfigureAwait(false);
                    Status?.Invoke($"Clipboard 전송 완료 · BLE · {Describe(content)}");
                    return true;
                }
                catch (Exception ex)
                {
                    Status?.Invoke($"Clipboard BLE 전송 실패: {ex.Message}");
                }
            }
            else if (!bleAllowed)
            {
                Status?.Invoke(
                    $"이미지 {content.Data.Length / 1024:N0} KiB는 BLE 제한을 초과했고 P2P 연결도 준비되지 않았습니다.");
            }

            return false;
        }
        finally
        {
            _send.Release();
        }
    }

    private async Task<bool> OnReceivedAsync(
        string deviceId,
        ClipboardContent content)
    {
        if (!_enabled() ||
            !string.Equals(
                _approvedDeviceId(),
                deviceId,
                StringComparison.Ordinal))
            return false;

        var receiver = _receiver;
        if (receiver is null)
        {
            Status?.Invoke(
                "Windows Clipboard 수신기가 아직 준비되지 않았습니다.");
            return false;
        }

        Status?.Invoke(
            $"Clipboard 수신 · {Describe(content)}");

        // Re-check the active target immediately before and after the UI receiver.
        // A target switch must never let device A's clipboard be attributed to device B.
        if (!string.Equals(
                _approvedDeviceId(),
                deviceId,
                StringComparison.Ordinal))
            return false;

        var applied = await receiver(
                deviceId,
                content)
            .ConfigureAwait(false);

        return applied &&
            string.Equals(
                _approvedDeviceId(),
                deviceId,
                StringComparison.Ordinal);
    }

    private static string Describe(ClipboardContent content) =>
        content.Kind == ClipboardContentKind.TextUtf8
            ? $"텍스트 {content.Data.Length:N0} bytes"
            : $"이미지 {content.Kind} {content.Data.Length / 1024.0:N1} KiB";

    public async ValueTask DisposeAsync()
    {
        await _p2p.DisposeAsync().ConfigureAwait(false);
        _ble.Dispose();
        _direct.Dispose();
        _send.Dispose();
    }
}
