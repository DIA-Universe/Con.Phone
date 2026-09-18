using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using ConPhone.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;

namespace ConPhone.Windows;

public sealed record Peer(string Id, string Name, string Fingerprint, bool Keyboard, bool Mouse, bool Paired)
{
    public override string ToString() => $"{Name} [{Fingerprint}]  키보드:{Keyboard} 마우스:{Mouse} 페어링:{Paired}";
}

public sealed class BleHidServer
{
    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _keyboard, _mouse, _bootKeyboard, _bootMouse;
    private readonly ConcurrentDictionary<string, byte> _protocol = new();
    private readonly ConcurrentDictionary<string, bool> _suspended = new();
    private readonly ConcurrentDictionary<string, GattSession> _sessions = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private volatile string? _approved;
    private volatile string _lastOperation = "Idle";
    private volatile bool _running;
    private bool _configured;
    private byte[] _keyboardValue = new byte[8], _mouseValue = new byte[5];
    public event Action? Changed;
    public event Action<string>? Status;
    public string Advertisement => _provider?.AdvertisementStatus.ToString() ?? "NotStarted";
    public string LastOperation => _lastOperation;
    public string? ApprovedId => _approved;
    public bool Ready => _running && _approved is { } id && IsReady(id);
    private static Guid Uuid(ushort n) => new($"0000{n:x4}-0000-1000-8000-00805f9b34fb");

