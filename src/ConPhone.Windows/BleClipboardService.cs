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
    private readonly Func<DirectLinkConfig?> _directLinkConfig;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, BleFragmentReassembler> _bleReassemblers = new();
    private readonly ConcurrentDictionary<string, ClipboardReassembler> _clipboardReassemblers = new();

    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _tx;
    private GattLocalCharacteristic? _rx;
    private GattLocalCharacteristic? _secret;
    private GattLocalCharacteristic? _capabilities;
    private GattLocalCharacteristic? _directLink;
    private bool _disposed;

    public event Action<string, ClipboardContent>? Received;
    public event Action<string>? Status;

    public BleClipboardService(
        Func<string?> approvedDeviceId,
        Func<string, bool> trustedDevice,
        Func<bool> enabled,
        Func<int> bleImageLimitBytes,
        Func<DirectLinkConfig?> directLinkConfig)
    {
        _approvedDeviceId = approvedDeviceId;
        _trustedDevice = trustedDevice;
        _enabled = enabled;
        _bleImageLimitBytes = bleImageLimitBytes;
        _directLinkConfig = directLinkConfig;
    }

    public bool Running => _provider?.AdvertisementStatus is
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
            Status?.Invoke($"Clipboard BLE 검색 신호: {args.Status} ({args.Error})");

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
            Status?.Invoke($"Clipboard BLE 구독: {Subscribers}");

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

        _provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsConnectable = true,
            IsDiscoverable = true
        });
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
                request.RespondWithProtocolError(0x05);
                return;
            }

            var deviceHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id));
            var secret = ClipboardPairingSecretStore.GetOrCreate(id);
            var credential = new byte[64];
            deviceHash.CopyTo(credential, 0);
            secret.CopyTo(credential, 32);
            request.RespondWithValue(credential.AsBuffer());
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

            // protocol v2 (backward-compatible extension):
            // [0]=version, [1]=text, [2]=image, [3]=direct P2P,
            // [4..7]=BLE image fallback limit in bytes, little-endian,
            // [8]=1 when this trusted peer is the current active target.
            var limit = Math.Clamp(
                _bleImageLimitBytes(),
                64 * 1024,
                4 * 1024 * 1024);

            var response = new byte[9] { 2, 1, 1, 1, 0, 0, 0, 0, 0 };
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                response.AsSpan(4, 4),
                limit);
            response[8] = IsActive(args.Session.DeviceId.Id)
                ? (byte)1
                : (byte)0;
            request.RespondWithValue(response.AsBuffer());
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
            request.RespondWithValue(
                config is null
                    ? new byte[] { 0 }.AsBuffer()
                    : config.Encode().AsBuffer());
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

    private async void RxWriteRequested(
        GattLocalCharacteristic sender,
        GattWriteRequestedEventArgs args)
    {
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
                var fragment = BleFragmentCodec.Decode(request.Value.ToArray());
                var ble = _bleReassemblers.GetOrAdd(id, _ => new BleFragmentReassembler());
                var frame = ble.Add(fragment);
                if (frame is not null)
                {
                    var chunk = ClipboardWireCodec.Decode(frame);
                    var clipboard = _clipboardReassemblers.GetOrAdd(id, _ => new ClipboardReassembler());
                    var content = clipboard.Add(chunk);
                    if (content is not null)
                        Received?.Invoke(id, content);
                }

                valid = true;
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            {
                Status?.Invoke($"Clipboard BLE 수신 프레임 거부: {ex.Message}");
            }

            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                if (valid) request.Respond();
                else request.RespondWithProtocolError(0x0D);
            }
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Clipboard BLE 수신 실패: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    public void StopAdvertising()
    {
        if (_provider is null) return;
        try
        {
            if (Running)
                _provider.StopAdvertising();
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Clipboard BLE 검색 신호 중지 확인 실패: {ex.Message}");
        }
    }

    public bool HasSubscriber(string deviceId) =>
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

        var client = LiveClients(_tx).FirstOrDefault(x => SessionId(x) == deviceId)
            ?? throw new InvalidOperationException("휴대폰 Companion이 Clipboard BLE 알림을 구독하지 않았습니다.");

        if (_tx is null)
            throw new InvalidOperationException("Clipboard BLE 서비스가 준비되지 않았습니다.");

        var maxGattValue = client.MaxNotificationSize;
        if (maxGattValue <= BleFragmentCodec.HeaderSize)
            throw new InvalidOperationException($"BLE 알림 크기가 너무 작습니다: {maxGattValue}");

        var isImage = content.Kind != ClipboardContentKind.TextUtf8;
        if (isImage && maxGattValue < 100)
            throw new InvalidOperationException(
                $"BLE 이미지 전송 MTU가 너무 작습니다({maxGattValue}). P2P 고속 링크를 사용하세요.");

        await _sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var wireFrame in ClipboardWireCodec.Enumerate(content, 16 * 1024))
            {
                var transferId = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
                var fragmentIndex = 0;
                foreach (var fragment in BleFragmentCodec.Split(wireFrame, maxGattValue, transferId))
                {
                    if (!IsActive(deviceId))
                        throw new InvalidOperationException(
                            "활성 휴대폰이 변경되어 이전 기기의 Clipboard BLE 전송을 중단했습니다.");

                    if (isImage && pauseBulk is not null)
                    {
                        while (pauseBulk())
                            await Task.Delay(25, token).ConfigureAwait(false);
                    }

                    var result = await _tx.NotifyValueAsync(fragment.AsBuffer(), client)
                        .AsTask(token).ConfigureAwait(false);

                    if (result.Status != GattCommunicationStatus.Success)
                        throw new InvalidOperationException($"Clipboard BLE 전송 실패: {result.Status}");

                    fragmentIndex++;
                    if (isImage && fragmentIndex % 4 == 0)
                        await Task.Delay(2, token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }
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

        _sendGate.Dispose();
        _bleReassemblers.Clear();
        _clipboardReassemblers.Clear();
    }
}
