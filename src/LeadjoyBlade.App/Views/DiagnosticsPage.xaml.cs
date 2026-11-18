using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LeadjoyBlade.App.Views;

public partial class DiagnosticsPage : UserControl
{
    private readonly StringBuilder _buffer = new();
    private readonly DispatcherTimer _timer;

    public DiagnosticsPage()
    {
        InitializeComponent();
        // 补上打开本页之前产生的日志（连接、按键映射等）。
        foreach (var line in LogBus.Snapshot()) _buffer.AppendLine(line);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            LogBus.Message -= OnLog;
        };
        Loaded += (_, _) =>
        {
            _timer.Start();
            // 页面会被重复创建，先退订再订阅，避免同一条日志显示多次。
            LogBus.Message -= OnLog;
            LogBus.Message += OnLog;
            Refresh();
        };
    }

    private void OnLog(object? sender, string message)
    {
        lock (_buffer)
        {
            _buffer.AppendLine(message);
            if (_buffer.Length > 120_000) _buffer.Remove(0, _buffer.Length - 100_000);
        }
    }

    private void Refresh()
    {
        lock (_buffer)
        {
            if (LogBox.Text.Length != _buffer.Length)
            {
                LogBox.Text = _buffer.ToString();
                LogBox.ScrollToEnd();
            }
        }

        DeviceStateText.Text = App.Current.Devices.IsConnected ? "已连接" : "未连接";
        DeviceNameText.Text = App.Current.Devices.ConnectedName ?? "—";
        ProfileText.Text = App.Current.Engine.Profile.Name;
    }

    private void Copy_OnClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogBox.Text); } catch { }
    }

    private void Clear_OnClick(object sender, RoutedEventArgs e)
    {
        lock (_buffer) _buffer.Clear();
        LogBox.Clear();
    }

    private void OpenData_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Core.Config.ProfileStore.DataDirectory,
                UseShellExecute = true,
            });
        }
        catch { }
    }
}
