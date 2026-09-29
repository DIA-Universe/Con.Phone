using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using ConPhone.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Credentials;
using Windows.Foundation;

namespace ConPhone.Windows;

public static class ClipboardPairingSecretStore
{
    private const string Resource = "ConPhone.Clipboard";

    public static string DeviceHash(string deviceId) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(deviceId)));

    public static byte[] GetOrCreate(string deviceId)
    {
        var user = DeviceHash(deviceId);
        var vault = new PasswordVault();

        try
        {
            var existing = vault.Retrieve(Resource, user);
            existing.RetrievePassword();
            var decoded = Convert.FromBase64String(existing.Password);
            if (decoded.Length == 32) return decoded;
        }
        catch (Exception)
        {
            // No credential yet (or an invalid stale value): generate a new device secret below.
        }

        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            foreach (var item in vault.FindAllByResource(Resource).Where(x => x.UserName == user).ToArray())
                vault.Remove(item);
        }
        catch (COMException)
        {
        }

        vault.Add(new PasswordCredential(Resource, user, Convert.ToBase64String(secret)));
        return secret;
    }

    public static void Forget(string deviceId)
    {
        var user = DeviceHash(deviceId);
        var vault = new PasswordVault();
        try
        {
            foreach (var item in vault.FindAllByResource(Resource).Where(x => x.UserName == user).ToArray())
                vault.Remove(item);
        }
        catch (COMException)
        {
        }
    }
}

public sealed class BleClipboardService : IDisposable
{
    private readonly ReliableBleSender _reliableSender = new();
    public static readonly Guid ServiceUuid = Guid.Parse("67A7F001-7E5B-4A2E-9B8B-1C9A5B6D1001");
    public static readonly Guid TxUuid = Guid.Parse("67A7F002-7E5B-4A2E-9B8B-1C9A5B6D1001");
    public static readonly Guid RxUuid = Guid.Parse("67A7F003-7E5B-4A2E-9B8B-1C9A5B6D1001");
    public static readonly Guid SecretUuid = Guid.Parse("67A7F004-7E5B-4A2E-9B8B-1C9A5B6D1001");
    public static readonly Guid CapabilitiesUuid = Guid.Parse("67A7F005-7E5B-4A2E-9B8B-1C9A5B6D1001");
    public static readonly Guid DirectLinkUuid = Guid.Parse("67A7F006-7E5B-4A2E-9B8B-1C9A5B6D1001");

    private readonly Func<string?> _approvedDeviceId;
    private readonly Func<string, bool> _trustedDevice;
    private readonly Func<bool> _enabled;
    private readonly Func<int> _bleImageLimitBytes;
    private readonly Func<bool> _p2pRequested;
    private readonly Func<DirectLinkConfig?> _directLinkConfig;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _notifyGate = new(1, 1);
    private readonly object _ackGate = new();
    private readonly Dictionary<(string DeviceId, Guid TransferId), TaskCompletionSource<bool>> _ackWaiters = [];
    private readonly ConcurrentDictionary<string, BleFragmentReassembler> _bleReassemblers = new();
    private readonly ConcurrentDictionary<string, ClipboardReassembler> _clipboardReassemblers = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _readyAfter = new();

    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _tx;
    private GattLocalCharacteristic? _rx;
    private GattLocalCharacteristic? _secret;
    private GattLocalCharacteristic? _capabilities;
    private GattLocalCharacteristic? _directLink;
    private bool _disposed;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private BleResourceLifetime _resources;
    private BleResourceLifetime _sends = new();
    private readonly object _peerGate = new();
    private readonly Dictionary<string, Subscription> _subscriptions = [];
    private sealed record Subscription(SharedBleResource<GattSession>.Lease Lease, BleResourceLifetime Lifetime);
    private long _stopGeneration;
    private volatile bool _published;
    private bool _prepared;
    private readonly Action<GattServiceProvider, GattServiceProviderAdvertisingParameters> _startAdvertising;

    public Func<string, ClipboardContent, Task<bool>>? ReceiveContentAsync { get; set; }
    public event Action<string>? Status;