    public static async Task<string> DiagnoseAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null) return "Bluetooth 어댑터 없음. 내장 어댑터/드라이버를 확인하세요.";
        var radio = await adapter.GetRadioAsync();
        return $"Windows: {Environment.OSVersion.Version}\r\n" +
            $"Bluetooth LE: {adapter.IsLowEnergySupported}\r\n" +
            $"Peripheral 역할: {adapter.IsPeripheralRoleSupported}\r\n" +
            $"무선 상태: {radio?.State.ToString() ?? "Unknown"}\r\n" +
            "지원 여부는 HID 인식/입력 성공을 의미하지 않습니다.";
    }

    public async Task StartAsync()
    {
        _lastOperation = "BluetoothAdapter.GetDefaultAsync";
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null || !adapter.IsLowEnergySupported || !adapter.IsPeripheralRoleSupported)
            throw new NotSupportedException("내장 Bluetooth의 LE Peripheral 역할이 지원되지 않습니다. 동글 우회는 제공하지 않습니다.");

        _lastOperation = "BluetoothAdapter.GetRadioAsync";
        var radio = await adapter.GetRadioAsync();
        if (radio?.State != RadioState.On) throw new InvalidOperationException("Windows Bluetooth를 먼저 켜 주세요.");

        if (_provider is not null && !_configured)
            throw new InvalidOperationException("이전 서비스 구성이 실패했습니다. 앱을 종료하고 다시 실행하세요.");

        if (_provider is not null && IsAdvertising(_provider.AdvertisementStatus))
        {
            _running = true;
            _lastOperation = $"Advertising:{_provider.AdvertisementStatus}";
            Status?.Invoke($"Bluetooth 광고가 이미 실행 중입니다: {_provider.AdvertisementStatus}");
            Changed?.Invoke();
            return;
        }

        if (_provider is null)
        {
            _lastOperation = "GattServiceProvider.CreateAsync";
            var result = await GattServiceProvider.CreateAsync(Uuid(0x1812));
            if (result.Error != BluetoothError.Success)
                throw new InvalidOperationException($"HID 서비스 생성 실패: {result.Error}. Windows 정책/권한/드라이버를 확인하세요.");
            _provider = result.ServiceProvider;
            _provider.AdvertisementStatusChanged += (_, args) =>
            {
                Status?.Invoke($"광고 상태: {args.Status}; 결과: {args.Error}");
                if (args.Status == GattServiceProviderAdvertisementStatus.Aborted) { _running = false; _approved = null; }
                Changed?.Invoke();
            };
            await Characteristic(0x2A4A, GattCharacteristicProperties.Read, [0x11, 0x01, 0x00, 0x02]);
            await Characteristic(0x2A4B, GattCharacteristicProperties.Read, Hid.ReportMap);
            _keyboard = await InputCharacteristic(0x2A4D, true, false, 1);
            _mouse = await InputCharacteristic(0x2A4D, false, false, 2);
            _bootKeyboard = await InputCharacteristic(0x2A22, true, true);
            _bootMouse = await InputCharacteristic(0x2A33, false, true);
            var output = await Characteristic(0x2A4D, GattCharacteristicProperties.Read | GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse);
            await Reference(output, 1, 2);
            OutputHandler(output);
            var bootOutput = await Characteristic(0x2A32, GattCharacteristicProperties.Read | GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse);
            OutputHandler(bootOutput);
            var protocol = await Characteristic(0x2A4E, GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse);
            Read(protocol, id => [_protocol.GetValueOrDefault(id, (byte)1)]);
            Write(protocol, (id, bytes) =>
            {
                if (bytes.Length != 1 || bytes[0] > 1) return false;
                _protocol[id] = bytes[0];
                if (_approved == id) _approved = null;
                Changed?.Invoke(); return true;
            });
            var control = await Characteristic(0x2A4C, GattCharacteristicProperties.WriteWithoutResponse);
            Write(control, (id, bytes) =>
            {
                if (bytes.Length != 1 || bytes[0] > 1) return false;
                _suspended[id] = bytes[0] == 0;
                if (bytes[0] == 0 && _approved == id) _approved = null;
                Changed?.Invoke(); return true;
            });
            _configured = true;
        }

        _approved = null;
        _running = true;
        _lastOperation = "GattServiceProvider.StartAdvertising";
        try
        {
            _provider.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true });
            _lastOperation = $"AdvertisingRequested:{_provider.AdvertisementStatus}";
            Status?.Invoke("Bluetooth HID 광고 요청 완료. 휴대폰의 기존 PC 등록이 연결되지 않으면 등록 해제 후 새 기기로 다시 페어링하세요.");
        }
        catch (InvalidOperationException ex) when (IsIllegalState(ex))
        {
            _running = false;
            throw new InvalidOperationException(
                $"Windows Bluetooth가 현재 GATT 광고를 시작할 수 없는 상태입니다. 광고:{Advertisement}, 단계:{_lastOperation}. Bluetooth를 껐다 켠 뒤 다시 시도해 주세요.", ex);
        }
        Changed?.Invoke();
    }

    private async Task<GattLocalCharacteristic> Characteristic(ushort uuid, GattCharacteristicProperties properties, byte[]? value = null)
    {
        _lastOperation = $"CreateCharacteristic:0x{uuid:X4}";
        var p = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = properties,
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            WriteProtectionLevel = GattProtectionLevel.EncryptionRequired
        };
        if (value is not null) p.StaticValue = value.AsBuffer();
        var r = await _provider!.Service.CreateCharacteristicAsync(Uuid(uuid), p);
        if (r.Error != BluetoothError.Success) throw new InvalidOperationException($"Characteristic {uuid:X4}: {r.Error}");
        return r.Characteristic;
    }

    private async Task<GattLocalCharacteristic> InputCharacteristic(ushort uuid, bool keyboard, bool boot, byte? reportId = null)
    {
        var c = await Characteristic(uuid, GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify);
        if (reportId is { } id) await Reference(c, id, 1);
        Read(c, client =>
        {
            // Never expose the approved user's current keys to a second bonded central.
            var value = client == _approved ? (keyboard ? _keyboardValue : _mouseValue) : new byte[keyboard ? 8 : 5];
            return boot && !keyboard ? new byte[] { (byte)(value[0] & 7), value[1], value[2] } : value.ToArray();
        });
        c.SubscribedClientsChanged += (_, _) =>
        {
            try
            {
                foreach (var client in c.SubscribedClients)
                {
                    if (!TryActiveSession(client, out var session, out var id) || session is null) continue;
                    if (_sessions.TryAdd(id, session))
                        session.SessionStatusChanged += (sender, args) =>
                        {
                            if (args.Status == GattSessionStatus.Closed)
                            {
                                if (_approved == id) _approved = null;
                                _protocol.TryRemove(id, out _);
                                _suspended.TryRemove(id, out _);
                                _sessions.TryRemove(id, out _);
                            }
                            Changed?.Invoke();
                        };
                }
            }
            catch (InvalidOperationException ex) when (IsIllegalState(ex)) { }
            catch (ObjectDisposedException) { }
            Changed?.Invoke();
        };
        return c;
    }

    private async Task Reference(GattLocalCharacteristic c, byte id, byte type)
    {
        _lastOperation = $"CreateReportReference:{id}:{type}";
        var r = await c.CreateDescriptorAsync(Uuid(0x2908), new GattLocalDescriptorParameters
        { StaticValue = new byte[] { id, type }.AsBuffer(), ReadProtectionLevel = GattProtectionLevel.EncryptionRequired });
        if (r.Error != BluetoothError.Success) throw new InvalidOperationException($"Report Reference: {r.Error}");
    }
    private static void Read(GattLocalCharacteristic c, Func<string, byte[]> value)
    {
        c.ReadRequested += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var request = await args.GetRequestAsync();
                if (request is null) return;
                var bytes = value(args.Session.DeviceId.Id);
                if (request.Offset > bytes.Length) request.RespondWithProtocolError(0x07);
                else request.RespondWithValue(bytes.AsSpan(request.Offset).ToArray().AsBuffer());
            }
            catch (Exception) { /* A disconnected request must not expose input in logs. */ }
            finally { deferral.Complete(); }
        };
    }
    private static void Write(GattLocalCharacteristic c, Func<string, byte[], bool> write)
    {
        c.WriteRequested += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var request = await args.GetRequestAsync();
                if (request is null) return;
                var valid = request.Offset == 0 && write(args.Session.DeviceId.Id, request.Value.ToArray());
                if (request.Option == GattWriteOption.WriteWithResponse)
                { if (valid) request.Respond(); else request.RespondWithProtocolError(0x0D); }
            }
            catch (Exception) { }
            finally { deferral.Complete(); }
        };
    }
    private static void OutputHandler(GattLocalCharacteristic c)
    {
        Read(c, _ => [0]); // LED output does not reveal the phone's current IME language.
        Write(c, (_, bytes) => bytes.Length == 1);
    }
    private GattLocalCharacteristic? Input(string id, bool keyboard)
    {
        bool boot = _protocol.GetValueOrDefault(id, (byte)1) == 0;
        return keyboard ? (boot ? _bootKeyboard : _keyboard) : (boot ? _bootMouse : _mouse);
    }

    private static bool IsAdvertising(GattServiceProviderAdvertisementStatus status) =>
        status is GattServiceProviderAdvertisementStatus.Started or
            GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData;

    private static bool IsIllegalState(Exception ex) =>
        ex is InvalidOperationException && ex.HResult == unchecked((int)0x8000000E);

    private static bool TryActiveSession(GattSubscribedClient client, out GattSession? session, out string id)
    {
        session = null; id = "";
        try
        {
            session = client.Session;
            if (session.SessionStatus != GattSessionStatus.Active) return false;
            id = session.DeviceId.Id;
            return !string.IsNullOrWhiteSpace(id);
        }
        catch (InvalidOperationException ex) when (IsIllegalState(ex)) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    private static GattSubscribedClient? Client(GattLocalCharacteristic? c, string id)
    {
        if (c is null) return null;
        try
        {
            foreach (var client in c.SubscribedClients)
                if (TryActiveSession(client, out _, out var clientId) && clientId == id)
                    return client;
        }
        catch (InvalidOperationException ex) when (IsIllegalState(ex)) { }
        catch (ObjectDisposedException) { }
        return null;
    }

    private bool IsReady(string id) => !_suspended.GetValueOrDefault(id) &&
        Client(Input(id, true), id) is not null && Client(Input(id, false), id) is not null;

    public async Task<IReadOnlyList<Peer>> GetPeersAsync()
    {
        _lastOperation = "EnumerateSubscribedClients";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in new[] { _keyboard, _mouse, _bootKeyboard, _bootMouse })
        {
            if (c is null) continue;
            try
            {
                foreach (var client in c.SubscribedClients)
                    if (TryActiveSession(client, out _, out var id))
                        ids.Add(id);
            }
            catch (InvalidOperationException ex) when (IsIllegalState(ex)) { }
            catch (ObjectDisposedException) { }
        }

        var peers = new List<Peer>();
        foreach (var id in ids)
        {
            try
            {
                _lastOperation = "BluetoothLEDevice.FromIdAsync";
                using var device = await BluetoothLEDevice.FromIdAsync(id);
                var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..8];
                peers.Add(new(id, device?.Name ?? "이름 확인 불가", fingerprint,
                    Client(Input(id, true), id) is not null, Client(Input(id, false), id) is not null,
                    device?.DeviceInformation.Pairing.IsPaired == true));
            }
            catch (InvalidOperationException ex) when (IsIllegalState(ex))
            {
                // A just-disconnected WinRT session may remain in SubscribedClients briefly.
            }
            catch (ObjectDisposedException) { }
        }
        _lastOperation = "PeerRefreshComplete";
        return peers;
    }
    public async Task ApproveAsync(string id)
    {
        using var device = await BluetoothLEDevice.FromIdAsync(id);
        if (device?.DeviceInformation.Pairing.IsPaired != true)
            throw new InvalidOperationException("Windows에서 페어링된 기기만 승인할 수 있습니다. 양쪽의 연결 확인 화면을 확인하세요.");
        if (!_running || !IsReady(id)) throw new InvalidOperationException("키보드와 마우스 구독이 모두 활성화되어야 합니다.");
        _keyboardValue = new byte[8]; _mouseValue = new byte[5];
        _approved = id;
        Changed?.Invoke();
    }
    public void Revoke() { _approved = null; Changed?.Invoke(); }

    public async Task SendAsync(WireReport report, Func<bool> current, CancellationToken token)
    {
        await _send.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!current()) return;
            var id = _approved;
            if (id is null || !_running || !IsReady(id)) throw new InvalidOperationException("승인된 입력 연결이 사라졌습니다.");
            var characteristic = Input(id, report.Keyboard)!;
            var client = Client(characteristic, id);
            if (client is null) throw new InvalidOperationException("승인된 입력 연결이 사라졌습니다.");
            var bytes = report.Data;
            if (!report.Keyboard && _protocol.GetValueOrDefault(id, (byte)1) == 0)
                bytes = [(byte)(bytes[0] & 7), bytes[1], bytes[2]];
            if (bytes.Length > client.MaxNotificationSize) throw new InvalidOperationException("HID report가 알림 크기를 초과합니다.");
            if (report.Keyboard) _keyboardValue = report.Data.ToArray(); else _mouseValue = report.Data.ToArray();
            // This overload targets one approved client; never broadcast input to all clients.
            var result = await characteristic.NotifyValueAsync(bytes.AsBuffer(), client).AsTask(token).ConfigureAwait(false);
            if (result.Status != GattCommunicationStatus.Success) throw new InvalidOperationException($"HID 전송 실패: {result.Status}");
        }
        finally { _send.Release(); }
    }
    public void Stop()
    {
        _running = false; _approved = null;
        _keyboardValue = new byte[8]; _mouseValue = new byte[5];
        if (_provider is not null)
        {
            try
            {
                var status = _provider.AdvertisementStatus;
                if (IsAdvertising(status) || status == GattServiceProviderAdvertisementStatus.Aborted)
                    _provider.StopAdvertising();
            }
            catch (InvalidOperationException ex) when (IsIllegalState(ex))
            {
                Status?.Invoke("Bluetooth 서비스가 이미 중지/전환 중이어서 중지 요청을 생략했습니다.");
            }
        }
        _lastOperation = "Stopped";
        Changed?.Invoke();
    }
}
