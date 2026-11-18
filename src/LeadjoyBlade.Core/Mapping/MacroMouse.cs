using LeadjoyBlade.Core.Input;

namespace LeadjoyBlade.Core.Mapping;

/// <summary>
/// 固定位置鼠标宏的统一执行逻辑。和幻想模式系统菜单的鼠标点击一致：
/// 配置了进程时使用进程客户区百分比，否则使用采集时记录的显示器。
/// </summary>
public static class MacroMouse
{
    public static bool TryGetPoint(MacroStep step, string? processName, out int x, out int y)
    {
        if (step.MouseScreen)
        {
            return ScreenTarget.TryGetPoint(step.MouseMonitor,
                step.MouseMonitorX, step.MouseMonitorY,
                step.MouseMonitorWidth, step.MouseMonitorHeight,
                step.MouseX, step.MouseY, out x, out y);
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            var handle = GameWindowTarget.FindProcessWindow(processName);
            var target = handle == IntPtr.Zero ? null : GameWindowTarget.Create(handle);
            if (target != null && target.TryGetScreenPoint(step.MouseX, step.MouseY, out x, out y))
                return true;
        }

        // 没有可用的进程窗口时退回虚拟桌面百分比，保证仍然点得到。
        var (vx, vy, vw, vh) = ScreenTarget.VirtualScreen();
        x = vx + (int)Math.Round(Math.Clamp(step.MouseX, 0, 1) * (vw - 1));
        y = vy + (int)Math.Round(Math.Clamp(step.MouseY, 0, 1) * (vh - 1));
        return true;
    }

    public static bool Click(MacroStep step, MouseInjector mouse, string? processName, int holdMs = 45)
    {
        if (!step.MouseUsePosition || !TryGetPoint(step, processName, out var x, out var y)) return false;
        mouse.ClickAt(x, y, step.MouseButton, holdMs);
        return true;
    }

    public static bool MoveTo(MacroStep step, MouseInjector mouse, string? processName)
    {
        if (!step.MouseUsePosition || !TryGetPoint(step, processName, out var x, out var y)) return false;
        return mouse.MoveTo(x, y);
    }
}
