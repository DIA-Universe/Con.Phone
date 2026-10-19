using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace ConPhone.Windows;

internal static class BleNativeState
{
    // Disconnected/closed WinRT objects can surface either a projected exception
    // or COMException. These statuses are expected while a link is changing.
    internal static bool IsUnavailable(Exception error) =>
        error is ObjectDisposedException || error.HResult is
            unchecked((int)0x80010108) or // RPC_E_DISCONNECTED
            unchecked((int)0x8000000E) or // E_ILLEGAL_METHOD_CALL
            unchecked((int)0x80000013);   // RO_E_CLOSED

    internal static bool IsActive(GattSession session)
    {
        try { return session.SessionStatus == GattSessionStatus.Active; }
        catch (Exception ex) when (IsUnavailable(ex)) { return false; }
    }

    internal static T Read<T>(Func<T> read, T unavailable)
    {
        try { return read(); }
        catch (Exception ex) when (IsUnavailable(ex)) { return unavailable; }
    }
}
