using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using ConPhone.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Foundation;

namespace ConPhone.Windows;

public sealed record Peer(
    string Id,
    string Name,
    string Fingerprint,
    bool Keyboard,
    bool Mouse,
    bool SystemControl,
    bool Paired,
    bool Connected,
    bool Trusted = false,
    bool Active = false,
    bool ConsumerControl = false)
{
    public override string ToString() =>
        $"{(Active ? "▶ " : "")}{Name} [{Fingerprint}]  " +
        $"키보드:{Keyboard} 마우스:{Mouse} 잠금:{SystemControl} 미디어:{ConsumerControl} " +
        $"등록:{Trusted} 연결:{Connected}";
}

public sealed class BleHidServer : IDisposable
{
    private GattServiceProvider? _provider;
    private GattServiceProvider? _batteryProvider;
    private GattLocalCharacteristic? _keyboard, _mouse, _system, _consumer;

    private readonly ConcurrentDictionary<string, byte> _protocol = new();
    private readonly ConcurrentDictionary<string, bool> _suspended = new();
    private readonly ConcurrentDictionary<string, SharedBleResource<GattSession>.Lease> _sessions = new();
    private readonly object _sessionGate = new();
    private readonly ConcurrentDictionary<string, DeviceInformation> _deviceInfo = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly SemaphoreSlim _deviceGate = new(1, 1);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private volatile string? _approved;
    private volatile string _lastOperation = "Idle";
    private volatile bool _running;
    private readonly Action<GattServiceProvider, GattServiceProviderAdvertisingParameters> _startAdvertising;
    private readonly Func<GattServiceProvider, GattServiceProviderAdvertisementStatus> _advertisementStatus;
    private volatile bool _desiredRunning;
    private long _lastRecoveryAttemptTicks;
    private long _autoRecoveryAttempts;
    private long _autoRecoverySuccesses;
    private long _lastRecoverySuccessUnixMs;
    private bool _everAdvertised;
    private bool _disposed;
    private BleResourceLifetime _resources;
    private Radio? _radio;
    private int _radioResetNeeded;
    private readonly Dictionary<SharedBleResource<GattSession>.Lease, TypedEventHandler<GattSession, GattSessionStatusChangedEventArgs>> _sessionHandlers = new();
    private long _stopGeneration;
    private long _nextSessionId;
    public event Action<bool>? RadioAvailabilityChanged;

    private byte[] _keyboardValue = new byte[Hid.KeyboardReportLength];
    private byte[] _mouseValue = new byte[Hid.MouseReportLength];
    private byte[] _systemValue = new byte[Hid.SystemReportLength];
    private byte[] _consumerValue = new byte[Hid.ConsumerReportLength];

    public event Action? Changed;
    public event Action<string>? Status;

    public string Advertisement => BleNativeState.Read(() => _provider is { } provider
        ? _advertisementStatus(provider).ToString() : "NotStarted", "NotStarted");
    public string LastOperation => _lastOperation;
    public bool DesiredRunning => _desiredRunning;
    public long AutoRecoveryAttempts => Interlocked.Read(ref _autoRecoveryAttempts);
    public long AutoRecoverySuccesses => Interlocked.Read(ref _autoRecoverySuccesses);
    public DateTimeOffset? LastRecoverySuccess
    {
        get
        {
            var value = Interlocked.Read(ref _lastRecoverySuccessUnixMs);
            return value > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : null;
        }
    }
    public int KeyboardSubscribers => LiveClientCount(_keyboard);
    public int MouseSubscribers => LiveClientCount(_mouse);
    public int SystemSubscribers => LiveClientCount(_system);
    public int ConsumerSubscribers => LiveClientCount(_consumer);
    public string? ApprovedId => _approved;
    public int TrustedDeviceCount => TrustedDeviceStore.LoadAll().Count;
    public bool IsTrustedDevice(string id) => TrustedDeviceStore.Contains(id);
    internal bool ServiceAvailable => _running && _desiredRunning && !_resources.IsDisposed;
    public bool Ready => ServiceAvailable && _approved is { } id && IsReady(id);
    public int MousePacingIntervalMs => PointerPacing.FallbackIntervalMs;

    public BleHidServer() : this((provider, parameters) => provider.StartAdvertising(parameters)) { }

