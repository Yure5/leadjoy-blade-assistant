using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using LeadjoyBlade.Core.Input;

namespace LeadjoyBlade.App;

/// <summary>
/// “设置鼠标位置”采集窗口：在进程窗口（或全屏）上盖一层半透明遮罩，
/// 圆形十字准星跟随鼠标并显示实时坐标；左键落点，落点后的准星可拖动微调，ESC 保存退出。
/// </summary>
public partial class MousePointPickerWindow : Window
{
    private const int SwpShowWindow = 0x0040;
    private const int SwpNoActivate = 0x0010;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoSize = 0x0001;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    private readonly GameWindowTarget? _window;
    private readonly string? _monitorDevice;
    private readonly double _initialX;
    private readonly double _initialY;
    private readonly bool _hasInitial;
    private bool _hasPoint;
    private bool _dragging;
    private double _relX;
    private double _relY;
    private double _pointDipX;
    private double _pointDipY;
    private ScreenMonitor? _monitor;

    public double ResultX => _relX;
    public double ResultY => _relY;
    public bool ResultScreen => _window == null;
    public string? ResultMonitor => _monitor?.Device ?? _monitorDevice;
    public int ResultMonitorX => _monitor?.X ?? 0;
    public int ResultMonitorY => _monitor?.Y ?? 0;
    public int ResultMonitorWidth => _monitor?.Width ?? 0;
    public int ResultMonitorHeight => _monitor?.Height ?? 0;

    public MousePointPickerWindow(GameWindowTarget? window, string? monitorDevice,
        double initialX = 0, double initialY = 0, bool hasInitial = false)
    {
        InitializeComponent();
        _window = window;
        _monitorDevice = monitorDevice;
        _initialX = initialX;
        _initialY = initialY;
        _hasInitial = hasInitial;
        if (!string.IsNullOrWhiteSpace(monitorDevice)) _monitor = ScreenTarget.Find(monitorDevice);
        TitleText.Text = window == null
            ? "全屏点选：多屏时按点击所在的显示器记录"
            : "在游戏窗口内点击要点击的位置";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var (x, y, width, height) = TargetRect();
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HwndTopmost, x, y, width, height, SwpShowWindow);
        SetForegroundWindow(hwnd);
        Focus();
        UpdateLayout();
        Opacity = 1;
        // 把目标窗口抬到普通窗口最上层（本窗口是 Topmost，仍在它上面），
        // 即使前台激活被系统拒绝，遮罩下也能看到游戏窗口而不是编辑器。
        if (_window is { Handle: var target } && target != IntPtr.Zero)
            SetWindowPos(target, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

        if (_hasInitial)
        {
            var scaleX = ActualWidth > 0 ? ActualWidth : width;
            var scaleY = ActualHeight > 0 ? ActualHeight : height;
            double dipX, dipY;
            if (_window == null && _monitor is { } monitor)
            {
                // 屏幕模式：初始点要还原到该显示器在虚拟桌面里的位置。
                var vs = ScreenTarget.VirtualScreen();
                var screenX = monitor.X + _initialX * (monitor.Width - 1);
                var screenY = monitor.Y + _initialY * (monitor.Height - 1);
                dipX = (screenX - vs.X) / Math.Max(1, vs.Width) * scaleX;
                dipY = (screenY - vs.Y) / Math.Max(1, vs.Height) * scaleY;
            }
            else
            {
                dipX = _initialX * scaleX;
                dipY = _initialY * scaleY;
            }
            SetPoint(_initialX, _initialY, dipX, dipY);
        }
        UpdateFollowMarker(Mouse.GetPosition(Root));
    }