    public BleClipboardService(
        Func<string?> approvedDeviceId,
        Func<string, bool> trustedDevice,
        Func<bool> enabled,
        Func<int> bleImageLimitBytes,
        Func<bool> p2pRequested,
        Func<DirectLinkConfig?> directLinkConfig,
        Action<GattServiceProvider, GattServiceProviderAdvertisingParameters>? startAdvertising = null)
    {
        _startAdvertising = startAdvertising ?? ((provider, parameters) => provider.StartAdvertising(parameters));
        _resources = new BleResourceLifetime(text => Status?.Invoke(text));
        _approvedDeviceId = approvedDeviceId;
        _trustedDevice = trustedDevice;
        _enabled = enabled;
        _bleImageLimitBytes = bleImageLimitBytes;
        _p2pRequested = p2pRequested;
        _directLinkConfig = directLinkConfig;
    }

    public bool Running => _published || BleNativeState.Read(() => _provider?.AdvertisementStatus is
        GattServiceProviderAdvertisementStatus.Started or
        GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData, false);

    public int Subscribers => LiveClients(_tx).Count;

    public async Task PrepareAsync()
    {
        var stopGeneration = Interlocked.Read(ref _stopGeneration);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { if (stopGeneration == Interlocked.Read(ref _stopGeneration)) await PrepareCoreAsync().ConfigureAwait(false); }
        catch { CleanupServerResources(); throw; }
        finally { _lifecycleGate.Release(); }
    }

