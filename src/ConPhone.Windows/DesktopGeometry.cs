using System.Runtime.InteropServices;
using ConPhone.Core;

namespace ConPhone.Windows;

internal static class DesktopGeometry
{
    public static ScreenBounds[] Capture()
    {
        var old = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            var screens = new List<ScreenBounds>();
            EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Rect r, nint data) =>
            {
                screens.Add(new(r.Left, r.Top, r.Right, r.Bottom));
                return true;
            }, 0);
            return screens.ToArray();
        }
        finally { if (old != 0) SetThreadDpiAwarenessContext(old); }
    }

    public static void UsePhysicalCoordinates() => SetThreadDpiAwarenessContext(new nint(-4));
    public static bool Cursor(out Point point) => GetCursorPos(out point);
    public static void ReturnTo(EdgeEntry entry)
    {
        var screens = Capture();
        var screen = screens.FirstOrDefault(s => s == entry.Screen);
        if (screen == default) screen = screens.FirstOrDefault();
        if (screen == default) return;
        var x = entry.Side == PhoneSide.Right ? screen.Right - 17 : screen.Left + 16;
        SetCursorPos(Math.Clamp(x, screen.Left, screen.Right - 1),
            Math.Clamp(entry.Y, screen.Top, screen.Bottom - 1));
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref Rect rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}