    private (int X, int Y, int Width, int Height) TargetRect()
    {
        if (_window != null && _window.TryRefresh())
            return (_window.X, _window.Y, _window.Width, _window.Height);
        return ScreenTarget.VirtualScreen();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        DialogResult = _hasPoint;
        Close();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(Root);
        UpdateFollowMarker(point);
        if (_dragging)
        {
            RecordAt(point);
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(Root);
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;

        if (_hasPoint && Distance(point, _pointDipX, _pointDipY) <= 22)
        {
            _dragging = true;
            Mouse.Capture(this);
            RecordAt(point);
            return;
        }

        _dragging = false;
        RecordAt(point);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Mouse.Capture(null);
    }

    private void UpdateFollowMarker(Point point)
    {
        CursorMarker.Visibility = Visibility.Visible;
        Canvas.SetLeft(CursorMarker, point.X - CursorMarker.Width / 2);
        Canvas.SetTop(CursorMarker, point.Y - CursorMarker.Height / 2);
        UpdateBadge(point);
    }

    private void UpdateBadge(Point point)
    {
        if (!TryComputeRelative(out var relX, out var relY, out var text)) return;
        CoordBadge.Visibility = Visibility.Visible;
        CoordText.Text = text;
        CoordBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var badgeWidth = CoordBadge.DesiredSize.Width;
        var badgeHeight = CoordBadge.DesiredSize.Height;
        var left = Math.Clamp(point.X + 22, 4, Math.Max(4, ActualWidth - badgeWidth - 4));
        var top = Math.Clamp(point.Y + 18, 4, Math.Max(4, ActualHeight - badgeHeight - 4));
        Canvas.SetLeft(CoordBadge, left);
        Canvas.SetTop(CoordBadge, top);
        _ = relX;
        _ = relY;
    }

    private bool TryComputeRelative(out double relX, out double relY, out string text)
    {
        relX = relY = 0;
        text = "";
        if (!GetCursorPos(out var cursor)) return false;

        if (_window != null)
        {
            _window.TryRefresh();
            if (_window.Width <= 0 || _window.Height <= 0) return false;
            relX = Math.Clamp((cursor.X - _window.X) / (double)Math.Max(1, _window.Width - 1), 0, 1);
            relY = Math.Clamp((cursor.Y - _window.Y) / (double)Math.Max(1, _window.Height - 1), 0, 1);
            text = $"窗口 X {cursor.X - _window.X}  Y {cursor.Y - _window.Y}    ({relX * 100:0.#}%, {relY * 100:0.#}%)";
            return true;
        }

        var monitor = ScreenTarget.GetMonitorAt(cursor.X, cursor.Y) ?? ScreenTarget.Primary();
        ScreenTarget.TryGetRelative(monitor, cursor.X, cursor.Y, out relX, out relY);
        text = $"屏幕 {ShortName(monitor.Device)}  X {cursor.X - monitor.X}  Y {cursor.Y - monitor.Y}    ({relX * 100:0.#}%, {relY * 100:0.#}%)";
        _ = monitor;
        return true;
    }

    private void RecordAt(Point dipPoint)
    {
        if (!GetCursorPos(out var cursor)) return;

        if (_window != null)
        {
            _window.TryRefresh();
            if (_window.Width <= 0 || _window.Height <= 0) return;
            _relX = Math.Clamp((cursor.X - _window.X) / (double)Math.Max(1, _window.Width - 1), 0, 1);
            _relY = Math.Clamp((cursor.Y - _window.Y) / (double)Math.Max(1, _window.Height - 1), 0, 1);
        }
        else
        {
            _monitor = ScreenTarget.GetMonitorAt(cursor.X, cursor.Y) ?? ScreenTarget.Primary();
            ScreenTarget.TryGetRelative(_monitor.Value, cursor.X, cursor.Y, out _relX, out _relY);
        }

        _hasPoint = true;
        _pointDipX = dipPoint.X;
        _pointDipY = dipPoint.Y;
        FixedMarker.Visibility = Visibility.Visible;
        Canvas.SetLeft(FixedMarker, dipPoint.X - FixedMarker.Width / 2);
        Canvas.SetTop(FixedMarker, dipPoint.Y - FixedMarker.Height / 2);
        UpdateBadge(dipPoint);
    }

    private void SetPoint(double relX, double relY, double dipX, double dipY)
    {
        _relX = Math.Clamp(relX, 0, 1);
        _relY = Math.Clamp(relY, 0, 1);
        _hasPoint = true;
        _pointDipX = dipX;
        _pointDipY = dipY;
        FixedMarker.Visibility = Visibility.Visible;
        Canvas.SetLeft(FixedMarker, dipX - FixedMarker.Width / 2);
        Canvas.SetTop(FixedMarker, dipY - FixedMarker.Height / 2);
    }

    private static double Distance(Point point, double x, double y)
        => Math.Sqrt((point.X - x) * (point.X - x) + (point.Y - y) * (point.Y - y));

    private static string ShortName(string device)
        => string.IsNullOrWhiteSpace(device) ? "显示器" : device.Replace(@"\\.\", "");

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, int flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}



