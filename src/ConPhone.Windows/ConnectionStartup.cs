using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace ConPhone.Windows;

internal static class ConnectionStartup
{
    // Publish the clipboard service for GATT discovery but reserve the limited
    // advertising packet for HID. Android also scans for the HID UUID (0x1812).
    public static GattServiceProviderAdvertisingParameters HidAdvertisement() => new()
    {
        IsConnectable = true,
        IsDiscoverable = true
    };

    public static GattServiceProviderAdvertisingParameters ClipboardAdvertisement() => new()
    {
        IsConnectable = false,
        IsDiscoverable = true
    };

    public static async Task RunAsync(
        Func<Task> prepareClipboard,
        Func<Task> startHid,
        Func<Task> startClipboard,
        Action<string, Exception> optionalFailure)
    {
        // Keep successful GATT preparation ordered before HID, but never make
        // Android's optional clipboard service a prerequisite for iOS/Android HID.
        try { await prepareClipboard(); }
        catch (Exception ex) { optionalFailure("준비", ex); }

        await startHid(); // A failed HID start must not advertise only clipboard.

        try { await startClipboard(); }
        catch (Exception ex) { optionalFailure("시작", ex); }
    }
}
