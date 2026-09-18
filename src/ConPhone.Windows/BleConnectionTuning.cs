using System.Reflection;
using Windows.Devices.Bluetooth;

namespace ConPhone.Windows;

internal static class BleConnectionTuning
{
    public static bool TryGetConnectionIntervalUnits(
        BluetoothLEDevice device,
        out ushort intervalUnits)
    {
        intervalUnits = 0;

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return false;

        try
        {
            var method = device.GetType().GetMethod(
                "GetConnectionParameters",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            var parameters = method?.Invoke(device, null);
            if (parameters is null) return false;

            var property = parameters.GetType().GetProperty(
                "ConnectionInterval",
                BindingFlags.Instance | BindingFlags.Public);

            var value = property?.GetValue(parameters);
            if (value is null) return false;

            intervalUnits = Convert.ToUInt16(value);
            return intervalUnits != 0;
        }
        catch
        {
            return false;
        }
    }

    public static IDisposable? TryRequestThroughputOptimized(
        BluetoothLEDevice device)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return null;

        try
        {
            var assembly = typeof(BluetoothLEDevice).Assembly;
            var preferredType = assembly.GetType(
                "Windows.Devices.Bluetooth.BluetoothLEPreferredConnectionParameters",
                throwOnError: false);

            if (preferredType is null) return null;

            var throughput = preferredType.GetProperty(
                    "ThroughputOptimized",
                    BindingFlags.Static | BindingFlags.Public)
                ?.GetValue(null);

            if (throughput is null) return null;

            var method = device.GetType().GetMethod(
                "RequestPreferredConnectionParameters",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: [preferredType],
                modifiers: null);

            return method?.Invoke(device, [throughput]) as IDisposable;
        }
        catch
        {
            return null;
        }
    }
}