    internal BleHidServer(Action<GattServiceProvider, GattServiceProviderAdvertisingParameters> startAdvertising,
        Func<GattServiceProvider, GattServiceProviderAdvertisementStatus>? advertisementStatus = null)
    {
        _startAdvertising = startAdvertising;
        _advertisementStatus = advertisementStatus ?? (provider => provider.AdvertisementStatus);
        _resources = new BleResourceLifetime(text => Status?.Invoke(text));
        _approved = TrustedDeviceStore.Load();
    }

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
               "휴대폰에서 시작한 HID 연결만 처리합니다. PC에서 상대 기기로 연결/페어링을 요청하지 않습니다.";
    }

    public async Task StartAsync()
    {
        var stopGeneration = Interlocked.Read(ref _stopGeneration);
        await _startGate.WaitAsync();
        try { if (stopGeneration == Interlocked.Read(ref _stopGeneration)) await StartCoreAsync(); }
        catch {
            // An advertisement error must not remove a constructed HID service
            // or close its sessions and change the peer's GATT table mid-link.
            if (!_running || _resources.IsDisposed) CleanupServerResources();
            throw;
        }
        finally { _startGate.Release(); }
    }

    private async Task StartCoreAsync()
    {
        ThrowIfDisposed();
        _desiredRunning = true;
        if (ServiceAvailable && _provider is not null) {
            RequestAdvertising();
            return;
        }
        if (_resources.IsDisposed) {
            CleanupServerResources();
            _resources = new BleResourceLifetime(text => Status?.Invoke(text));
        }
        var lifetime = _resources;
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        var startupToken = startupTimeout.Token;
        using var startupCancellation = startupToken.Register(lifetime.Dispose);

        _lastOperation = "BluetoothAdapter.GetDefaultAsync";
        var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(startupToken);
        if (adapter is null || !adapter.IsLowEnergySupported || !adapter.IsPeripheralRoleSupported)
            throw new NotSupportedException("내장 Bluetooth의 LE Peripheral 역할이 지원되지 않습니다.");

        _lastOperation = "BluetoothAdapter.GetRadioAsync";
        var radio = await adapter.GetRadioAsync().AsTask(startupToken);
        startupToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_radio, radio)) {
            if (_radio is not null) _radio.StateChanged -= RadioStateChanged;
            _radio = radio;
            if (_radio is not null) _radio.StateChanged += RadioStateChanged;
        }
        if (radio?.State != RadioState.On)
            throw new InvalidOperationException("Windows Bluetooth를 먼저 켜 주세요.");

        startupToken.ThrowIfCancellationRequested();

        if (_provider is null)
        {
            _lastOperation = "GattServiceProvider.CreateAsync";
            var result = await GattServiceProvider.CreateAsync(Uuid(0x1812)).AsTask(startupToken);
            if (result.Error != BluetoothError.Success)
                throw new InvalidOperationException($"HID 서비스 생성 실패: {result.Error}");

            _provider = result.ServiceProvider;
            var provider = _provider;
            lifetime.Register(() => provider.StopAdvertising());
            TypedEventHandler<GattServiceProvider, GattServiceProviderAdvertisementStatusChangedEventArgs> advertisementChanged = (_, args) =>
            {
                if (lifetime.IsDisposed || !ReferenceEquals(_provider, provider)) return;
                if (IsAdvertising(args.Status))
                {
                    _everAdvertised = true;
                    Status?.Invoke($"검색 신호 상태: {args.Status}; 결과: {args.Error}");
                }
                else if (args.Status == GattServiceProviderAdvertisementStatus.Aborted)
                {
                    if (_everAdvertised)
                    {
                        Status?.Invoke($"검색 신호 중단: {args.Status}; 결과: {args.Error} · HID 서비스 유지");
                    }
                    else
                    {
                        // Healthy radios can report Aborted once while transitioning to Started.
                        Status?.Invoke($"검색 신호 준비 중: {args.Status}; 결과: {args.Error}");
                    }
                }
                else
                {
                    Status?.Invoke($"검색 신호 상태: {args.Status}; 결과: {args.Error}");
                }

                Changed?.Invoke();
            };

            provider.AdvertisementStatusChanged += advertisementChanged;
            lifetime.Register(() => provider.AdvertisementStatusChanged -= advertisementChanged);

            await Characteristic(0x2A4A, GattCharacteristicProperties.Read, [0x11, 0x01, 0x00, 0x03]);
            await Characteristic(0x2A4B, GattCharacteristicProperties.Read, Hid.ReportMap);

            _keyboard = await InputCharacteristic(
                reportId: 1,
                reportLength: Hid.KeyboardReportLength,
                label: "키보드",
                currentValue: id => id == _approved ? _keyboardValue : new byte[Hid.KeyboardReportLength]);

            _mouse = await InputCharacteristic(
                reportId: 2,
                reportLength: Hid.MouseReportLength,
                label: "마우스",
                currentValue: id => id == _approved ? _mouseValue : new byte[Hid.MouseReportLength]);

            _system = await InputCharacteristic(
                reportId: 3,
                reportLength: Hid.SystemReportLength,
                label: "시스템 잠금",
                currentValue: id => id == _approved ? _systemValue : new byte[Hid.SystemReportLength]);

            _consumer = await InputCharacteristic(
                reportId: 4,
                reportLength: Hid.ConsumerReportLength,
                label: "미디어키",
                currentValue: id => id == _approved ? _consumerValue : new byte[Hid.ConsumerReportLength]);

            await CreateBatteryServiceAsync();

            var protocol = await Characteristic(
                0x2A4E,
                GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse);

            Read(protocol, id => [_protocol.GetValueOrDefault(id, (byte)1)]);
            Write(protocol, (id, bytes) =>
            {
                if (bytes.Length != 1 || bytes[0] > 1) return false;
                _protocol[id] = bytes[0];
                Changed?.Invoke();
                return true;
            });

            var control = await Characteristic(0x2A4C, GattCharacteristicProperties.WriteWithoutResponse);
            Write(control, (id, bytes) =>
            {
                if (bytes.Length != 1 || bytes[0] > 1) return false;
                _suspended[id] = bytes[0] == 0;
                Changed?.Invoke();
                return true;
            });

        }

        startupToken.ThrowIfCancellationRequested();
        // The startup deadline owns construction only, never retries of the
        // discovery signal on an existing service.
        startupCancellation.Dispose();
        startupToken.ThrowIfCancellationRequested();
        _running = true;
        RequestAdvertising();
    }

    private void RequestAdvertising()
    {
        _resources.Token.ThrowIfCancellationRequested();
        var provider = _provider ?? throw new InvalidOperationException("HID 서비스가 준비되지 않았습니다.");
        if (IsAdvertising(_advertisementStatus(provider))) return;
        _lastOperation = "GattServiceProvider.StartAdvertising";

        try
        {
            _startAdvertising(provider, ConnectionStartup.HidAdvertisement());
            _lastOperation = $"AdvertisingRequested:{_advertisementStatus(provider)}";
        }
        catch (InvalidOperationException ex) when (IsIllegalState(ex))
        {
            throw new InvalidOperationException(
                $"Windows Bluetooth 검색 신호 대기 중. 상태:{Advertisement}. 기존 HID 서비스는 유지합니다.",
                ex);
        }

        Changed?.Invoke();
    }

    public async Task<bool> EnsureAdvertisingAsync()
    {
        if (_disposed || !_desiredRunning)
            return false;

        if (Advertisement is "Started" or "StartedWithoutAllAdvertisementData")
        {
            return true;
        }

        if (!await _recoveryGate.WaitAsync(0).ConfigureAwait(false))
            return false;

        try
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastRecoveryAttemptTicks);
            var elapsedSeconds = previous == 0
                ? double.MaxValue
                : (now - previous) / (double)System.Diagnostics.Stopwatch.Frequency;

            if (elapsedSeconds < 4)
                return false;

            Interlocked.Exchange(ref _lastRecoveryAttemptTicks, now);
            var attempt = Interlocked.Increment(ref _autoRecoveryAttempts);

            try
            {
                await StartAsync().ConfigureAwait(false);
                if (Advertisement is "Started" or "StartedWithoutAllAdvertisementData")
                {
                    Interlocked.Increment(ref _autoRecoverySuccesses);
                    Interlocked.Exchange(
                        ref _lastRecoverySuccessUnixMs,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Status?.Invoke("Bluetooth HID 검색 신호 자동 복구 완료.");
                    return true;
                }
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                NotSupportedException or
                ObjectDisposedException || BleNativeState.IsUnavailable(ex))
            {
                _lastOperation = $"AutoRecovery:{ex.GetType().Name}:0x{ex.HResult:X8}";
                if (attempt == 1 || attempt % 6 == 0)
                {
                    Status?.Invoke(
                        $"Bluetooth HID 자동 복구 대기 · 시도 {attempt} · {ex.Message}");
                }
            }

            return false;
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private async Task<GattLocalCharacteristic> Characteristic(
        ushort uuid,
        GattCharacteristicProperties properties,
        byte[]? value = null)
    {
        _lastOperation = $"CreateCharacteristic:0x{uuid:X4}";

        var p = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = properties,
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            WriteProtectionLevel = GattProtectionLevel.EncryptionRequired
        };

        if (value is not null) p.StaticValue = value.AsBuffer();

        var r = await _provider!.Service.CreateCharacteristicAsync(Uuid(uuid), p).AsTask(_resources.Token);
        if (r.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Characteristic {uuid:X4}: {r.Error}");

        return r.Characteristic;
    }

    private async Task<GattLocalCharacteristic> InputCharacteristic(
        byte reportId,
        int reportLength,
        string label,
        Func<string, byte[]> currentValue)
    {
        var c = await Characteristic(
            0x2A4D,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify);

        await Reference(c, reportId, 1);
        Read(c, id =>
        {
            var bytes = currentValue(id);
            return bytes.Length == reportLength ? bytes.ToArray() : new byte[reportLength];
        });

        var lifetime = _resources;
        TypedEventHandler<GattLocalCharacteristic, object> subscribed = (_, _) =>
        {
            if (lifetime.IsDisposed) return;
            try
            {
                foreach (var client in c.SubscribedClients)
                {
                    if (!TryActiveSession(client, out var session, out var id) || session is null) continue;
                    TrackSession(id, session);
                }
            }
            catch (Exception ex) when (BleNativeState.IsUnavailable(ex))
            { Status?.Invoke($"[BLE] subscription changed during disconnect HRESULT=0x{ex.HResult:X8}"); }

            Status?.Invoke(
                $"HID 구독 · 키보드:{LiveClientCount(_keyboard)} 마우스:{LiveClientCount(_mouse)} 잠금:{LiveClientCount(_system)} ({label} 변경)");
            Changed?.Invoke();
        };

        c.SubscribedClientsChanged += subscribed;
        lifetime.Register(() => c.SubscribedClientsChanged -= subscribed);
        return c;
    }

    private void TrackSession(string id, GattSession session)
    {
        lock (_sessionGate)
        {
            if (_disposed || _resources.IsDisposed || !BleNativeState.IsActive(session)) return;
            if (_sessions.TryGetValue(id, out var existing)) {
                // Keyboard/mouse/consumer subscriptions return different wrappers
                // for the same live link. Keep its canonical owner until Closed.
                if (existing.IsActive) return;
                _sessions.TryRemove(id, out _);
                ReleaseSession(existing);
            }
            var lease = SharedBleResource<GattSession>.Acquire(id, session, BleNativeState.IsActive);
            session = lease.Resource;
            _sessions[id] = lease;
            var generation = _resources.Id;
            var sessionId = Interlocked.Increment(ref _nextSessionId);
            TypedEventHandler<GattSession, GattSessionStatusChangedEventArgs> changed = (sender, args) =>
            {
                lock (_sessionGate)
                {
                    if (_disposed || _resources.IsDisposed || _resources.Id != generation || !_sessions.TryGetValue(id, out var current) ||
                        !ReferenceEquals(current, lease)) return;
                    if (args.Status == GattSessionStatus.Closed)
                    {
                        _sessions.TryRemove(id, out _);
                        _protocol.TryRemove(id, out _);
                        _suspended.TryRemove(id, out _);
                        ReleaseSession(lease);
                    }
                }
                Status?.Invoke($"[BLE][Session {generation}/HID {sessionId}] session={args.Status} status={args.Error}");
                Changed?.Invoke();
            };
            _sessionHandlers.Add(lease, changed);
            session.SessionStatusChanged += changed;
            Status?.Invoke($"[BLE][Session {generation}/HID {sessionId}/Link {lease.Generation}] incoming session tracked");
        }
    }

    private void ReleaseSession(SharedBleResource<GattSession>.Lease lease)
    {
        // Called under _sessionGate; remove ownership before invoking native code.
        if (_sessionHandlers.Remove(lease, out var handler))
            try { lease.Resource.SessionStatusChanged -= handler; } catch { }
        try {
            lease.Dispose();
            Status?.Invoke($"[BLE][Session {_resources.Id}] HID session reference released");
        }
        catch (Exception ex) { Status?.Invoke($"[BLE] session Dispose HRESULT=0x{ex.HResult:X8}"); }
    }

    private async void RadioStateChanged(Radio sender, object args)
    {
        if (_disposed || !ReferenceEquals(sender, _radio)) return;
        var on = sender.State == RadioState.On;
        if (!on)
        {
            _running = false;
            Interlocked.Exchange(ref _radioResetNeeded, 1);
            _resources.Dispose();
        }
        Status?.Invoke($"[BLE][Session {_resources.Id}] radio={sender.State}");
        RadioAvailabilityChanged?.Invoke(on);
        await _startGate.WaitAsync();
        try
        {
            if (Interlocked.Exchange(ref _radioResetNeeded, 0) != 0) CleanupServerResources();
            if (!_disposed && _desiredRunning && _radio?.State == RadioState.On)
                await StartCoreAsync();
        }
        catch (Exception ex) {
            if (!_running || _resources.IsDisposed) CleanupServerResources();
            Status?.Invoke($"[BLE] radio recovery {ex.GetType().Name} HRESULT=0x{ex.HResult:X8}");
        }
        finally { _startGate.Release(); }
        Changed?.Invoke();
    }

    private async Task ReadDeviceInfoAsync(string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = false;
        try
        {
            await _deviceGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            if (_disposed) return;

            // Read OS metadata only. BluetoothLEDevice.FromIdAsync can prompt
            // for access and must not run in the 5-second background refresh.
            _lastOperation = "DeviceInformation.CreateFromIdAsync";
            var info = await DeviceInformation.CreateFromIdAsync(
                    id, [], DeviceInformationKind.AssociationEndpoint)
                .AsTask(timeout.Token).ConfigureAwait(false);
            if (info is not null && !_disposed && !_resources.IsDisposed) _deviceInfo[id] = info;
            else _deviceInfo.TryRemove(id, out _);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or
            OperationCanceledException or ArgumentException or UnauthorizedAccessException)
        {
            _deviceInfo.TryRemove(id, out _);
        }
        finally
        {
            if (entered) _deviceGate.Release();
        }
    }

    private static int LiveClientCount(GattLocalCharacteristic? characteristic)
    {
        if (characteristic is null) return 0;

        try
        {
            var count = 0;
            foreach (var client in characteristic.SubscribedClients)
                if (TryActiveSession(client, out _, out _)) count++;
            return count;
        }
        catch (Exception ex) when (BleNativeState.IsUnavailable(ex)) { return 0; }
    }

    private async Task CreateBatteryServiceAsync()
    {
        var result = await GattServiceProvider.CreateAsync(Uuid(0x180F)).AsTask(_resources.Token);
        if (result.Error != BluetoothError.Success) return;

        _batteryProvider = result.ServiceProvider;
        var batteryProvider = _batteryProvider;
        _resources.Register(() => {
            if (IsAdvertising(batteryProvider.AdvertisementStatus)) batteryProvider.StopAdvertising();
        });

        var p = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            ReadProtectionLevel = GattProtectionLevel.Plain
        };

        var level = await _batteryProvider.Service.CreateCharacteristicAsync(Uuid(0x2A19), p).AsTask(_resources.Token);
        if (level.Error != BluetoothError.Success) return;

        Read(level.Characteristic, _ => [100]);
    }

    private async Task Reference(GattLocalCharacteristic c, byte id, byte type)
    {
        _lastOperation = $"CreateReportReference:{id}:{type}";

        var r = await c.CreateDescriptorAsync(
            Uuid(0x2908),
            new GattLocalDescriptorParameters
            {
                StaticValue = new byte[] { id, type }.AsBuffer(),
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired
            }).AsTask(_resources.Token);

        if (r.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Report Reference: {r.Error}");
    }

    private void Read(GattLocalCharacteristic c, Func<string, byte[]> value)
    {
        var lifetime = _resources;
        TypedEventHandler<GattLocalCharacteristic, GattReadRequestedEventArgs> read = async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var request = await args.GetRequestAsync().AsTask(lifetime.Token);
                if (request is null) return;
                if (lifetime.IsDisposed) { request.RespondWithProtocolError(0x0E); return; }

                var bytes = value(args.Session.DeviceId.Id);
                if (request.Offset > bytes.Length)
                    request.RespondWithProtocolError(0x07);
                else
                    request.RespondWithValue(bytes.AsSpan(request.Offset).ToArray().AsBuffer());
            }
            catch (Exception) { }
            finally
            {
                try { deferral.Complete(); } catch (Exception ex) { Status?.Invoke($"[BLE] deferral completion HRESULT=0x{ex.HResult:X8}"); }
            }
        };
        c.ReadRequested += read;
        lifetime.Register(() => c.ReadRequested -= read);
    }

    private void Write(GattLocalCharacteristic c, Func<string, byte[], bool> write)
    {
        var lifetime = _resources;
        TypedEventHandler<GattLocalCharacteristic, GattWriteRequestedEventArgs> handler = async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var request = await args.GetRequestAsync().AsTask(lifetime.Token);
                if (request is null) return;
                if (lifetime.IsDisposed) { request.RespondWithProtocolError(0x0E); return; }

                bool valid;
                lock (_sessionGate)
                    valid = !lifetime.IsDisposed && request.Offset == 0 && write(args.Session.DeviceId.Id, request.Value.ToArray());
                if (request.Option == GattWriteOption.WriteWithResponse)
                {
                    if (valid) request.Respond();
                    else request.RespondWithProtocolError(0x0D);
                }
            }
            catch (Exception) { }
            finally
            {
                try { deferral.Complete(); } catch (Exception ex) { Status?.Invoke($"[BLE] deferral completion HRESULT=0x{ex.HResult:X8}"); }
            }
        };
        c.WriteRequested += handler;
        lifetime.Register(() => c.WriteRequested -= handler);
    }

    private static bool IsAdvertising(GattServiceProviderAdvertisementStatus status) =>
        status is GattServiceProviderAdvertisementStatus.Started or
            GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData;

    private static bool IsIllegalState(Exception ex) =>
        ex is InvalidOperationException && ex.HResult == unchecked((int)0x8000000E);

    private static bool TryActiveSession(
        GattSubscribedClient client,
        out GattSession? session,
        out string id)
    {
        session = null;
        id = "";

        try
        {
            session = client.Session;
            if (!BleNativeState.IsActive(session)) return false;

            id = session.DeviceId.Id;
            return !string.IsNullOrWhiteSpace(id);
        }
        catch (Exception ex) when (BleNativeState.IsUnavailable(ex)) { return false; }
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
        catch (Exception ex) when (BleNativeState.IsUnavailable(ex)) { }

        return null;
    }

    private bool IsReady(string id) =>
        !_suspended.GetValueOrDefault(id) &&
        Client(_keyboard, id) is not null &&
        Client(_mouse, id) is not null;

    public async Task<IReadOnlyList<Peer>> GetPeersAsync()
    {
        ThrowIfDisposed();
        _lastOperation = "EnumerateSubscribedClients";

        var trustedIds = TrustedDeviceStore.LoadAll();
        var ids = new HashSet<string>(trustedIds, StringComparer.Ordinal);

        foreach (var characteristic in new[] { _keyboard, _mouse, _system, _consumer })
        {
            if (characteristic is null) continue;

            try
            {
                foreach (var client in characteristic.SubscribedClients)
                {
                    if (!TryActiveSession(client, out _, out var id))
                        continue;

                    ids.Add(id);
                }
            }
            catch (Exception ex) when (BleNativeState.IsUnavailable(ex)) { }
        }

        // Remembered devices should stay visible even while temporarily disconnected.
        foreach (var id in ids)
            await ReadDeviceInfoAsync(id).ConfigureAwait(false);

        var peers = new List<Peer>();
        foreach (var id in ids)
        {
            _deviceInfo.TryGetValue(id, out var device);
            var fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..8];

            var trusted = trustedIds.Contains(id, StringComparer.Ordinal);
            var active = string.Equals(_approved, id, StringComparison.Ordinal);

            peers.Add(new Peer(
                id,
                string.IsNullOrWhiteSpace(device?.Name)
                    ? (trusted ? "저장된 기기 (오프라인)" : "이름 확인 불가")
                    : device.Name,
                fingerprint,
                Client(_keyboard, id) is not null,
                Client(_mouse, id) is not null,
                Client(_system, id) is not null,
                device?.Pairing.IsPaired == true,
                Client(_keyboard, id) is not null || Client(_mouse, id) is not null,
                trusted,
                active,
                Client(_consumer, id) is not null));
        }

        _lastOperation = "PeerRefreshComplete";
        return peers
            .OrderByDescending(x => x.Active)
            .ThenByDescending(x => x.Trusted)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task ApproveAsync(string id)
    {
        ThrowIfDisposed();
        await ReadDeviceInfoAsync(id).ConfigureAwait(false);

        if (!_deviceInfo.TryGetValue(id, out var device) ||
            device.Pairing.IsPaired != true)
            throw new InvalidOperationException("Windows에서 페어링된 기기만 등록할 수 있습니다.");

        if (!ServiceAvailable)
            throw new InvalidOperationException("Bluetooth HID 서비스가 실행 중이 아닙니다.");

        for (var i = 0; i < 20 && !IsReady(id); i++)
            await Task.Delay(250).ConfigureAwait(false);

        if (!IsReady(id))
        {
            var keyboard = Client(_keyboard, id) is not null;
            var mouse = Client(_mouse, id) is not null;
            throw new InvalidOperationException(
                $"HID 구독 미완료 · 키보드:{keyboard} 마우스:{mouse}. 휴대폰 Bluetooth 상세에서 입력 장치를 확인하세요.");
        }

        var slot = TrustedDeviceStore.Remember(id);
        await SelectTargetAsync(id).ConfigureAwait(false);

        Status?.Invoke(
            $"기기 등록/활성화 완료 · 슬롯:{slot}/{TrustedDeviceStore.MaxDevices} · " +
            $"K:{(Client(_keyboard, id) is null ? 0 : 1)} M:{(Client(_mouse, id) is null ? 0 : 1)} " +
            $"LOCK:{(Client(_system, id) is null ? 0 : 1)} MEDIA:{(Client(_consumer, id) is null ? 0 : 1)}");
        Changed?.Invoke();
    }

    public async Task SelectTargetAsync(string id)
    {
        ThrowIfDisposed();

        if (!TrustedDeviceStore.Contains(id))
            throw new InvalidOperationException("먼저 이 기기를 신뢰 기기로 등록해 주세요.");

        if (!ServiceAvailable)
            throw new InvalidOperationException("Bluetooth HID 서비스가 실행 중이 아닙니다.");

        if (!IsReady(id))
            throw new InvalidOperationException(
                "선택한 기기의 키보드·마우스 HID 연결이 현재 준비되지 않았습니다.");

        _keyboardValue = new byte[Hid.KeyboardReportLength];
        _mouseValue = new byte[Hid.MouseReportLength];
        _systemValue = new byte[Hid.SystemReportLength];
        _consumerValue = new byte[Hid.ConsumerReportLength];

        _approved = id;
        TrustedDeviceStore.SetActive(id);

        Status?.Invoke(
            $"활성 입력 대상 전환 · {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..8]}");
        Changed?.Invoke();
    }

    public string? Revoke(string? deviceId = null)
    {
        var target = deviceId ?? _approved;
        if (target is null) return null;

        TrustedDeviceStore.Remove(target);
        _deviceInfo.TryRemove(target, out _);

        if (string.Equals(_approved, target, StringComparison.Ordinal))
            _approved = TrustedDeviceStore.LoadActive();

        Status?.Invoke(
            $"신뢰 기기 해제 · 남은 등록 {TrustedDeviceStore.LoadAll().Count}/{TrustedDeviceStore.MaxDevices}");
        Changed?.Invoke();
        return target;
    }

    public async Task SendSystemLockAsync(ModifierProfile profile, HotkeyBinding appleLockShortcut, CancellationToken token)
    {
        ThrowIfDisposed();

        var id = _approved;
        if (id is null || !ServiceAvailable || !IsReady(id))
            throw new InvalidOperationException("승인된 휴대폰 입력 연결이 없습니다.");

        var keyboardClient = Client(_keyboard, id);
        if (_keyboard is null || keyboardClient is null)
            throw new InvalidOperationException("휴대폰 키보드 HID 구독이 없습니다.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _resources.Token);
        token = linked.Token;
        await _send.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte[] winL = profile == ModifierProfile.MacCtrlCommandSwap
                ? AppleLockShortcut.Report(appleLockShortcut)
                : Hid.LockKeyboardReport(ModifierProfile.Standard);
            byte[] keyboardRelease = new byte[Hid.KeyboardReportLength];

            _keyboardValue = winL;
            var keyDown = await _keyboard.NotifyValueAsync(winL.AsBuffer(), keyboardClient)
                .AsTask(token).ConfigureAwait(false);
            if (keyDown.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"잠금 키 전송 실패: {keyDown.Status}");

            await Task.Delay(80, token).ConfigureAwait(false);

            _keyboardValue = keyboardRelease;
            var keyUp = await _keyboard.NotifyValueAsync(keyboardRelease.AsBuffer(), keyboardClient)
                .AsTask(token).ConfigureAwait(false);
            if (keyUp.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"잠금 키 해제 실패: {keyUp.Status}");

            // If the phone also subscribed to System Control, follow with System Sleep.
            // This is best-effort: lack of this optional characteristic must not make lock fail.
            var systemClient = Client(_system, id);
            if (profile == ModifierProfile.Standard && _system is not null && systemClient is not null)
            {
                await Task.Delay(60, token).ConfigureAwait(false);

                var pressed = Hid.SystemReport(true);
                _systemValue = pressed;
                var down = await _system.NotifyValueAsync(pressed.AsBuffer(), systemClient)
                    .AsTask(token).ConfigureAwait(false);

                if (down.Status == GattCommunicationStatus.Success)
                {
                    await Task.Delay(60, token).ConfigureAwait(false);
                    var released = Hid.SystemReport(false);
                    _systemValue = released;
                    _ = await _system.NotifyValueAsync(released.AsBuffer(), systemClient)
                        .AsTask(token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // A locked host may stop receiving notifications before key-up succeeds.
            // Never retain a pressed lock chord in the readable report value.
            _keyboardValue = new byte[Hid.KeyboardReportLength];
            _systemValue = Hid.SystemReport(false);
            _send.Release();
        }
    }

    public async Task SendConsumerAsync(
        ConsumerAction action,
        CancellationToken token)
    {
        ThrowIfDisposed();

        var id = _approved;
        if (id is null || !ServiceAvailable || !IsReady(id))
            throw new InvalidOperationException("승인된 휴대폰 입력 연결이 없습니다.");

        var client = Client(_consumer, id);
        if (_consumer is null || client is null)
            throw new NotSupportedException(
                "휴대폰이 미디어 Consumer Control Report를 구독하지 않았습니다. 새 HID 프로필로 다시 페어링해 주세요.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _resources.Token);
        token = linked.Token;
        await _send.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var pressed = Hid.ConsumerReport(action, true);
            _consumerValue = pressed;
            var down = await _consumer.NotifyValueAsync(pressed.AsBuffer(), client)
                .AsTask(token).ConfigureAwait(false);
            if (down.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"미디어키 전송 실패: {down.Status}");

            await Task.Delay(60, token).ConfigureAwait(false);

            var released = Hid.ConsumerReport(action, false);
            _consumerValue = released;
            var up = await _consumer.NotifyValueAsync(released.AsBuffer(), client)
                .AsTask(token).ConfigureAwait(false);
            if (up.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"미디어키 해제 실패: {up.Status}");
        }
        finally
        {
            _send.Release();
        }
    }

    public Task<bool> SendAsync(
        WireReport report,
        Func<bool> current,
        CancellationToken token)
    {
        var target = _approved;
        bool StillCurrent() => current() && string.Equals(target, _approved, StringComparison.Ordinal);
        return HidSendRecovery.SendAsync(StillCurrent,
            ct => TrySendAsync(report, StillCurrent, ct), token);
    }

    private async Task<bool> TrySendAsync(
        WireReport report,
        Func<bool> current,
        CancellationToken token)
    {
        ThrowIfDisposed();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _resources.Token);
        token = linked.Token;
        await _send.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!current()) return true;

            var id = _approved;
            if (id is null || !ServiceAvailable || _suspended.GetValueOrDefault(id))
                return false;

            // Validate the characteristic actually being sent. Enumerating both
            // keyboard and mouse here repeated three native snapshots per move.
            var characteristic = report.Keyboard ? _keyboard : _mouse;
            var client = Client(characteristic, id);
            if (characteristic is null || client is null)
                return false;

            var bytes = report.Data;
            if (bytes.Length > client.MaxNotificationSize)
                throw new InvalidOperationException("HID report가 알림 크기를 초과합니다.");

            if (report.Keyboard) _keyboardValue = bytes.ToArray();
            else _mouseValue = bytes.ToArray();

            var result = await characteristic.NotifyValueAsync(bytes.AsBuffer(), client)
                .AsTask(token).ConfigureAwait(false);

            if (result.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"HID 전송 실패: {result.Status}");
            return true;
        }
        finally
        {
            _send.Release();
        }
    }

    public void Stop()
    {
        if (_disposed) return;

        _desiredRunning = false;
        Interlocked.Increment(ref _stopGeneration);
        _running = false;
        _keyboardValue = new byte[Hid.KeyboardReportLength];
        _mouseValue = new byte[Hid.MouseReportLength];
        _systemValue = new byte[Hid.SystemReportLength];
        _consumerValue = new byte[Hid.ConsumerReportLength];

        CleanupServerResources();
        _lastOperation = "Stopped";
        Changed?.Invoke();
    }

    private void CleanupServerResources()
    {
        _running = false;
        _resources.Dispose(); // cancel operations, detach handlers, stop publication
        lock (_sessionGate)
        {
            var sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            foreach (var session in sessions) ReleaseSession(session);
        }
        _protocol.Clear(); _suspended.Clear(); _deviceInfo.Clear();
        _keyboard = null; _mouse = null; _system = null; _consumer = null;
        _provider = null; _batteryProvider = null;
        _everAdvertised = false;
        if (_disposed && _radio is not null) {
            try { _radio.StateChanged -= RadioStateChanged; } catch { }
            _radio = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _desiredRunning = false;
        Interlocked.Increment(ref _stopGeneration);
        if (_radio is not null) {
            try { _radio.StateChanged -= RadioStateChanged; } catch { }
            _radio = null;
        }
        CleanupServerResources();
        // Managed semaphores remain usable by cancelled operations unwinding.
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
