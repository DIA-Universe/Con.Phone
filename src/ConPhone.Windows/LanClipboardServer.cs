using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ConPhone.Core;

namespace ConPhone.Windows;

public sealed class LanClipboardServer : IAsyncDisposable
{
    public const int DataPort = 47911;

    private static readonly byte[] Hello = "CPHELLO1"u8.ToArray();
    private static readonly byte[] Ready = "CPREADY1"u8.ToArray();
    private static readonly byte[] AadPrefix = "ConPhone-Clipboard-P2P-v1"u8.ToArray();

    private readonly Func<string?> _approvedDeviceId;
    private readonly Func<bool> _enabled;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _clientGate = new();
    private ClientConnection? _client;
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public event Action<string, ClipboardContent>? Received;
    public event Action<string>? Status;

    public LanClipboardServer(Func<string?> approvedDeviceId, Func<bool> enabled)
    {
        _approvedDeviceId = approvedDeviceId;
        _enabled = enabled;
    }

    public bool HasClient(string deviceId)
    {
        lock (_clientGate)
            return _client is { Closed: false } client &&
                   string.Equals(client.DeviceId, deviceId, StringComparison.Ordinal);
    }

    public Task StartAsync()
    {
        if (_listener is not null) return Task.CompletedTask;

        _listener = new TcpListener(IPAddress.Any, DataPort);
        _listener.Start();

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_lifetime.Token));
        Status?.Invoke($"Clipboard P2P 보안 채널 대기 · TCP {DataPort}");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var tcp = await _listener!.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(tcp, token), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Status?.Invoke($"Clipboard P2P 연결 수락 오류: {ex.Message}");
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken token)
    {
        using (tcp)
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();
            if (!_enabled()) return;
            var approved = _approvedDeviceId();
            if (approved is null) return;

            var approvedHash = SHA256.HashData(Encoding.UTF8.GetBytes(approved));
            var secret = ClipboardPairingSecretStore.GetOrCreate(approved);

            var hello = new byte[8 + 32 + 16 + 32];
            if (!await ReadExactAsync(stream, hello, token).ConfigureAwait(false)) return;
            if (!hello.AsSpan(0, 8).SequenceEqual(Hello)) return;

            var deviceHash = hello.AsSpan(8, 32);
            var clientNonce = hello.AsSpan(40, 16).ToArray();
            var clientMac = hello.AsSpan(56, 32);

            if (!CryptographicOperations.FixedTimeEquals(deviceHash, approvedHash))
                return;

            var clientSigned = new byte[8 + 32 + 16];
            Hello.CopyTo(clientSigned, 0);
            approvedHash.CopyTo(clientSigned, 8);
            clientNonce.CopyTo(clientSigned, 40);
            var expectedMac = HMACSHA256.HashData(secret, clientSigned);
            if (!CryptographicOperations.FixedTimeEquals(clientMac, expectedMac))
                return;

            var serverNonce = RandomNumberGenerator.GetBytes(16);
            var readySigned = new byte[8 + 16 + 16];
            Ready.CopyTo(readySigned, 0);
            clientNonce.CopyTo(readySigned, 8);
            serverNonce.CopyTo(readySigned, 24);
            var serverMac = HMACSHA256.HashData(secret, readySigned);

            var ready = new byte[8 + 16 + 32];
            Ready.CopyTo(ready, 0);
            serverNonce.CopyTo(ready, 8);
            serverMac.CopyTo(ready, 24);
            await stream.WriteAsync(ready, token).ConfigureAwait(false);

            var connection = new ClientConnection(approved, tcp, stream, secret, approvedHash);
            ClientConnection? replaced;
            lock (_clientGate)
            {
                replaced = _client;
                _client = connection;
            }
            replaced?.Close();

            Status?.Invoke("Clipboard P2P 보안 채널 연결됨.");

            var reassembler = new ClipboardReassembler();
            try
            {
                while (!token.IsCancellationRequested && !connection.Closed)
                {
                    var frame = await connection.ReadEncryptedAsync(token).ConfigureAwait(false);
                    if (frame is null) break;

                    var chunk = ClipboardWireCodec.Decode(frame);
                    var content = reassembler.Add(chunk);
                    if (content is not null)
                        Received?.Invoke(approved, content);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or CryptographicException or InvalidDataException)
            {
                Status?.Invoke($"Clipboard P2P 연결 종료: {ex.Message}");
            }
            finally
            {
                connection.Close();
                lock (_clientGate)
                {
                    if (ReferenceEquals(_client, connection))
                        _client = null;
                }
            }
        }
    }

    public async Task SendAsync(string deviceId, ClipboardContent content, CancellationToken token)
    {
        if (!_enabled())
            throw new InvalidOperationException("Clipboard sync is disabled.");

        ClientConnection client;
        lock (_clientGate)
        {
            client = _client is { Closed: false } current &&
                     string.Equals(current.DeviceId, deviceId, StringComparison.Ordinal)
                ? current
                : throw new InvalidOperationException("Clipboard P2P 연결이 없습니다.");
        }

        foreach (var frame in ClipboardWireCodec.Encode(content))
            await client.WriteEncryptedAsync(frame, token).ConfigureAwait(false);
    }

    public void Deactivate()
    {
        ClientConnection? client;
        lock (_clientGate)
        {
            client = _client;
            _client = null;
        }

        client?.Close();
        Status?.Invoke("Clipboard P2P 연결 종료.");
    }

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }

    private sealed class ClientConnection
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly byte[] _secret;
        private readonly byte[] _aad;
        private readonly SemaphoreSlim _send = new(1, 1);
        private int _closed;

        public string DeviceId { get; }
        public bool Closed => Volatile.Read(ref _closed) != 0;

        public ClientConnection(
            string deviceId,
            TcpClient tcp,
            NetworkStream stream,
            byte[] secret,
            byte[] deviceHash)
        {
            DeviceId = deviceId;
            _tcp = tcp;
            _stream = stream;
            _secret = secret.ToArray();
            _aad = new byte[AadPrefix.Length + deviceHash.Length];
            AadPrefix.CopyTo(_aad, 0);
            deviceHash.CopyTo(_aad, AadPrefix.Length);
        }

        public async Task WriteEncryptedAsync(byte[] plaintext, CancellationToken token)
        {
            await _send.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (Closed) throw new IOException("Clipboard connection closed.");

                var nonce = RandomNumberGenerator.GetBytes(12);
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[16];
                using (var aes = new AesGcm(_secret, 16))
                    aes.Encrypt(nonce, plaintext, ciphertext, tag, _aad);

                var bodyLength = 12 + ciphertext.Length + 16;
                var prefix = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(prefix, bodyLength);
                await _stream.WriteAsync(prefix, token).ConfigureAwait(false);
                await _stream.WriteAsync(nonce, token).ConfigureAwait(false);
                await _stream.WriteAsync(ciphertext, token).ConfigureAwait(false);
                await _stream.WriteAsync(tag, token).ConfigureAwait(false);
                await _stream.FlushAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _send.Release();
            }
        }

        public async Task<byte[]?> ReadEncryptedAsync(CancellationToken token)
        {
            var prefix = new byte[4];
            if (!await ReadExactAsync(_stream, prefix, token).ConfigureAwait(false))
                return null;

            var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (bodyLength < 28 || bodyLength > ClipboardContent.MaxImageBytes + 1024 * 1024)
                throw new InvalidDataException("Invalid encrypted clipboard frame length.");

            var body = new byte[bodyLength];
            if (!await ReadExactAsync(_stream, body, token).ConfigureAwait(false))
                return null;

            var nonce = body.AsSpan(0, 12);
            var cipherLength = bodyLength - 28;
            var ciphertext = body.AsSpan(12, cipherLength);
            var tag = body.AsSpan(12 + cipherLength, 16);
            var plaintext = new byte[cipherLength];

            using (var aes = new AesGcm(_secret, 16))
                aes.Decrypt(nonce, ciphertext, tag, plaintext, _aad);

            return plaintext;
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _tcp.Close(); } catch { }
            _send.Dispose();
            CryptographicOperations.ZeroMemory(_secret);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();

        ClientConnection? client;
        lock (_clientGate)
        {
            client = _client;
            _client = null;
        }
        client?.Close();

        try { _listener?.Stop(); } catch { }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; }
            catch (OperationCanceledException) { }
        }

        _lifetime.Dispose();
    }
}
