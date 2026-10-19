using System.Runtime.InteropServices;

namespace ConPhone.Windows;

internal static class PeerCatalogSelfTest
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine($"PASS: {name}");
    }

    internal static async Task RunAsync()
    {
        IReadOnlyList<PeerDevice> paired = [new("phone", "Test phone", true)];
        IReadOnlyList<string> trusted = [];
        string? approved = null;
        IReadOnlyDictionary<string, PeerInput> inputs = new Dictionary<string, PeerInput>();
        var enumerationFails = false;
        var metadataFails = false;
        var diagnostics = new List<string>();
        var catalog = new PeerCatalog(
            () => enumerationFails ? Task.FromException<IReadOnlyList<PeerDevice>>(
                new COMException("synthetic metadata error", unchecked((int)0x80010108))) : Task.FromResult(paired),
            id => metadataFails ? Task.FromException<PeerDevice?>(new COMException("synthetic device error"))
                : Task.FromResult<PeerDevice?>(new(id, "Test phone", true)),
            () => trusted, () => approved, () => inputs, diagnostics.Add);

        var peers = await catalog.GetAsync();
        Check(peers.Count == 1 && peers[0].Paired && !peers[0].Connected && !peers[0].Trusted,
            "Paired, unapproved phone stays visible with zero HID subscriptions");
        trusted = ["phone"];
        approved = "phone";
        inputs = new Dictionary<string, PeerInput> { ["phone"] = new(true, false) };
        peers = await catalog.GetAsync();
        Check(peers.Count == 1 && peers[0].Keyboard && !peers[0].Mouse && peers[0].Trusted && peers[0].Active,
            "Partial HID connection uses one deduplicated row with explicit missing input");
        inputs = new Dictionary<string, PeerInput> { ["phone"] = new(true, true) };
        peers = await catalog.GetAsync();
        Check(peers[0].Keyboard && peers[0].Mouse && peers[0].Connected,
            "Reconnection updates input state without recreating the candidate");
        inputs = new Dictionary<string, PeerInput>();
        enumerationFails = true;
        peers = await catalog.GetAsync();
        Check(peers.Count == 1 && peers[0].Name == "Test phone" && !peers[0].Connected &&
            !peers[0].Keyboard && !peers[0].Mouse,
            "Enumeration failure retains name but never retains live input state");
        Check(diagnostics.Any(line => line.Contains("paired metadata unavailable")),
            "Failed enumeration is recorded separately from an empty successful query");
        enumerationFails = false;
        paired = [];
        metadataFails = true;
        trusted = ["phone", "second"];
        peers = await catalog.GetAsync();
        Check(peers.Count == 2 && peers.Single(peer => peer.Id == "phone").Name == "Test phone" &&
            peers.All(peer => !peer.Connected),
            "One device metadata error cannot erase trusted rows or cached names");
        trusted = [];
        approved = null;
        peers = await catalog.GetAsync();
        Check(peers.Count == 1 && peers[0].Id == "phone" && !peers[0].Trusted,
            "Previously observed incoming phone remains visible without granting trust");

        var fresh = new PeerCatalog(() => Task.FromResult<IReadOnlyList<PeerDevice>>([]),
            _ => Task.FromResult<PeerDevice?>(null), () => ["offline"], () => "offline",
            () => new Dictionary<string, PeerInput>(), _ => { });
        peers = await fresh.GetAsync();
        Check(peers.Count == 1 && peers[0].Trusted && peers[0].Active && !peers[0].Connected,
            "After restart, saved phone remains visible without OS metadata or HID");

        var pending = new TaskCompletionSource<IReadOnlyList<PeerDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        inputs = new Dictionary<string, PeerInput> { ["phone"] = new(true, true) };
        var delayed = new PeerCatalog(() => pending.Task, _ => Task.FromResult<PeerDevice?>(null),
            () => [], () => null, () => inputs, _ => { });
        var reading = delayed.GetAsync();
        inputs = new Dictionary<string, PeerInput>();
        pending.SetResult([new("phone", "Test phone", true)]);
        peers = await reading;
        Check(peers.Count == 1 && !peers[0].Connected,
            "Disconnect during metadata read cannot return stale connected status");
        paired = [new("removed", "Unpaired later", true)];
        var removable = new PeerCatalog(() => Task.FromResult(paired), _ => Task.FromResult<PeerDevice?>(null),
            () => [], () => null, () => inputs, _ => { });
        await removable.GetAsync();
        paired = [];
        Check((await removable.GetAsync()).Count == 0,
            "Successful empty query removes unpaired candidates that were never observed or trusted");

        paired = [new("link-only", "Test phone", true, false)];
        enumerationFails = false;
        inputs = new Dictionary<string, PeerInput>();
        var connection = new PeerCatalog(
            () => enumerationFails ? Task.FromException<IReadOnlyList<PeerDevice>>(new COMException("synthetic read failure"))
                : Task.FromResult(paired),
            _ => Task.FromResult<PeerDevice?>(null), () => [], () => null, () => inputs, _ => { });
        var disconnected = (await connection.GetAsync()).Single();
        Check(disconnected.BluetoothConnected == false && !disconnected.InputReady &&
            disconnected.ConnectionDetail.StartsWith("Bluetooth 연결 안 됨"),
            "Paired device reports disconnected when Windows link state is false");
        paired = [new("link-only", "Test phone", true, true)];
        var connected = (await connection.GetAsync()).Single();
        Check(connected.BluetoothConnected == true && !connected.Connected && !connected.InputReady &&
            connected.ConnectionDetail.StartsWith("Bluetooth 연결됨") && connected.ConnectionDetail.Contains("입력 연결 대기"),
            "Windows reconnection updates display even without HID; input is still not ready");
        enumerationFails = true;
        var unknown = (await connection.GetAsync()).Single();
        Check(unknown.BluetoothConnected is null && unknown.ConnectionDetail.StartsWith("Bluetooth 상태 확인 중") &&
            unknown.Name == connected.Name,
            "Failed refresh preserves candidate but clears old Bluetooth connected status");
        enumerationFails = false;
        paired = [new("link-only", "Test phone", true, false)];
        var offlineAgain = (await connection.GetAsync()).Single();
        Check(offlineAgain.BluetoothConnected == false && !offlineAgain.InputReady,
            "Subsequent disconnect replaces connected state");
        inputs = new Dictionary<string, PeerInput> { ["link-only"] = new(true, true) };
        paired = [new("link-only", "Test phone", true)];
        var hidReady = (await connection.GetAsync()).Single();
        Check(hidReady.BluetoothConnected is null && hidReady.InputReady && hidReady.ConnectionDetail.StartsWith("Bluetooth 연결됨"),
            "Live HID proves input readiness even if optional OS metadata is missing");
        var suspended = hidReady.WithInput(new PeerInput(true, true, Suspended: true));
        Check(!suspended.InputReady && suspended.ConnectionDetail.Contains("일시 중지"),
            "HID suspend cannot be presented as input ready");
        var recovered = suspended.WithInput(new PeerInput(true, true));
        Check(recovered.InputReady && !recovered.ConnectionDetail.Contains("일시 중지"),
            "Input snapshot clears suspend without waiting for metadata refresh");
        var dropped = recovered.WithInput(new PeerInput());
        Check(!dropped.InputReady && !dropped.Connected && dropped.ConnectionDetail.Contains("입력 연결 대기"),
            "Input-only refresh removes stale ready state without a metadata query");

        // Exercise the actual read-only WinRT selector on this PC. It does not
        // substitute for testing the user's phone or a physical HID reconnect.
        var native = await BleHidServer.ReadPairedDevicesAsync();
        Check(native.All(peer => peer.Paired), $"Native paired-only enumeration completed ({native.Count} candidates)");
        Console.WriteLine($"Native Bluetooth metadata: connected={native.Count(peer => peer.BluetoothConnected == true)}, " +
            $"disconnected={native.Count(peer => peer.BluetoothConnected == false)}, unknown={native.Count(peer => peer.BluetoothConnected is null)}");
        using var server = new BleHidServer();
        var before = server.DescribeSubscriptions();
        var nativePeers = await server.GetPeersAsync();
        Check(native.All(device => nativePeers.Any(peer => peer.Id == device.Id)),
            "Production GetPeersAsync includes paired candidates with no HID service running");
        Check(server.DescribeSubscriptions() == before && !server.DesiredRunning && !server.Ready,
            "Production GetPeersAsync enumerates without starting services or changing input readiness");
        Console.WriteLine("PASS: Peer catalog regression checks. Physical phone connection NOT tested.");
    }
}
