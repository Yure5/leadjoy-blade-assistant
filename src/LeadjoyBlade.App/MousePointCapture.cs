using System.Threading;
using System.Windows;
using LeadjoyBlade.Core.Input;

namespace LeadjoyBlade.App;

/// <summary>一次鼠标位置采集的结果（坐标是 0~1 的相对值）。</summary>
public sealed record MouseCaptureResult(
    bool Screen, double X, double Y, string? Monitor,
    int MonitorX, int MonitorY, int MonitorWidth, int MonitorHeight);

/// <summary>
/// 「设置鼠标位置」的共用流程：激活配置进程（未配置则全屏）、最小化本程序窗口、
/// 打开采集遮罩、结束后恢复窗口并返回坐标。系统菜单动作与独立宏步骤共用。
/// </summary>
public static class MousePointCapture
{
    public static MouseCaptureResult? Capture(Window? owner, bool storedScreen,
        double initialX, double initialY, string? monitorDevice)
    {
        var process = App.Current.Engine.Profile.ProcessName;
        var screenMode = string.IsNullOrWhiteSpace(process);
        GameWindowTarget? window = null;

        if (!screenMode)
        {
            var handle = GameWindowTarget.FindProcessWindow(process);
            if (handle == IntPtr.Zero)
            {
                MessageBox.Show(owner,
                    $"配置的进程「{process}」没有运行。\n请先启动它；或在配置里清空进程名后改用全屏坐标。",
                    "设置鼠标位置", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            GameWindowTarget.ActivateWindow(handle);
            // 系统有时会拒绝首次 SetForegroundWindow；轮询并再试一次，确认真的到前台再显示遮罩。
            for (var attempt = 0; attempt < 12 && !GameWindowTarget.IsForeground(handle); attempt++)
            {
                Thread.Sleep(60);
                if (attempt == 5) GameWindowTarget.ActivateWindow(handle);
            }
            Thread.Sleep(120);
            if (!GameWindowTarget.IsForeground(handle))
            {
                MessageBox.Show(owner,
                    $"无法把进程「{process}」的窗口切到前台。\n请先手动点一下该窗口，或确认它没有被最小化/被其他全屏程序挡住。",
                    "设置鼠标位置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            window = GameWindowTarget.Create(handle);
            if (window == null)
            {
                MessageBox.Show(owner, "无法读取进程窗口区域，请确认窗口没有最小化。", "设置鼠标位置",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
        }

        // 采集时本程序的窗口会挡住游戏窗口；最小化而不是 Hide/Show，
        // 否则 ShowDialog 的窗口会丢失对话框状态，之后点“取消”设置 DialogResult 会抛异常。
        var minimized = new List<(Window Window, WindowState State)>();
        foreach (Window w in Application.Current.Windows)
        {
            if (!w.IsVisible || w.WindowState == WindowState.Minimized) continue;
            minimized.Add((w, w.WindowState));
            w.WindowState = WindowState.Minimized;
        }

        try
        {
            var hasInitial = storedScreen == screenMode;
            var picker = new MousePointPickerWindow(window, screenMode ? monitorDevice : null,
                initialX, initialY, hasInitial);
            if (picker.ShowDialog() != true) return null;
            return new MouseCaptureResult(picker.ResultScreen, picker.ResultX, picker.ResultY,
                picker.ResultMonitor, picker.ResultMonitorX, picker.ResultMonitorY,
                picker.ResultMonitorWidth, picker.ResultMonitorHeight);
        }
        finally
        {
            foreach (var (w, state) in minimized)
            {
                try { w.WindowState = state; } catch { }
            }
            owner?.Activate();
        }
    }
}
