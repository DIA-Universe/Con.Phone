using ConPhone.Core;

namespace ConPhone.Windows;

public sealed class ClipboardSyncCoordinator : IAsyncDisposable
{
    private readonly Func<string?> _approvedDeviceId;
    private readonly Func<bool> _enabled;
    private readonly DirectClipboardLink _direct;
    private readonly BleClipboardService _ble;
    private readonly LanClipboardServer _lan;
    private readonly SemaphoreSlim _send = new(1, 1);
    private bool _started;

    public event Action<ClipboardContent>? Received;
    public event Action<string>? Status;

    public ClipboardSyncCoordinator(
        Func<string?> approvedDeviceId,
        Func<bool> enabled)
    {
        _approvedDeviceId = approvedDeviceId;
        _enabled = enabled;
        _direct = new DirectClipboardLink();
        _ble = new BleClipboardService(approvedDeviceId, enabled, GetDirectLinkConfig);
        _lan = new LanClipboardServer(approvedDeviceId, enabled);

        _direct.Status += text => Status?.Invoke(text);
        _ble.Status += text => Status?.Invoke(text);
        _lan.Status += text => Status?.Invoke(text);
        _ble.Received += OnReceived;
        _lan.Received += OnReceived;
    }

    public int BleSubscribers => _ble.Subscribers;

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
        Status?.Invoke("기기간 복붙 비활성화 · Bluetooth/P2P 연결 중지");
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

        await _send.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_lan.HasClient(id))
            {
                try
                {
                    await _lan.SendAsync(id, content, token).ConfigureAwait(false);
                    Status?.Invoke($"Clipboard 전송 완료 · P2P · {Describe(content)}");
                    return true;
                }
                catch (Exception ex)
                {
                    Status?.Invoke($"P2P 전송 실패, BLE fallback 확인: {ex.Message}");
                }
            }

            var bleAllowed =
                content.Kind == ClipboardContentKind.TextUtf8 ||
                content.Data.Length <= bleImageLimitBytes;

            if (!bleAllowed && !_lan.HasClient(id))
            {
                _ = GetDirectLinkConfig();
                Status?.Invoke("큰 이미지 전송을 위해 P2P 고속 링크 연결을 기다립니다.");

                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
                while (!_lan.HasClient(id) && DateTimeOffset.UtcNow < deadline)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(125, token).ConfigureAwait(false);
                }

                if (_lan.HasClient(id))
                {
                    try
                    {
                        await _lan.SendAsync(id, content, token).ConfigureAwait(false);
                        Status?.Invoke($"Clipboard 전송 완료 · P2P · {Describe(content)}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Status?.Invoke($"P2P 이미지 전송 실패: {ex.Message}");
                    }
                }
            }

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
                    $"이미지 {content.Data.Length / 1024:N0} KiB는 BLE 제한을 초과했고 P2P 연결도 준비되지 않았습니다.");
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
        _ble.Dispose();
        _direct.Dispose();
        _send.Dispose();
    }
}
