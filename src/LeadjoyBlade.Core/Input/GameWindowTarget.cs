using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LeadjoyBlade.Core.Input;

/// <summary>
/// 记录游戏窗口客户区在屏幕上的位置，用于“基于游戏窗口固定位置”的鼠标点击。
/// 坐标以客户区百分比保存，窗口移动或分辨率变化后仍能落到相对位置。
/// </summary>
public sealed class GameWindowTarget
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    public IntPtr Handle { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public bool IsValid => Handle != IntPtr.Zero && Width > 0 && Height > 0;

    /// <summary>捕获当前前台游戏窗口；若前台是本程序，则尝试按配置的进程名查找。</summary>
    public static GameWindowTarget? Capture(string? processName)
    {
        var foreground = GetForegroundWindow();
        if (IsUsable(foreground) && !IsOwnProcess(foreground))
            return FromHandle(foreground);

        var name = NormalizeProcessName(processName);
        if (name != null)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    var handle = process.MainWindowHandle;
                    if (handle != IntPtr.Zero && IsUsable(handle))
                        return FromHandle(handle);
                }
            }
            catch
            {
                // 进程可能已退出或权限不足，退回全屏坐标。
            }
        }

        // 再试一次前台：启动器/覆盖层可能短暂抢焦点；但不能把本程序自己的窗口当成游戏。
        if (IsUsable(foreground) && !IsOwnProcess(foreground)) return FromHandle(foreground);
        return null;
    }

    /// <summary>按窗口句柄创建目标（用于编辑器点选）。</summary>
    public static GameWindowTarget? Create(IntPtr handle)
        => IsUsable(handle) ? FromHandle(handle) : null;

    /// <summary>按配置的进程名查找主窗口；多个进程时取第一个可用主窗口。</summary>
    public static IntPtr FindProcessWindow(string? processName)
    {
        var name = NormalizeProcessName(processName);
        if (name == null) return IntPtr.Zero;
        var pids = new HashSet<uint>();
        try
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                pids.Add((uint)process.Id);
                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero && IsUsable(handle)) return handle;
            }
        }
        catch
        {
            // 权限不足或进程退出。
        }

        if (pids.Count == 0) return IntPtr.Zero;

        // 有些游戏/启动器不会填 Process.MainWindowHandle，直接枚举顶层窗口按 PID 匹配。
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains(pid)) return true;
            if ((GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) != 0) return true;
            if (GetWindowTextLength(hwnd) == 0) return true;
            if (!IsUsable(hwnd)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>把目标窗口带到前台，方便点选时看清窗口内容。</summary>
    public static bool ActivateWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle)) return false;
        if (IsIconic(handle)) ShowWindow(handle, SwRestore);
        else ShowWindow(handle, SwShow);

        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(handle, out _);
        var attached = false;
        if (foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread)
            attached = AttachThreadInput(foregroundThread, targetThread, true);

        try
        {
            // 先短暂置顶再取消，确保 z-order 也排在前面（仅 SetForegroundWindow 有时会被系统拒绝）。
            SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            SetWindowPos(handle, HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            SetActiveWindow(handle);
            SetFocus(handle);
        }
        finally
        {
            if (attached) AttachThreadInput(foregroundThread, targetThread, false);
        }

        if (GetForegroundWindow() != handle) SwitchToThisWindow(handle, true);
        return GetForegroundWindow() == handle;
    }

    public static bool IsForeground(IntPtr handle)
        => handle != IntPtr.Zero && GetForegroundWindow() == handle;


    private static GameWindowTarget? FromHandle(IntPtr handle)
    {
        var target = new GameWindowTarget { Handle = handle };
        return target.TryRefresh() ? target : null;
    }

    /// <summary>重新读取客户区；窗口销毁或最小化时返回 false。</summary>
    public bool TryRefresh()
    {
        if (Handle == IntPtr.Zero || !IsWindow(Handle)) return false;
        if (!GetClientRect(Handle, out var rect)) return false;
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;

        var origin = new Point { X = 0, Y = 0 };
        if (!ClientToScreen(Handle, ref origin)) return false;

        X = origin.X;
        Y = origin.Y;
        Width = rect.Right - rect.Left;
        Height = rect.Bottom - rect.Top;
        return Width > 0 && Height > 0;
    }

    /// <summary>把屏幕坐标换算成当前窗口客户区内的百分比；窗口无效时返回 false。</summary>
    public bool TryGetRelativePoint(int screenX, int screenY, out double relativeX, out double relativeY)
    {
        relativeX = relativeY = 0;
        if (!TryRefresh()) return false;
        relativeX = Math.Clamp((screenX - X) / (double)Math.Max(1, Width - 1), 0, 1);
        relativeY = Math.Clamp((screenY - Y) / (double)Math.Max(1, Height - 1), 0, 1);
        return true;
    }

    public static bool TryGetCursorPosition(out int x, out int y)
    {
        var point = new Point();
        var ok = GetCursorPos(out point);
        x = point.X;
        y = point.Y;
        return ok;
    }

    public bool TryGetScreenPoint(double relativeX, double relativeY, out int x, out int y)
    {
        x = y = 0;
        if (!TryRefresh())
        {
            // 窗口已失效：退化为整个虚拟桌面，仍然保证固定比例点击可用。
            X = GetSystemMetrics(SmXVirtualScreen);
            Y = GetSystemMetrics(SmYVirtualScreen);
            Width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
            Height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
            Handle = IntPtr.Zero;
        }

        x = X + (int)Math.Round(Math.Clamp(relativeX, 0, 1) * (Width - 1));
        y = Y + (int)Math.Round(Math.Clamp(relativeY, 0, 1) * (Height - 1));
        return true;
    }

    public static bool ClickRelative(GameWindowTarget? target, double relativeX, double relativeY,
        MouseButtonKind button, MouseInjector mouse, int holdMs = 45)
    {
        var actual = target;
        if (actual == null || !actual.IsValid || !IsWindow(actual.Handle)) actual = Capture(null);
        int x, y;
        if (actual == null)
        {
            var fallback = new GameWindowTarget
            {
                X = GetSystemMetrics(SmXVirtualScreen),
                Y = GetSystemMetrics(SmYVirtualScreen),
                Width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen)),
                Height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen)),
            };
            fallback.TryGetScreenPoint(relativeX, relativeY, out x, out y);
        }
        else if (!actual.TryGetScreenPoint(relativeX, relativeY, out x, out y))
        {
            return false;
        }

        mouse.ClickAt(x, y, button, holdMs);
        return true;
    }

    private static bool IsUsable(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle) || !IsWindowVisible(handle)) return false;
        if (!GetClientRect(handle, out var rect)) return false;
        return rect.Right - rect.Left > 0 && rect.Bottom - rect.Top > 0;
    }

    private static bool IsOwnProcess(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static string? NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr data);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr data);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
}






