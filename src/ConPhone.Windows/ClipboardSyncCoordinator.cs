using ConPhone.Core;

namespace ConPhone.Windows;

public sealed class ClipboardSyncCoordinator : IAsyncDisposable
{
    private readonly Func<string?> _approvedDeviceId;
    private readonly BleClipboardService _ble;
    private readonly LanClipboardServer _lan;
    private readonly SemaphoreSlim _send = new(1, 1);
    private bool _started;

    public event Action<ClipboardContent>? Received;
    public event Action<string>? Status;

    public ClipboardSyncCoordinator(Func<string?> approvedDeviceId)
    {
        _approvedDeviceId = approvedDeviceId;
        _ble = new BleClipboardService(approvedDeviceId);
        _lan = new LanClipboardServer(approvedDeviceId);

        _ble.Status += text => Status?.Invoke(text);
        _lan.Status += text => Status?.Invoke(text);
        _ble.Received += OnReceived;
        _lan.Received += OnReceived;
    }

    public int BleSubscribers => _ble.Subscribers;

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        try
        {
            await _lan.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Clipboard LAN/P2P 시작 실패; BLE fallback 유지: {ex.Message}");
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

    public async Task<bool> SendAsync(
        ClipboardContent content,
        int bleImageLimitBytes,
        CancellationToken token)
    {
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
                    $"이미지 {content.Data.Length / 1024:N0} KiB는 BLE 제한을 초과했습니다. LAN/P2P 연결을 기다립니다.");
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
        if (!string.Equals(_approvedDeviceId(), deviceId, StringComparison.Ordinal))
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
        _send.Dispose();
    }
}
