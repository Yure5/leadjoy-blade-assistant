using System.Runtime.InteropServices;

namespace LeadjoyBlade.Core.Input;

/// <summary>一块显示器（物理像素坐标，左上角为虚拟桌面坐标）。</summary>
public readonly record struct ScreenMonitor(
    string Device, int X, int Y, int Width, int Height, bool Primary, IntPtr Handle)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>
/// 多屏坐标工具：按显示器记录/还原固定位置点击，避免把点落到另一块屏幕。
/// </summary>
public static class ScreenTarget
{
    private const int MonitorInfoPrimary = 0x1;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    public static IReadOnlyList<ScreenMonitor> GetMonitors()
    {
        var list = new List<ScreenMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr hdc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(handle, ref info))
            {
                list.Add(new ScreenMonitor(
                    info.Device ?? "",
                    info.Monitor.Left,
                    info.Monitor.Top,
                    Math.Max(1, info.Monitor.Right - info.Monitor.Left),
                    Math.Max(1, info.Monitor.Bottom - info.Monitor.Top),
                    (info.Flags & MonitorInfoPrimary) != 0,
                    handle));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static ScreenMonitor? GetMonitorAt(int x, int y)
    {
        foreach (var monitor in GetMonitors())
            if (monitor.Contains(x, y)) return monitor;
        return null;
    }

    public static ScreenMonitor? Find(string? device)
    {
        if (string.IsNullOrWhiteSpace(device)) return null;
        foreach (var monitor in GetMonitors())
            if (string.Equals(monitor.Device, device, StringComparison.OrdinalIgnoreCase)) return monitor;
        return null;
    }

    public static ScreenMonitor Primary()
    {
        foreach (var monitor in GetMonitors())
            if (monitor.Primary) return monitor;
        return new ScreenMonitor("", 0, 0,
            Math.Max(1, GetSystemMetrics(SmCxVirtualScreen)),
            Math.Max(1, GetSystemMetrics(SmCyVirtualScreen)), true, IntPtr.Zero);
    }

    public static (int X, int Y, int Width, int Height) VirtualScreen() =>
        (GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
         Math.Max(1, GetSystemMetrics(SmCxVirtualScreen)), Math.Max(1, GetSystemMetrics(SmCyVirtualScreen)));

    /// <summary>把屏幕物理坐标换算成显示器内的 0~1 比例。</summary>
    public static bool TryGetRelative(ScreenMonitor monitor, int screenX, int screenY,
        out double relativeX, out double relativeY)
    {
        relativeX = Math.Clamp((screenX - monitor.X) / (double)Math.Max(1, monitor.Width - 1), 0, 1);
        relativeY = Math.Clamp((screenY - monitor.Y) / (double)Math.Max(1, monitor.Height - 1), 0, 1);
        return true;
    }

    /// <summary>按记录的显示器把 0~1 比例换算成屏幕坐标；设备找不到时用采集边界兜底。</summary>
    public static bool TryGetPoint(string? device, int fallbackX, int fallbackY, int fallbackWidth, int fallbackHeight,
        double relativeX, double relativeY, out int x, out int y)
    {
        ScreenMonitor monitor;
        var found = Find(device);
        if (found is { } live)
        {
            monitor = live;
        }
        else if (fallbackWidth > 0 && fallbackHeight > 0)
        {
            monitor = new ScreenMonitor(device ?? "", fallbackX, fallbackY, fallbackWidth, fallbackHeight, false, IntPtr.Zero);
        }
        else
        {
            monitor = Primary();
        }

        x = monitor.X + (int)Math.Round(Math.Clamp(relativeX, 0, 1) * (monitor.Width - 1));
        y = monitor.Y + (int)Math.Round(Math.Clamp(relativeY, 0, 1) * (monitor.Height - 1));
        return true;
    }

    /// <summary>屏幕模式下按记录的显示器点击；找不到该设备时用采集时的边界兜底。</summary>
    public static bool Click(string? device, int fallbackX, int fallbackY, int fallbackWidth, int fallbackHeight,
        double relativeX, double relativeY, MouseButtonKind button, MouseInjector mouse, int holdMs = 45)
    {
        if (!TryGetPoint(device, fallbackX, fallbackY, fallbackWidth, fallbackHeight,
                relativeX, relativeY, out var x, out var y)) return false;
        mouse.ClickAt(x, y, button, holdMs);
        return true;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
