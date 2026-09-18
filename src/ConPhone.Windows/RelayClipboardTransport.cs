using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using ConPhone.Core;

namespace ConPhone.Windows;

public sealed class RelayClipboardTransport : IAsyncDisposable
{
    private readonly Func<string?> _approvedDeviceId;
    private readonly Func<bool> _enabled;
    private readonly Func<string?> _relayUrl;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _gate = new();

    private Task? _loop;
    private ClientWebSocket? _socket;
    private string? _connectedDeviceId;
    private byte[]? _secret;
    private byte[]? _deviceHash;
    private ClipboardReassembler _reassembler = new();

    public event Action<string, ClipboardContent>? Received;
    public event Action<string>? Status;

    public RelayClipboardTransport(
        Func<string?> approvedDeviceId,
        Func<bool> enabled,
        Func<string?> relayUrl)
    {
        _approvedDeviceId = approvedDeviceId;
        _enabled = enabled;
        _relayUrl = relayUrl;
    }

    public bool Connected
    {
        get
        {
            lock (_gate)
                return _socket?.State == WebSocketState.Open;
        }
    }

    public Task StartAsync()
    {
        if (_loop is null)
            _loop = Task.Run(() => ConnectionLoopAsync(_lifetime.Token));
        return Task.CompletedTask;
    }

    public async Task StopConnectionAsync()
    {
        ClientWebSocket? socket;
        lock (_gate)
        {
            socket = _socket;
            _socket = null;
            ClearContextLocked();
        }

        if (socket is not null)
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "disabled",
                        CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
            socket.Dispose();
        }
    }

    public async Task SendAsync(
        string deviceId,
        ClipboardContent content,
        CancellationToken token)
    {
        ClientWebSocket socket;
        byte[] secret;
        byte[] deviceHash;

        lock (_gate)
        {
            if (_socket?.State != WebSocketState.Open ||
                !string.Equals(_connectedDeviceId, deviceId, StringComparison.Ordinal) ||
                _secret is null ||
                _deviceHash is null)
                throw new InvalidOperationException("인터넷 Relay가 연결되어 있지 않습니다.");

            socket = _socket;
            secret = _secret.ToArray();
            deviceHash = _deviceHash.ToArray();
        }

        await _sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var frame in ClipboardWireCodec.Encode(content))
            {
                var encrypted = ClipboardRelayCrypto.EncryptFrame(secret, deviceHash, frame);
                await socket.SendAsync(
                    encrypted,
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    token).ConfigureAwait(false);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            _sendGate.Release();
        }
    }

    private async Task ConnectionLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_enabled())
                {
                    await StopConnectionAsync().ConfigureAwait(false);
                    await Task.Delay(1000, token).ConfigureAwait(false);
                    continue;
                }

                var deviceId = _approvedDeviceId();
                var baseUrl = _relayUrl()?.Trim();
                if (string.IsNullOrWhiteSpace(deviceId) ||
                    string.IsNullOrWhiteSpace(baseUrl) ||
                    !TryBuildBaseUri(baseUrl, out var relayBase))
                {
                    await StopConnectionAsync().ConfigureAwait(false);
                    await Task.Delay(1500, token).ConfigureAwait(false);
                    continue;
                }

                if (Connected &&
                    string.Equals(_connectedDeviceId, deviceId, StringComparison.Ordinal))
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                    continue;
                }

                await StopConnectionAsync().ConfigureAwait(false);
                await ConnectAndReceiveAsync(deviceId, relayBase!, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Status?.Invoke($"Clipboard Relay 재연결 대기: {ex.Message}");
                await StopConnectionAsync().ConfigureAwait(false);
                try { await Task.Delay(2500, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task ConnectAndReceiveAsync(
        string deviceId,
        Uri relayBase,
        CancellationToken token)
    {
        var deviceHash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId));
        var secret = ClipboardPairingSecretStore.GetOrCreate(deviceId);
        var identity = ClipboardRelayCrypto.DeriveIdentity(secret, deviceHash);
        var uri = BuildRelayUri(relayBase, identity, "pc");

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(uri, token).ConfigureAwait(false);

        lock (_gate)
        {
            _socket = socket;
            _connectedDeviceId = deviceId;
            _secret = secret.ToArray();
            _deviceHash = deviceHash.ToArray();
            _reassembler = new ClipboardReassembler();
        }

        Status?.Invoke("Clipboard 인터넷 Relay 연결됨 · 서버는 암호문만 중계");

        try
        {
            await ReceiveLoopAsync(socket, deviceId, secret, deviceHash, token)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                    ClearContextLocked();
                }
            }

            socket.Dispose();
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        string deviceId,
        byte[] secret,
        byte[] deviceHash,
        CancellationToken token)
    {
        var buffer = new byte[2 * 1024 * 1024 + 64];

        while (!token.IsCancellationRequested &&
               socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                if (result.MessageType != WebSocketMessageType.Binary)
                    throw new InvalidDataException("Relay accepted a non-binary frame.");

                message.Write(buffer, 0, result.Count);
                if (message.Length > 2 * 1024 * 1024 + 64)
                    throw new InvalidDataException("Relay frame exceeds the safety limit.");
            }
            while (!result.EndOfMessage);

            var plaintext = ClipboardRelayCrypto.DecryptFrame(
                secret,
                deviceHash,
                message.GetBuffer().AsSpan(0, checked((int)message.Length)));

            var chunk = ClipboardWireCodec.Decode(plaintext);
            ClipboardContent? content;
            lock (_gate)
                content = _reassembler.Add(chunk);

            if (content is not null && _enabled() &&
                string.Equals(_approvedDeviceId(), deviceId, StringComparison.Ordinal))
                Received?.Invoke(deviceId, content);
        }
    }

    private void ClearContextLocked()
    {
        _connectedDeviceId = null;

        if (_secret is not null)
            CryptographicOperations.ZeroMemory(_secret);

        _secret = null;
        _deviceHash = null;
        _reassembler = new ClipboardReassembler();
    }

    private static bool TryBuildBaseUri(string text, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
            return false;

        var scheme = parsed.Scheme.ToLowerInvariant();
        if (scheme is not ("https" or "http" or "wss" or "ws"))
            return false;

        var builder = new UriBuilder(parsed)
        {
            Scheme = scheme switch
            {
                "https" => "wss",
                "http" => "ws",
                _ => scheme
            }
        };

        if ((builder.Scheme == "wss" && builder.Port == 443) ||
            (builder.Scheme == "ws" && builder.Port == 80))
            builder.Port = -1;

        uri = builder.Uri;
        return true;
    }

    private static Uri BuildRelayUri(
        Uri baseUri,
        ClipboardRelayIdentity identity,
        string side)
    {
        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');
        builder.Path = path.EndsWith("/relay", StringComparison.OrdinalIgnoreCase)
            ? path
            : path + "/relay";

        builder.Query =
            $"room={Uri.EscapeDataString(identity.Room)}&" +
            $"auth={Uri.EscapeDataString(identity.Auth)}&" +
            $"side={Uri.EscapeDataString(side)}";
        return builder.Uri;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await StopConnectionAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _sendGate.Dispose();
        _lifetime.Dispose();
    }
}
