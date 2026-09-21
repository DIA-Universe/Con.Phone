using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using ConPhone.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Credentials;

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
    private volatile bool _published;

    public Func<string, ClipboardContent, Task<bool>>? ReceiveContentAsync { get; set; }
    public event Action<string>? Status;

    public BleClipboardService(
        Func<string?> approvedDeviceId,
        Func<string, bool> trustedDevice,
        Func<bool> enabled,
        Func<int> bleImageLimitBytes,
        Func<bool> p2pRequested,
        Func<DirectLinkConfig?> directLinkConfig)
    {
        _approvedDeviceId = approvedDeviceId;
        _trustedDevice = trustedDevice;
        _enabled = enabled;
        _bleImageLimitBytes = bleImageLimitBytes;
        _p2pRequested = p2pRequested;
        _directLinkConfig = directLinkConfig;
    }

    public bool Running => _published || _provider?.AdvertisementStatus is
        GattServiceProviderAdvertisementStatus.Started or
        GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData;

    public int Subscribers => LiveClients(_tx).Count;

    public async Task PrepareAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_provider is not null)
            return;

        var created = await GattServiceProvider.CreateAsync(ServiceUuid);
        if (created.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Clipboard BLE 서비스 생성 실패: {created.Error}");

        _provider = created.ServiceProvider;
        _provider.AdvertisementStatusChanged += (_, args) =>
        {
            if (args.Status is GattServiceProviderAdvertisementStatus.Stopped or
                GattServiceProviderAdvertisementStatus.Aborted) _published = false;
            Status?.Invoke($"Clipboard BLE 서비스 상태: {args.Status} ({args.Error})");
        };

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

        _tx.SubscribedClientsChanged += (_, _) =>
        {
            var liveIds = LiveClients(_tx).Select(SessionId).ToHashSet();
            foreach (var id in _readyAfter.Keys)
                if (!liveIds.Contains(id)) _readyAfter.TryRemove(id, out _);
            Status?.Invoke($"Clipboard BLE 구독: {Subscribers} · 휴대폰 준비 확인 대기");
        };

        _rx.WriteRequested += RxWriteRequested;
        _secret.ReadRequested += SecretReadRequested;
        _capabilities.ReadRequested += CapabilitiesReadRequested;
        _directLink.ReadRequested += DirectLinkReadRequested;
    }

    public async Task StartAsync()
    {
        await PrepareAsync().ConfigureAwait(false);
        StartAdvertising();
    }

    private void StartAdvertising()
    {
        if (_provider is null || Running) return;

        _provider.StartAdvertising(ConnectionStartup.ClipboardAdvertisement());
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

        var result = await _provider!.Service.CreateCharacteristicAsync(uuid, parameters);
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Clipboard BLE characteristic {uuid}: {result.Error}");

        return result.Characteristic;
    }

    private async void SecretReadRequested(
        GattLocalCharacteristic sender,
        GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null) return;

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
            deferral.Complete();
        }
    }

    private async void CapabilitiesReadRequested(
        GattLocalCharacteristic sender,
        GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null) return;

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
            RespondRead(request, response);
            if (request.Offset == 0 && response[8] == 1 &&
                _readyAfter.TryAdd(args.Session.DeviceId.Id, DateTimeOffset.UtcNow.AddSeconds(1)))
                Status?.Invoke("Clipboard 활성 대상 준비 응답 완료 · 전송 대기 해제");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void DirectLinkReadRequested(
        GattLocalCharacteristic sender,
        GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null) return;

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
            deferral.Complete();
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
        var deferral = args.GetDeferral();

        try
        {
            var request = await args.GetRequestAsync();
            if (request is null) return;

            var id = args.Session.DeviceId.Id;
            if (!IsActive(id))
            {
                if (request.Option == GattWriteOption.WriteWithResponse)
                    request.RespondWithProtocolError(0x05);
                return;
            }

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
                    var frame = ble.Add(fragment);

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
                            var content = clipboard.Add(chunk);

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
                                        TimeSpan.FromSeconds(8))
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
            deferral.Complete();
        }

        if (ackToSend is { } transferId &&
            ackDeviceId is { } deviceId)
        {
            try
            {
                using var ackTimeout =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(5));

                await SendAckAsync(
                        deviceId,
                        transferId,
                        ackTimeout.Token)
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
            var result = await _tx.NotifyValueAsync(
                    fragment.AsBuffer(),
                    client)
                .AsTask(token)
                .ConfigureAwait(false);

            if (result.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException(
                    $"Clipboard BLE ACK 전송 실패: {result.Status}");
        }
    }

    public void CancelPendingSends(string reason)
    {
        FailAllAcks(
            new InvalidOperationException(reason));
    }

    public void StopAdvertising()
    {
        if (_provider is null) return;
        try
        {
            if (Running)
                _provider.StopAdvertising();
            _published = false;
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Clipboard BLE 검색 신호 중지 확인 실패: {ex.Message}");
        }
    }

    public bool HasSubscriber(string deviceId) =>
        _readyAfter.TryGetValue(deviceId, out var readyAt) && DateTimeOffset.UtcNow >= readyAt &&
        LiveClients(_tx).Any(x => SessionId(x) == deviceId);

    public async Task SendAsync(
        string deviceId,
        ClipboardContent content,
        CancellationToken token,
        Func<bool>? pauseBulk = null)
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
                    if (!IsActive(deviceId)) throw new InvalidOperationException("활성 기기가 변경되었습니다.");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var result = await _tx.NotifyValueAsync(packet.AsBuffer(), client).AsTask(timeout.Token).ConfigureAwait(false);
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

                        if (bulkTransfer && pauseBulk is not null)
                        {
                            while (pauseBulk())
                                await Task.Delay(
                                    25,
                                    token).ConfigureAwait(false);
                        }

                        if (isImage)
                        {
                            await _reliableSender.SendAsync(deviceId, fragment, NotifyAsync, token).ConfigureAwait(false);
                            finalFragment = fragment;
                        }
                        else await NotifyAsync(fragment, token).ConfigureAwait(false);

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
                    catch (TimeoutException) when (isImage && finalFragment is not null && attempt < 2)
                    {
                        // The image may already be applied: replay only the final fragment
                        // to request its cached application ACK, without applying twice.
                        await _reliableSender.SendAsync(deviceId, finalFragment, NotifyAsync, token).ConfigureAwait(false);
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

        try
        {
            if (_provider is not null && Running)
                _provider.StopAdvertising();
        }
        catch
        {
        }

        FailAllAcks(
            new ObjectDisposedException(nameof(BleClipboardService)));

        // Do not dispose _sendGate here: an in-flight SendAsync may still be
        // unwinding from NotifyValueAsync and must be able to Release() safely.
        _bleReassemblers.Clear();
        _clipboardReassemblers.Clear();
    }
}