    private async Task PrepareCoreAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_provider is not null && !_resources.IsDisposed) return;
        if (_resources.IsDisposed) {
            CleanupServerResources();
            _resources = new BleResourceLifetime(text => Status?.Invoke(text));
        }
        var lifetime = _resources;
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var startupCancellation = startupTimeout.Token.Register(lifetime.Dispose);

        var created = await GattServiceProvider.CreateAsync(ServiceUuid).AsTask(_resources.Token);
        if (created.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Clipboard BLE 서비스 생성 실패: {created.Error}");

        _provider = created.ServiceProvider;
        var provider = _provider;
        lifetime.Register(() => provider.StopAdvertising());
        TypedEventHandler<GattServiceProvider, GattServiceProviderAdvertisementStatusChangedEventArgs> advertisementChanged = (_, args) =>
        {
            if (lifetime.IsDisposed || !ReferenceEquals(provider, _provider)) return;
            if (args.Status is GattServiceProviderAdvertisementStatus.Stopped or
                GattServiceProviderAdvertisementStatus.Aborted) _published = false;
            Status?.Invoke($"Clipboard BLE 서비스 상태: {args.Status} ({args.Error})");
        };

        provider.AdvertisementStatusChanged += advertisementChanged;
        lifetime.Register(() => provider.AdvertisementStatusChanged -= advertisementChanged);

        _tx = await CreateCharacteristicAsync(
            TxUuid,
            GattCharacteristicProperties.Notify,
            GattProtectionLevel.EncryptionRequired,
            GattProtectionLevel.EncryptionRequired);

        _rx = await CreateCharacteristicAsync(
            RxUuid,
            GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse,
            GattProtectionLevel.EncryptionRequired,
            GattProtectionLevel.EncryptionRequired);

        _secret = await CreateCharacteristicAsync(
            SecretUuid,
            GattCharacteristicProperties.Read,
            GattProtectionLevel.EncryptionRequired,
            GattProtectionLevel.EncryptionRequired);

        _capabilities = await CreateCharacteristicAsync(
            CapabilitiesUuid,
            GattCharacteristicProperties.Read,
            GattProtectionLevel.EncryptionRequired,
            GattProtectionLevel.EncryptionRequired);

        _directLink = await CreateCharacteristicAsync(
            DirectLinkUuid,
            GattCharacteristicProperties.Read,
            GattProtectionLevel.EncryptionRequired,
            GattProtectionLevel.EncryptionRequired);

        var tx = _tx; var rx = _rx; var secret = _secret;
        var capabilities = _capabilities; var direct = _directLink;
        TypedEventHandler<GattLocalCharacteristic, object> subscribed = (_, _) => {
            if (!lifetime.IsDisposed) RefreshSubscriptions();
        };
        tx.SubscribedClientsChanged += subscribed;
        lifetime.Register(() => tx.SubscribedClientsChanged -= subscribed);
        rx.WriteRequested += RxWriteRequested;
        lifetime.Register(() => rx.WriteRequested -= RxWriteRequested);
        secret.ReadRequested += SecretReadRequested;
        lifetime.Register(() => secret.ReadRequested -= SecretReadRequested);
        capabilities.ReadRequested += CapabilitiesReadRequested;
        lifetime.Register(() => capabilities.ReadRequested -= CapabilitiesReadRequested);
        direct.ReadRequested += DirectLinkReadRequested;
        lifetime.Register(() => direct.ReadRequested -= DirectLinkReadRequested);
        lifetime.Token.ThrowIfCancellationRequested();
        _prepared = true;
    }

    public async Task StartAsync()
    {
        var stopGeneration = Interlocked.Read(ref _stopGeneration);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try {
            if (stopGeneration != Interlocked.Read(ref _stopGeneration)) return;
            await PrepareCoreAsync().ConfigureAwait(false);
            _resources.Token.ThrowIfCancellationRequested();
            StartAdvertising();
        }
        catch {
            if (!_prepared || _resources.IsDisposed) CleanupServerResources();
            throw;
        }
        finally { _lifecycleGate.Release(); }
    }

    private void StartAdvertising()
    {
        if (_provider is null || Running) return;

        _startAdvertising(_provider, ConnectionStartup.ClipboardAdvertisement());
        // Non-connectable publication can keep AdvertisementStatus at Created.
        // It is already registered; repeating StartAdvertising then throws E_ILLEGAL_METHOD_CALL.
        _published = true;
        Status?.Invoke("Clipboard BLE 서비스 준비 · HID 검색 신호를 함께 사용합니다.");
    }

    private async Task<GattLocalCharacteristic> CreateCharacteristicAsync(
        Guid uuid,
        GattCharacteristicProperties properties,
        GattProtectionLevel readProtection,
        GattProtectionLevel writeProtection)
    {
        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = properties,
            ReadProtectionLevel = readProtection,
            WriteProtectionLevel = writeProtection
        };

        var result = await _provider!.Service.CreateCharacteristicAsync(uuid, parameters).AsTask(_resources.Token);
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Clipboard BLE characteristic {uuid}: {result.Error}");

        return result.Characteristic;
    }

    private async void SecretReadRequested(
        GattLocalCharacteristic sender,
        GattReadRequestedEventArgs args)
    {
        var lifetime = _resources;
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync().AsTask(lifetime.Token);
            if (request is null) return;
            if (lifetime.IsDisposed || !ReferenceEquals(sender, _secret)) {
                request.RespondWithProtocolError(0x0E);
                return;
            }

            var id = args.Session.DeviceId.Id;
            if (!IsTrusted(id))
            {
                Status?.Invoke("Clipboard 인증 거부 · 현재 연결한 휴대폰을 PC에서 등록해 주세요.");
                request.RespondWithProtocolError(0x05);
                return;
            }

            var deviceHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id));
            var secret = ClipboardPairingSecretStore.GetOrCreate(id);
            var credential = new byte[64];
            deviceHash.CopyTo(credential, 0);
            secret.CopyTo(credential, 32);
            RespondRead(request, credential);
            if (request.Offset == 0) Status?.Invoke("Clipboard 인증 정보 읽기 응답 완료");
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Clipboard pairing secret 읽기 실패: {ex.Message}");
        }
        finally
        {
            try { deferral.Complete(); } catch (Exception ex) { Status?.Invoke($"[BLE] deferral completion HRESULT=0x{ex.HResult:X8}"); }
        }
    }

    private async void CapabilitiesReadRequested(
        GattLocalCharacteristic sender,
        GattReadRequestedEventArgs args)
    {
        var lifetime = _resources;
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync().AsTask(lifetime.Token);
            if (request is null) return;
            if (lifetime.IsDisposed || !ReferenceEquals(sender, _capabilities)) {
                request.RespondWithProtocolError(0x0E);
                return;
            }

            if (!IsTrusted(args.Session.DeviceId.Id))
            {
                request.RespondWithProtocolError(0x05);
                return;
            }

            // protocol v2 optional extension:
            // [0]=version, [1]=text, [2]=image, [3]=direct P2P,
            // [4..7]=BLE image fallback limit in bytes, little-endian,
            // [8]=1 when this trusted peer is the current active target,
            // [9]=1 only when Windows currently requests the Direct P2P fast path.
            var limit = Math.Clamp(
                _bleImageLimitBytes(),
                64 * 1024,
                4 * 1024 * 1024);

            var response = new byte[10] { 2, 1, 0, 1, 0, 0, 0, 0, 0, 0 };
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                response.AsSpan(4, 4),
                limit);
            response[8] = IsActive(args.Session.DeviceId.Id)
                ? (byte)1
                : (byte)0;
            response[9] =
                response[8] == 1 && _p2pRequested()
                    ? (byte)1
                    : (byte)0;
            var subscription = FindSubscription(args.Session.DeviceId.Id, args.Session);
            lock (_peerGate)
            {
                if (lifetime.IsDisposed || subscription is null || subscription.Lifetime.IsDisposed) {
                    request.RespondWithProtocolError(0x0E);
                    return;
                }
                RespondRead(request, response);
                if (request.Offset == 0 && response[8] == 1 &&
                    _readyAfter.TryAdd(args.Session.DeviceId.Id, DateTimeOffset.UtcNow.AddSeconds(1)))
                    Status?.Invoke("Clipboard 활성 대상 준비 응답 완료 · 전송 대기 해제");
            }
        }
        catch (Exception ex) { Status?.Invoke($"[BLE] capabilities read HRESULT=0x{ex.HResult:X8}"); }
        finally
        {
            try { deferral.Complete(); } catch (Exception ex) { Status?.Invoke($"[BLE] deferral completion HRESULT=0x{ex.HResult:X8}"); }
        }
    }

    private async void DirectLinkReadRequested(
        GattLocalCharacteristic sender,
        GattReadRequestedEventArgs args)
    {
        var lifetime = _resources;
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync().AsTask(lifetime.Token);
            if (request is null) return;
            if (lifetime.IsDisposed || !ReferenceEquals(sender, _directLink)) {
                request.RespondWithProtocolError(0x0E);
                return;
            }

            if (!IsActive(args.Session.DeviceId.Id))
            {
                request.RespondWithProtocolError(0x05);
                return;
            }

            var config = _directLinkConfig();
            RespondRead(request,
                config is null
                    ? new byte[] { 0 }
                    : config.Encode());
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Clipboard P2P 설정 읽기 실패: {ex.Message}");
        }
        finally
        {
            try { deferral.Complete(); } catch (Exception ex) { Status?.Invoke($"[BLE] deferral completion HRESULT=0x{ex.HResult:X8}"); }
        }
    }

    internal static byte[] ReadSlice(byte[] value, int offset) => value.AsSpan(offset).ToArray();

    private static void RespondRead(GattReadRequest request, byte[] value)
    {
        if (request.Offset > value.Length) request.RespondWithProtocolError(0x07);
        else request.RespondWithValue(ReadSlice(value, (int)request.Offset).AsBuffer());
    }

    private async void RxWriteRequested(
        GattLocalCharacteristic sender,
        GattWriteRequestedEventArgs args)
    {
        Guid? ackToSend = null;
        string? ackDeviceId = null;
        Subscription? receiving = null;
        var lifetime = _resources;
        var deferral = args.GetDeferral();

        try
        {
            var request = await args.GetRequestAsync().AsTask(lifetime.Token);
            if (request is null) return;
            if (lifetime.IsDisposed || !ReferenceEquals(sender, _rx)) {
                request.RespondWithProtocolError(0x0E);
                return;
            }

            var id = args.Session.DeviceId.Id;
            if (!IsActive(id))
            {
                if (request.Option == GattWriteOption.WriteWithResponse)
                    request.RespondWithProtocolError(0x05);
                return;
            }

            receiving = FindSubscription(id, args.Session);
            if (receiving is null) {
                if (request.Option == GattWriteOption.WriteWithResponse) request.RespondWithProtocolError(0x0E);
                return;
            }
            var receiveToken = receiving.Lifetime.Token;
            receiveToken.ThrowIfCancellationRequested();
            var valid = false;
            try
            {
                var bytes = request.Value.ToArray();

                if (_reliableSender.Accept(id, bytes))
                {
                    valid = true;
                }
                else if (ClipboardAckCodec.TryDecode(bytes, out var ackTransferId))
                {
                    CompleteAck(id, ackTransferId);
                    valid = true;
                }
                else
                {
                    var fragment = BleFragmentCodec.Decode(bytes);
                    var ble = _bleReassemblers.GetOrAdd(
                        id,
                        _ => new BleFragmentReassembler());
                    byte[]? frame;
                    lock (ble) { receiveToken.ThrowIfCancellationRequested(); frame = ble.Add(fragment); }

                    if (frame is not null)
                    {
                        if (ClipboardAckCodec.TryDecode(
                                frame,
                                out var fragmentedAckTransferId))
                        {
                            CompleteAck(
                                id,
                                fragmentedAckTransferId);
                        }
                        else
                        {
                            var chunk = ClipboardWireCodec.Decode(frame);
                            var clipboard = _clipboardReassemblers.GetOrAdd(
                                id,
                                _ => new ClipboardReassembler());
                            ClipboardContent? content;
                            lock (clipboard) { receiveToken.ThrowIfCancellationRequested(); content = clipboard.Add(chunk); }

                            if (content is not null)
                            {
                                var receiver = ReceiveContentAsync;
                                if (receiver is null)
                                    throw new InvalidDataException(
                                        "Windows clipboard receiver is not ready.");

                                var applied = await receiver(
                                        id,
                                        content)
                                    .WaitAsync(
                                        TimeSpan.FromSeconds(8), receiveToken)
                                    .ConfigureAwait(false);

                                if (!applied)
                                    throw new InvalidDataException(
                                        "Windows clipboard apply failed.");

                                ackToSend = chunk.TransferId;
                                ackDeviceId = id;
                            }
                        }
                    }

                    valid = true;
                }
            }
            catch (Exception ex) when (
                ex is InvalidDataException or
                ArgumentException or
                TimeoutException)
            {
                Status?.Invoke(
                    $"Clipboard BLE 수신 프레임 거부: {ex.Message}");
            }

            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                if (valid) request.Respond();
                else request.RespondWithProtocolError(0x0D);
            }
        }
        catch (Exception ex)
        {
            Status?.Invoke(
                $"Clipboard BLE 수신 실패: {ex.Message}");
        }
        finally
        {
            // Complete the GATT write transaction before sending the application
            // notification ACK. Some Bluetooth stacks serialize server callbacks
            // and notifications behind this deferral.
            try { deferral.Complete(); } catch (Exception ex) { Status?.Invoke($"[BLE] deferral completion HRESULT=0x{ex.HResult:X8}"); }
        }

        if (ackToSend is { } transferId &&
            ackDeviceId is { } deviceId && receiving is not null && !receiving.Lifetime.IsDisposed)
        {
            try
            {
                using var ackTimeout =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(5));

                using var receivedAck = CancellationTokenSource.CreateLinkedTokenSource(ackTimeout.Token, receiving.Lifetime.Token);
                await SendAckAsync(
                        deviceId,
                        transferId,
                        receivedAck.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Status?.Invoke(
                    $"Clipboard BLE ACK 전송 실패 · 상대가 재시도할 수 있습니다: {ex.Message}");
            }
        }
    }

    private async Task SendAckAsync(
        string deviceId,
        Guid transferId,
        CancellationToken token)
    {
        if (_tx is null || !IsActive(deviceId))
            throw new InvalidOperationException(
                "Clipboard ACK를 보낼 활성 BLE 대상이 없습니다.");

        var client = LiveClients(_tx)
            .FirstOrDefault(x => SessionId(x) == deviceId)
            ?? throw new InvalidOperationException(
                "Clipboard ACK 대상이 BLE 알림을 구독하지 않았습니다.");

        var characteristic = _tx ?? throw new InvalidOperationException("Clipboard service unavailable.");
        var subscription = FindSubscription(deviceId, client.Session)
            ?? throw new InvalidOperationException("Clipboard subscription expired.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            token, _resources.Token, _sends.Token, subscription.Lifetime.Token);
        token = linked.Token;
        token.ThrowIfCancellationRequested();
        var maxGattValue = client.MaxNotificationSize;
        if (maxGattValue <= BleFragmentCodec.HeaderSize)
            throw new InvalidOperationException(
                $"BLE ACK 알림 크기가 너무 작습니다: {maxGattValue}");

        var ack = ClipboardAckCodec.Encode(transferId);
        var transportTransferId =
            BitConverter.ToUInt32(
                RandomNumberGenerator.GetBytes(4));

        foreach (var fragment in BleFragmentCodec.Split(
                     ack,
                     maxGattValue,
                     transportTransferId))
        {
            var result = await NotifySerializedAsync(characteristic, fragment, client, token).ConfigureAwait(false);

            if (result.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException(
                    $"Clipboard BLE ACK 전송 실패: {result.Status}");
        }
    }

    private async Task<GattClientNotificationResult> NotifySerializedAsync(
        GattLocalCharacteristic characteristic, byte[] packet, GattSubscribedClient client, CancellationToken token)
    {
        await _notifyGate.WaitAsync(token).ConfigureAwait(false);
        try {
            token.ThrowIfCancellationRequested();
            return await characteristic.NotifyValueAsync(packet.AsBuffer(), client).AsTask(token).ConfigureAwait(false);
        }
        finally { _notifyGate.Release(); }
    }

    public void CancelPendingSends(string reason)
    {
        BleResourceLifetime old;
        lock (_ackGate)
        {
            old = _sends;
            _sends = new BleResourceLifetime();
            FailAllAcks(new InvalidOperationException(reason));
        }
        old.Dispose();
    }

    public void StopAdvertising()
    {
        Interlocked.Increment(ref _stopGeneration);
        CleanupServerResources();
    }

    private void RefreshSubscriptions()
    {
        if (_disposed || _resources.IsDisposed) return;
        try {
            var live = LiveClients(_tx);
            lock (_peerGate)
            {
                if (_disposed || _resources.IsDisposed) return;
                foreach (var id in _subscriptions.Keys.ToArray())
                    if (!_subscriptions[id].Lease.IsActive || !live.Any(c => SessionId(c) == id))
                        RemoveSubscription(id, _subscriptions[id], detach: true,
                            retire: !_subscriptions[id].Lease.IsActive);
                foreach (var client in live)
                {
                    var id = SessionId(client);
                    if (id is null || _subscriptions.ContainsKey(id)) continue;
                    var lifetime = new BleResourceLifetime(text => Status?.Invoke(text));
                    var lease = SharedBleResource<GattSession>.Acquire(id, client.Session, BleNativeState.IsActive);
                    var session = lease.Resource;
                    lifetime.Register(lease.Dispose);
                    var subscription = new Subscription(lease, lifetime);
                    _subscriptions.Add(id, subscription);
                    TypedEventHandler<GattSession, GattSessionStatusChangedEventArgs> changed = (sender, args) => {
                        if (args.Status == GattSessionStatus.Closed) {
                            if (lease.IsActive || BleNativeState.IsActive(sender)) {
                                Status?.Invoke($"[BLE][Session {lifetime.Id}] late clipboard Closed callback; retire old observation without closing Active native session");
                            }
                            lock (_peerGate) RemoveSubscription(id, subscription, detach: true, retire: true);
                            RefreshSubscriptions();
                        }
                        Status?.Invoke($"[BLE][Session {lifetime.Id}] clipboard session={args.Status} status={args.Error}");
                    };
                    session.SessionStatusChanged += changed;
                    lifetime.Register(() => session.SessionStatusChanged -= changed);
                    Status?.Invoke($"[BLE][Session {lifetime.Id}/Link {lease.Generation}] notification subscribed");
                }
            }
        }
        catch (Exception ex) { Status?.Invoke($"[BLE] subscription refresh HRESULT=0x{ex.HResult:X8}"); }
    }

    private Subscription? FindSubscription(string id, GattSession session)
    {
        // Request callbacks and SubscribedClients need not share a managed wrapper.
        // Reject closed callbacks, then match the live peer and local generation.
        if (!BleNativeState.IsActive(session) || session.DeviceId.Id != id) return null;
        RefreshSubscriptions();
        lock (_peerGate) return _subscriptions.TryGetValue(id, out var current) &&
            current.Lease.IsActive && !current.Lifetime.IsDisposed ? current : null;
    }

    private void RemoveSubscription(string id, Subscription expected, bool detach = false, bool retire = false)
    {
        if (!_subscriptions.TryGetValue(id, out var current) || !ReferenceEquals(current, expected)) return;
        _subscriptions.Remove(id);
        if (retire) current.Lease.Retire();
        if (detach) current.Lease.Detach();
        current.Lifetime.Dispose();
        _readyAfter.TryRemove(id, out _);
        if (_bleReassemblers.TryGetValue(id, out var ble)) lock (ble) ble.ClearPending();
        if (_clipboardReassemblers.TryGetValue(id, out var wire)) lock (wire) wire.ClearPending();
        Status?.Invoke($"[BLE][Session {current.Lifetime.Id}] subscription released; pending operations cancelled");
    }

    private void CleanupServerResources()
    {
        _prepared = false;
        _resources.Dispose();
        _published = false;
        CancelPendingSends("BLE service cleanup");
        lock (_peerGate)
            foreach (var pair in _subscriptions.ToArray()) RemoveSubscription(pair.Key, pair.Value);
        _readyAfter.Clear();
        foreach (var ble in _bleReassemblers.Values) lock (ble) ble.ClearPending();
        foreach (var wire in _clipboardReassemblers.Values) lock (wire) wire.ClearPending();
        _tx = null; _rx = null; _secret = null; _capabilities = null; _directLink = null;
        _provider = null;
    }

    public bool HasSubscriber(string deviceId) =>
        _readyAfter.TryGetValue(deviceId, out var readyAt) && DateTimeOffset.UtcNow >= readyAt &&
        LiveClients(_tx).Any(x => SessionId(x) == deviceId);

    public async Task SendAsync(
        string deviceId,
        ClipboardContent content,
        CancellationToken token,
        Func<bool>? inputBusy = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsActive(deviceId))
            throw new InvalidOperationException("승인된 기기에만 클립보드를 보낼 수 있습니다.");
        if (!HasSubscriber(deviceId))
            throw new InvalidOperationException("Companion이 인증·활성 대상 확인을 마치지 않았습니다. 휴대폰 앱의 연결 상태를 확인하세요.");

        var client = LiveClients(_tx).FirstOrDefault(x => SessionId(x) == deviceId)
            ?? throw new InvalidOperationException("휴대폰 Companion이 Clipboard BLE 알림을 구독하지 않았습니다.");

        if (_tx is null)
            throw new InvalidOperationException("Clipboard BLE 서비스가 준비되지 않았습니다.");

        var characteristic = _tx ?? throw new InvalidOperationException("Clipboard service unavailable.");
        var subscription = FindSubscription(deviceId, client.Session)
            ?? throw new InvalidOperationException("Clipboard subscription expired.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            token, _resources.Token, _sends.Token, subscription.Lifetime.Token);
        token = linked.Token;
        token.ThrowIfCancellationRequested();
        var maxGattValue = client.MaxNotificationSize;
        if (maxGattValue <= BleFragmentCodec.HeaderSize)
            throw new InvalidOperationException($"BLE 알림 크기가 너무 작습니다: {maxGattValue}");

        var isImage = content.Kind != ClipboardContentKind.TextUtf8;
        var bulkTransfer =
            isImage ||
            (content.Kind == ClipboardContentKind.TextUtf8 &&
             content.Data.Length > 64 * 1024);

        if (isImage && maxGattValue < 100)
            throw new InvalidOperationException(
                $"BLE 이미지 전송 MTU가 너무 작습니다({maxGattValue}). P2P 고속 링크를 사용하세요.");

        await _sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var wireFrames =
                ClipboardWireCodec.Enumerate(content, 16 * 1024).GetEnumerator();

            if (!wireFrames.MoveNext())
                throw new InvalidDataException("Clipboard BLE transfer has no frames.");

            var firstWireFrame = wireFrames.Current;
            var contentTransferId =
                ClipboardWireCodec.Decode(firstWireFrame).TransferId;

            var ack = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var ackKey = (deviceId, contentTransferId);

            lock (_ackGate)
            {
                token.ThrowIfCancellationRequested();
                if (_ackWaiters.ContainsKey(ackKey))
                    throw new InvalidOperationException(
                        "Clipboard BLE ACK transfer id collision.");

                _ackWaiters.Add(ackKey, ack);
            }

            try
            {
                byte[]? finalFragment = null;
                async Task NotifyAsync(byte[] packet, CancellationToken cancellation)
                {
                    await InputAwareClipboardPacing.YieldAsync(inputBusy, cancellation).ConfigureAwait(false);
                    if (!IsActive(deviceId)) throw new InvalidOperationException("활성 기기가 변경되었습니다.");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    GattClientNotificationResult result;
                    try
                    {
                        result = await NotifySerializedAsync(characteristic, packet, client, timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!cancellation.IsCancellationRequested)
                    {
                        throw new IOException($"클립보드 조각을 Bluetooth에 전달하지 못했습니다: {ex.Message}", ex);
                    }
                    if (result.Status != GattCommunicationStatus.Success)
                        throw new InvalidOperationException($"Clipboard BLE 전송 실패: {result.Status}");
                }

                async Task SendWireFrameAsync(byte[] wireFrame)
                {
                    var transportTransferId =
                        BitConverter.ToUInt32(
                            RandomNumberGenerator.GetBytes(4));
                    var fragmentIndex = 0;

                    foreach (var fragment in BleFragmentCodec.Split(
                                 wireFrame,
                                 maxGattValue,
                                 transportTransferId))
                    {
                        if (!IsActive(deviceId))
                            throw new InvalidOperationException(
                                "활성 휴대폰이 변경되어 이전 기기의 Clipboard BLE 전송을 중단했습니다.");

                        // Text needs the same receipt/replay protection as bulk data.
                        // A successful GATT notify is not proof that Android assembled it.
                        await _reliableSender.SendAsync(deviceId, fragment, NotifyAsync, token).ConfigureAwait(false);
                        finalFragment = fragment;

                        fragmentIndex++;
                        if (bulkTransfer && fragmentIndex % 4 == 0)
                            await Task.Delay(
                                2,
                                token).ConfigureAwait(false);
                    }
                    if (isImage)
                        Status?.Invoke($"이미지 BLE 조각 {fragmentIndex}개 · 휴대폰 수신 확인 완료");
                }

                await SendWireFrameAsync(firstWireFrame)
                    .ConfigureAwait(false);

                while (wireFrames.MoveNext())
                {
                    await SendWireFrameAsync(wireFrames.Current)
                        .ConfigureAwait(false);
                }

                Status?.Invoke($"Clipboard BLE 데이터 전송 완료 · {content.Data.Length:N0} bytes · 휴대폰 적용 확인 대기");
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        await ack.Task.WaitAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
                        break;
                    }
                    catch (TimeoutException) when (finalFragment is not null && attempt < 2)
                    {
                        // The clipboard may already be applied: replay only the final fragment
                        // to request its cached application ACK, without applying twice.
                        await _reliableSender.SendAsync(deviceId, finalFragment, NotifyAsync, token).ConfigureAwait(false);
                    }
                    catch (TimeoutException ex)
                    {
                        throw new TimeoutException("휴대폰 조각 수신 완료, 클립보드 적용 ACK 없음 (3회 확인).", ex);
                    }
                }
            }
            finally
            {
                lock (_ackGate)
                {
                    if (_ackWaiters.TryGetValue(ackKey, out var current) &&
                        ReferenceEquals(current, ack))
                        _ackWaiters.Remove(ackKey);
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void CompleteAck(
        string deviceId,
        Guid transferId)
    {
        TaskCompletionSource<bool>? completion = null;
        var key = (deviceId, transferId);

        lock (_ackGate)
        {
            if (_ackWaiters.Remove(key, out var waiter))
                completion = waiter;
        }

        completion?.TrySetResult(true);
    }

    private void FailAllAcks(Exception error)
    {
        List<TaskCompletionSource<bool>> waiters;

        lock (_ackGate)
        {
            waiters = _ackWaiters.Values.ToList();
            _ackWaiters.Clear();
        }

        foreach (var waiter in waiters)
            waiter.TrySetException(error);
    }

    private bool IsTrusted(string id) =>
        _enabled() &&
        !string.IsNullOrWhiteSpace(id) &&
        _trustedDevice(id);

    private bool IsActive(string id) =>
        _enabled() &&
        !string.IsNullOrWhiteSpace(id) &&
        string.Equals(_approvedDeviceId(), id, StringComparison.Ordinal);

    private static IReadOnlyList<GattSubscribedClient> LiveClients(GattLocalCharacteristic? c)
    {
        if (c is null) return Array.Empty<GattSubscribedClient>();
        try
        {
            return c.SubscribedClients
                .Where(x =>
                {
                    try { return x.Session.SessionStatus == GattSessionStatus.Active; }
                    catch { return false; }
                })
                .ToArray();
        }
        catch
        {
            return Array.Empty<GattSubscribedClient>();
        }
    }

    private static string? SessionId(GattSubscribedClient client)
    {
        try { return client.Session.DeviceId.Id; }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Interlocked.Increment(ref _stopGeneration);
        CleanupServerResources();
        ReceiveContentAsync = null;
        // Managed gates remain usable by cancelled sends unwinding.
        _bleReassemblers.Clear();
        _clipboardReassemblers.Clear();
    }
}
