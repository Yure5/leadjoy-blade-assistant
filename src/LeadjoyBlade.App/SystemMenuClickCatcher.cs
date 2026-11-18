using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace LeadjoyBlade.App;

/// <summary>
/// 系统菜单打开时的全屏透明点击捕获层。
/// 任意鼠标点击都会请求关闭菜单，包括双十字热键栏区域。
/// </summary>
public sealed class SystemMenuClickCatcher : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    public event EventHandler? DismissRequested;

    public SystemMenuClickCatcher()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // 几乎全透明但仍有可见像素，保证整块窗口参与 WPF 命中测试。
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        PreviewMouseDown += (_, _) => DismissRequested?.Invoke(this, EventArgs.Empty);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(handle, GwlExStyle) | WsExToolWindow | WsExNoActivate;
            SetWindowLong(handle, GwlExStyle, style);
        };
    }

    public void ShowCatcher()
    {
        if (!IsVisible) Show();
    }

    public void HideCatcher()
    {
        if (IsVisible) Hide();
    }
}
