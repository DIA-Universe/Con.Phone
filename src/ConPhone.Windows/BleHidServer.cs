using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using ConPhone.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;

namespace ConPhone.Windows;
public sealed record Peer(string Id,string Name,string Fingerprint,bool Keyboard,bool Mouse,bool Paired)
{
    public override string ToString()=>$"{Name} [{Fingerprint}]  키보드:{Keyboard} 마우스:{Mouse} 페어링:{Paired}";
}
public sealed class BleHidServer : IDisposable
{
    private GattServiceProvider? _provider,_battery;
    private GattLocalCharacteristic? _keyboard,_mouse;
    private readonly ConcurrentDictionary<string,BluetoothLEDevice> _devices=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,GattSession> _sessions=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,bool> _suspended=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,bool> _disconnected=new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refresh=new(1,1), _start=new(1,1);
    private readonly SerialWrites _writes=new();
    private GattSubscribedClient[] _keyboardClients=[],_mouseClients=[];
    private volatile bool _enabled,_ready,_disposed;
    private volatile string? _approved;
    private volatile string _advertisement="NotStarted",_operation="Idle";
    private bool _configured;
    private byte[] _keyValue=new byte[8],_mouseValue=new byte[6];
    private long _sent,_failures,_latency;
    public event Action? Changed;
    public event Action<string>? Status;
    public bool Ready=>_ready;
    public string? ApprovedId=>_approved;
    public string Advertisement=>_advertisement;
    public string LastOperation=>_operation;
    public int KeyboardSubscribers=>Volatile.Read(ref _keyboardClients).Length;
    public int MouseSubscribers=>Volatile.Read(ref _mouseClients).Length;
    public long Sent=>Interlocked.Read(ref _sent);
    public long Failures=>Interlocked.Read(ref _failures);
    public long LastSendMs=>Interlocked.Read(ref _latency);
    public bool NativeWritePending=>_writes.Busy;
    private static Guid Uuid(ushort value)=>new($"0000{value:x4}-0000-1000-8000-00805f9b34fb");
    public static async Task<string> DiagnoseAsync()
    {
        var a=await BluetoothAdapter.GetDefaultAsync();
        if(a is null) return "Bluetooth 어댑터 없음";
        var r=await a.GetRadioAsync();
        return $"Windows: {Environment.OSVersion.Version}\r\nBluetooth LE: {a.IsLowEnergySupported}\r\nPeripheral: {a.IsPeripheralRoleSupported}\r\nRadio: {r?.State}";
    }
    public async Task StartAsync()
    {
        await _start.WaitAsync();
        try
        {
            if(_disposed) throw new ObjectDisposedException(nameof(BleHidServer));
            if(_enabled && _advertisement is "Started" or "StartedWithoutAllAdvertisementData") return;
            _operation="Adapter";
            var adapter=await BluetoothAdapter.GetDefaultAsync();
            if(adapter is null || !adapter.IsLowEnergySupported || !adapter.IsPeripheralRoleSupported)
                throw new NotSupportedException("Bluetooth LE Peripheral 역할을 사용할 수 없습니다.");
            var radio=await adapter.GetRadioAsync();
            if(radio?.State!=RadioState.On) throw new InvalidOperationException("Windows Bluetooth를 먼저 켜세요.");
            if(_provider is not null && !_configured) throw new InvalidOperationException("GATT 생성이 실패했습니다. 앱을 종료한 뒤 다시 실행하세요.");
            if(_provider is null)
            {
                _operation="CreateHidService";
                var result=await GattServiceProvider.CreateAsync(Uuid(0x1812));
                if(result.Error!=BluetoothError.Success) throw new InvalidOperationException($"HID 서비스 생성: {result.Error}");
                _provider=result.ServiceProvider;
                _provider.AdvertisementStatusChanged+=(_,e)=>
                {
                    _advertisement=e.Status.ToString();
                    Status?.Invoke($"광고: {e.Status}; {e.Error}");
                    // Advertisement state is NOT the state of an already-connected GATT session.
                    // Never tear down a live input session just because discoverability changed.
                    RefreshRoutes();
                };
                await Characteristic(0x2A4A,GattCharacteristicProperties.Read,[0x11,0x01,0,3]);
                await Characteristic(0x2A4B,GattCharacteristicProperties.Read,Hid.ReportMap);
                _keyboard=await Input(true,1); _mouse=await Input(false,2);
                var battery=await GattServiceProvider.CreateAsync(Uuid(0x180F));
                if(battery.Error==BluetoothError.Success)
                {
                    _battery=battery.ServiceProvider;
                    var value=await _battery.Service.CreateCharacteristicAsync(Uuid(0x2A19),new GattLocalCharacteristicParameters { CharacteristicProperties=GattCharacteristicProperties.Read|GattCharacteristicProperties.Notify, ReadProtectionLevel=GattProtectionLevel.Plain });
                    if(value.Error==BluetoothError.Success) Read(value.Characteristic,_=>[100]);
                }
                var protocol=await Characteristic(0x2A4E,GattCharacteristicProperties.Read|GattCharacteristicProperties.WriteWithoutResponse);
                Read(protocol,_=>[1]); Write(protocol,(_,b)=>b.Length==1 && b[0]==1);
                var control=await Characteristic(0x2A4C,GattCharacteristicProperties.WriteWithoutResponse);
                Write(control,(id,b)=> { if(b.Length!=1||b[0]>1) return false; _suspended[id]=b[0]==0; Status?.Invoke(b[0]==0?"HID 호스트 일시 중지":"HID 호스트 재개"); RefreshRoutes(); return true; });
                _configured=true;
            }
            _enabled=true; _operation="StartAdvertising";
            _provider.StartAdvertising(new() { IsConnectable=true,IsDiscoverable=true });
            RefreshRoutes();
        }
        finally { _start.Release(); }
    }
    private async Task<GattLocalCharacteristic> Characteristic(ushort uuid,GattCharacteristicProperties properties,byte[]? value=null)
    {
        _operation=$"Characteristic:{uuid:X4}";
        var parameters=new GattLocalCharacteristicParameters { CharacteristicProperties=properties, ReadProtectionLevel=GattProtectionLevel.EncryptionRequired, WriteProtectionLevel=GattProtectionLevel.EncryptionRequired };
        if(value is not null) parameters.StaticValue=value.AsBuffer();
        var result=await _provider!.Service.CreateCharacteristicAsync(Uuid(uuid),parameters);
        if(result.Error!=BluetoothError.Success) throw new InvalidOperationException($"GATT {uuid:X4}: {result.Error}");
        return result.Characteristic;
    }
    private async Task<GattLocalCharacteristic> Input(bool keyboard,byte reportId)
    {
        var c=await Characteristic(0x2A4D,GattCharacteristicProperties.Read|GattCharacteristicProperties.Notify);
        var descriptor=await c.CreateDescriptorAsync(Uuid(0x2908),new GattLocalDescriptorParameters { StaticValue=new byte[]{reportId,1}.AsBuffer(), ReadProtectionLevel=GattProtectionLevel.EncryptionRequired });
        if(descriptor.Error!=BluetoothError.Success) throw new InvalidOperationException($"ReportReference: {descriptor.Error}");
        Read(c,id=>id==_approved ? (keyboard?_keyValue:_mouseValue).ToArray() : new byte[keyboard?8:6]);
        c.SubscribedClientsChanged+=(_,_)=>RefreshRoutes(); return c;
    }
    private static string? LiveId(GattSubscribedClient client)
    {
        try { return client.Session.SessionStatus==GattSessionStatus.Active ? client.Session.DeviceId.Id : null; }
        catch(Exception ex) when(ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
    }
    private GattSubscribedClient[] Snapshot(GattLocalCharacteristic? c)
    {
        if(c is null) return [];
        try { return c.SubscribedClients.Where(x=>LiveId(x) is { } id && !_disconnected.GetValueOrDefault(id)).ToArray(); }
        catch(Exception ex) when(ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return []; }
    }
    private void RefreshRoutes()
    {
        if(_disposed) return;
        var k=Snapshot(_keyboard); var m=Snapshot(_mouse);
        Volatile.Write(ref _keyboardClients,k); Volatile.Write(ref _mouseClients,m);
        foreach(var client in k.Concat(m))
        {
            try
            {
                var session=client.Session; var id=session.DeviceId.Id;
                if(!_sessions.TryAdd(id,session)) continue;
                session.SessionStatusChanged+=(sessionSender,e)=>
                {
                    if(e.Status==GattSessionStatus.Closed && _sessions.TryGetValue(id,out var active) && ReferenceEquals(active,session))
                    {
                        _sessions.TryRemove(id,out _); _suspended.TryRemove(id,out _);
                        if(_approved==id) _approved=null;
                        Status?.Invoke("GATT 세션 종료 · 입력 자동 재개 안 함");
                    }
                    RefreshRoutes();
                };
            }
            catch(Exception ex) when(ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        }
        var approved=_approved;
        _ready=_enabled && approved is not null && !_suspended.GetValueOrDefault(approved) &&
            k.Any(x=>LiveId(x)==approved) && m.Any(x=>LiveId(x)==approved);
        Changed?.Invoke();
    }
    public async Task<IReadOnlyList<Peer>> GetPeersAsync()
    {
        await _refresh.WaitAsync();
        try
        {
            RefreshRoutes();
            var ids=Volatile.Read(ref _keyboardClients).Concat(Volatile.Read(ref _mouseClients)).Select(LiveId).OfType<string>().Distinct().ToArray();
            List<Peer> peers=[];
            foreach(var id in ids)
            {
                try
                {
                    if(!_devices.TryGetValue(id,out var device))
                    {
                        device=await BluetoothLEDevice.FromIdAsync(id);
                        if(device is null) continue;
                        if(!_devices.TryAdd(id,device)) { device.Dispose(); device=_devices[id]; }
                        else device.ConnectionStatusChanged+=(sender,_)=>
                        {
                            bool lost=sender.ConnectionStatus==BluetoothConnectionStatus.Disconnected;
                            _disconnected[id]=lost;
                            if(lost && _approved==id) _approved=null;
                            Status?.Invoke(lost?"물리 Bluetooth 연결 해제":"물리 Bluetooth 연결됨"); RefreshRoutes();
                        };
                    }
                    // Retain the BluetoothLEDevice until shutdown. Do NOT recreate/dispose it on each UI refresh.
                    var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..8];
                    peers.Add(new(id,device.Name,fingerprint,FindClient(true,id) is not null,FindClient(false,id) is not null,device.DeviceInformation.Pairing.IsPaired));
                }
                catch(Exception ex) when(ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { Status?.Invoke($"기기 조회: {ex.GetType().Name} 0x{ex.HResult:X8}"); }
            }
            return peers;
        }
        finally { _refresh.Release(); }
    }
    private GattSubscribedClient? FindClient(bool keyboard,string id)=>(keyboard?Volatile.Read(ref _keyboardClients):Volatile.Read(ref _mouseClients)).FirstOrDefault(x=>LiveId(x)==id);
    public async Task ApproveAsync(string id)
    {
        var peers=await GetPeersAsync();
        var p=peers.FirstOrDefault(x=>x.Id==id);
        if(p is null || !p.Paired || !p.Keyboard || !p.Mouse || !_enabled) throw new InvalidOperationException("페어링과 키보드·마우스 구독이 모두 확인된 본인 기기만 승인할 수 있습니다.");
        _keyValue=new byte[8]; _mouseValue=new byte[6]; _approved=id; RefreshRoutes();
        Status?.Invoke("기기 승인 완료 · 입력 전환은 단축키로 직접 시작하세요.");
    }
    public void Revoke() { _approved=null; _ready=false; Changed?.Invoke(); }
    public async Task<bool> SendAsync(WireReport report,Func<bool> current,CancellationToken token)
    {
        if(report.Data.Length!=(report.Keyboard?8:6)) throw new ArgumentException("HID report length mismatch");
        var target=_approved;
        try
        {
            return await _writes.RunAsync(()=>current() && target is not null && target==_approved && _ready,async()=>
            {
                var client=FindClient(report.Keyboard,target!);
                if(client is null) throw new InvalidOperationException("선택한 기기의 구독이 사라졌습니다.");
                var c=report.Keyboard?_keyboard!:_mouse!;
                if(report.Data.Length>client.MaxNotificationSize) throw new InvalidOperationException("Notification length");
                long start=Stopwatch.GetTimestamp();
                // No caller cancellation passed into WinRT: SerialWrites retains unfinished native tasks.
                var result=await c.NotifyValueAsync(report.Data.AsBuffer(),client).AsTask().ConfigureAwait(false);
                Interlocked.Exchange(ref _latency,(long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if(result.Status!=GattCommunicationStatus.Success) throw new InvalidOperationException($"HID 전송: {result.Status}");
                if(report.Keyboard) _keyValue=report.Data.ToArray(); else _mouseValue=report.Data.ToArray();
                Interlocked.Increment(ref _sent);
            },token).ConfigureAwait(false);
        }
        catch(Exception ex)
        {
            Interlocked.Increment(ref _failures); _operation=$"Send:{ex.GetType().Name}:0x{ex.HResult:X8}";
            throw; // Caller restores local input. The service itself stays registered.
        }
    }
    public void Stop()
    {
        _enabled=false; Revoke(); _keyValue=new byte[8]; _mouseValue=new byte[6];
        try { if(_advertisement is "Started" or "StartedWithoutAllAdvertisementData") _provider?.StopAdvertising(); }
        catch(Exception ex) when(ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { Status?.Invoke($"광고 중지: {ex.GetType().Name}"); }
    }
    public void Dispose()
    {
        Stop(); _disposed=true;
        foreach(var device in _devices.Values) device.Dispose(); _devices.Clear();
    }
    private static void Read(GattLocalCharacteristic c,Func<string,byte[]> value)
    {
        c.ReadRequested+=async(_,args)=>
        {
            var deferral=args.GetDeferral();
            try
            {
                var r=await args.GetRequestAsync(); if(r is null) return;
                var bytes=value(args.Session.DeviceId.Id);
                if(r.Offset>bytes.Length) r.RespondWithProtocolError(7);
                else r.RespondWithValue(bytes.AsSpan((int)r.Offset).ToArray().AsBuffer());
            }
            catch(Exception) { /* Request may expire during a physical disconnect; no input values logged. */ }
            finally { try { deferral.Complete(); } catch(InvalidOperationException) { } }
        };
    }
    private static void Write(GattLocalCharacteristic c,Func<string,byte[],bool> write)
    {
        c.WriteRequested+=async(_,args)=>
        {
            var deferral=args.GetDeferral();
            try
            {
                var r=await args.GetRequestAsync(); if(r is null) return;
                bool ok=r.Offset==0 && write(args.Session.DeviceId.Id,r.Value.ToArray());
                if(r.Option==GattWriteOption.WriteWithResponse) { if(ok) r.Respond(); else r.RespondWithProtocolError(0x0D); }
            }
            catch(Exception) { }
            finally { try { deferral.Complete(); } catch(InvalidOperationException) { } }
        };
    }
}
