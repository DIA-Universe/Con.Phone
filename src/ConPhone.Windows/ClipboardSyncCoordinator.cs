using ConPhone.Core;

namespace ConPhone.Windows;

public sealed class ClipboardSyncCoordinator : IAsyncDisposable
{
    private readonly Func<string?> _approvedDeviceId;
    private readonly Func<bool> _enabled;
    private readonly DirectClipboardLink _direct;
    private readonly BleClipboardService _ble;
    private readonly LanClipboardServer _lan;
    private readonly RelayClipboardTransport _relay;
    private readonly SemaphoreSlim _send = new(1, 1);
    private bool _started;

    public event Action<ClipboardContent>? Received;
    public event Action<string>? Status;

    public ClipboardSyncCoordinator(
        Func<string?> approvedDeviceId,
        Func<bool> enabled,
        Func<bool> relayEnabled,
        Func<string?> relayUrl)
    {
        _approvedDeviceId = approvedDeviceId;
        _enabled = enabled;
        _direct = new DirectClipboardLink();
        _ble = new BleClipboardService(approvedDeviceId, enabled, () => _direct.Config);
        _lan = new LanClipboardServer(approvedDeviceId, enabled);
        _relay = new RelayClipboardTransport(
            approvedDeviceId,
            () => enabled() && relayEnabled(),
            relayUrl);

        _direct.Status += text => Status?.Invoke(text);
        _ble.Status += text => Status?.Invoke(text);
        _lan.Status += text => Status?.Invoke(text);
        _relay.Status += text => Status?.Invoke(text);
        _ble.Received += OnReceived;
        _lan.Received += OnReceived;
        _relay.Received += OnReceived;
    }

    public int BleSubscribers => _ble.Subscribers;
    public bool RelayConnected => _relay.Connected;

    public Task PrepareBleAsync() => _ble.PrepareAsync();

    public async Task StartAsync()
    {
        if (!_started)
        {
            _started = true;
            try
            {
                await _lan.StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Status?.Invoke($"Clipboard LAN/P2P 시작 실패; BLE fallback 유지: {ex.Message}");
            }
        }

        try
        {
            _direct.Start();
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Clipboard P2P fast link 시작 실패; LAN/BLE fallback 유지: {ex.Message}");
        }

        try
        {
            await _ble.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Clipboard BLE fallback 시작 실패: {ex.Message}");
        }

        await _relay.StartAsync().ConfigureAwait(false);
    }

    public async Task SetActiveAsync(bool active)
    {
        if (active)
        {
            await StartAsync().ConfigureAwait(false);
            return;
        }

        _ble.StopAdvertising();
        _direct.Stop();
        _lan.Deactivate();
        await _relay.StopConnectionAsync().ConfigureAwait(false);
        Status?.Invoke("기기간 복붙 비활성화 · Bluetooth/P2P/Relay 연결 중지");
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

        await _send.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_lan.HasClient(id))
            {
                try
                {
                    await _lan.SendAsync(id, content, token).ConfigureAwait(false);
                    Status?.Invoke($"Clipboard 전송 완료 · LAN/P2P · {Describe(content)}");
                    return true;
                }
                catch (Exception ex)
                {
                    Status?.Invoke($"LAN/P2P 전송 실패, BLE fallback 확인: {ex.Message}");
                }
            }

            var bleAllowed =
                content.Kind == ClipboardContentKind.TextUtf8 ||
                content.Data.Length <= bleImageLimitBytes;

            if (bleAllowed && _ble.HasSubscriber(id))
            {
                try
                {
                    await _ble.SendAsync(id, content, token).ConfigureAwait(false);
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
                    $"이미지 {content.Data.Length / 1024:N0} KiB는 BLE 제한을 초과했습니다. LAN/P2P 또는 Relay를 확인합니다.");
            }

            if (_relay.Connected)
            {
                try
                {
                    await _relay.SendAsync(id, content, token).ConfigureAwait(false);
                    Status?.Invoke($"Clipboard 전송 완료 · Relay(E2E) · {Describe(content)}");
                    return true;
                }
                catch (Exception ex)
                {
                    Status?.Invoke($"Clipboard Relay 전송 실패: {ex.Message}");
                }
            }

            return false;
        }
        finally
        {
            _send.Release();
        }
    }

    private void OnReceived(string deviceId, ClipboardContent content)
    {
        if (!_enabled() ||
            !string.Equals(_approvedDeviceId(), deviceId, StringComparison.Ordinal))
            return;

        Status?.Invoke($"Clipboard 수신 · {Describe(content)}");
        Received?.Invoke(content);
    }

    private static string Describe(ClipboardContent content) =>
        content.Kind == ClipboardContentKind.TextUtf8
            ? $"텍스트 {content.Data.Length:N0} bytes"
            : $"이미지 {content.Kind} {content.Data.Length / 1024.0:N1} KiB";

    public async ValueTask DisposeAsync()
    {
        await _lan.DisposeAsync().ConfigureAwait(false);
        await _relay.DisposeAsync().ConfigureAwait(false);
        _ble.Dispose();
        _direct.Dispose();
        _send.Dispose();
    }
}
