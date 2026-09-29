using System.Reflection;
using ConPhone.Core;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace ConPhone.Windows;

/// <summary>Inject advertisement failures into real native services; no phone is simulated.</summary>
internal static class BleRecoverySelfTest
{
    private static T Field<T>(object owner, string name) where T : class =>
        (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner) ?? throw new InvalidOperationException($"Missing {name}"));

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine($"PASS: {name}");
    }

    public static async Task RunAsync()
    {
        var fail = true;
        var advertisementAborted = false;
        void Publish(GattServiceProvider provider, GattServiceProviderAdvertisingParameters parameters)
        {
            if (fail) throw new InvalidOperationException("Injected advertisement failure");
            provider.StartAdvertising(parameters);
        }
        using var hid = new BleHidServer(Publish, provider => advertisementAborted
            ? GattServiceProviderAdvertisementStatus.Aborted : provider.AdvertisementStatus);
        using var clipboard = new BleClipboardService(() => null, _ => false, () => false,
            () => 512 * 1024, () => false, () => null, Publish);
        await clipboard.PrepareAsync();
        var clipboardLifetime = Field<BleResourceLifetime>(clipboard, "_resources");
        var clipboardProvider = Field<GattServiceProvider>(clipboard, "_provider");
        BleResourceLifetime? hidLifetime = null;
        GattServiceProvider? hidProvider = null;
        object? keyboard = null;
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try { await hid.StartAsync(); throw new Exception("Expected HID publication failure"); }
            catch (InvalidOperationException ex) when (ex.Message == "Injected advertisement failure") { }
            hidLifetime ??= Field<BleResourceLifetime>(hid, "_resources");
            hidProvider ??= Field<GattServiceProvider>(hid, "_provider");
            keyboard ??= Field<object>(hid, "_keyboard");
            Check(hid.ServiceAvailable && !hidLifetime.IsDisposed &&
                ReferenceEquals(hidLifetime, Field<BleResourceLifetime>(hid, "_resources")) &&
                ReferenceEquals(hidProvider, Field<GattServiceProvider>(hid, "_provider")) &&
                ReferenceEquals(keyboard, Field<object>(hid, "_keyboard")),
                $"HID publication failure preserves service, characteristics and lifetime {attempt}/20");

            try { await clipboard.StartAsync(); throw new Exception("Expected clipboard publication failure"); }
            catch (InvalidOperationException ex) when (ex.Message == "Injected advertisement failure") { }
            Check(!clipboardLifetime.IsDisposed &&
                ReferenceEquals(clipboardProvider, Field<GattServiceProvider>(clipboard, "_provider")),
                $"Clipboard publication failure preserves service {attempt}/20");
        }
        Check(!await hid.EnsureAdvertisingAsync() && hid.ServiceAvailable && !hidLifetime!.IsDisposed,
            "Automatic recovery failure also preserves HID");
        fail = false;
        await hid.StartAsync();
        await clipboard.StartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (hid.Advertisement is not ("Started" or "StartedWithoutAllAdvertisementData") && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Check(hid.Advertisement is "Started" or "StartedWithoutAllAdvertisementData" && clipboard.Running,
            "Same native services publish after advertisement failure clears");
        Check(ReferenceEquals(hidProvider, Field<GattServiceProvider>(hid, "_provider")),
            "Recovery keeps original HID provider identity");
        // Inject loss of the discovery signal after native publication. Do not
        // StopAdvertising here: that explicitly unpublishes the service itself.
        advertisementAborted = true;
        fail = true;
        try { await hid.StartAsync(); throw new Exception("Expected recovery failure after publication"); }
        catch (InvalidOperationException ex) when (ex.Message == "Injected advertisement failure") { }
        Check(hid.ServiceAvailable && !hidLifetime!.IsDisposed &&
            ReferenceEquals(hidProvider, Field<GattServiceProvider>(hid, "_provider")),
            "Loss of advertising after publication does not discard HID service");
        fail = false;
        advertisementAborted = false;
        await hid.StartAsync();
        hid.Stop(); clipboard.StopAdvertising();
        Check(hidLifetime!.IsDisposed && clipboardLifetime.IsDisposed && !hid.ServiceAvailable && !clipboard.Running,
            "Explicit stop still releases both service lifetimes");
        Check(!await hid.EnsureAdvertisingAsync() && !hid.DesiredRunning,
            "Health check cannot restart explicitly stopped services");
        Console.WriteLine("PASS: Native advertisement fault recovery. Physical phone reconnection NOT tested.");
    }
}
