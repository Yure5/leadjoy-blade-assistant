using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LeadjoyBlade.Core.Devices;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.App.Views;

public partial class DevicePage : UserControl
{
    private readonly Dictionary<BladeButtons, (Border Border, TextBlock Text)> _chips = new();
    private Border? _vibrateChip;
    private bool? _lastNarrow;
    private double _lastViewportHeight = -1;
    private readonly DispatcherTimer _timer;
    private BladeReport? _latest;
    private int _reportCount;
    private DateTime _rateStart = DateTime.UtcNow;
    private int _rate;

    private Brush ChipIdle = Brushes.Transparent;
    private Brush ChipActive = Brushes.Transparent;
    private Brush ChipBorder = Brushes.Transparent;
    private Brush ChipText = Brushes.Gray;

    public DevicePage()
    {
        InitializeComponent();
        RefreshChipBrushes();
        ThemeManager.ThemeChanged += OnThemeChanged;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Loaded += (_, _) => UpdateDeadzoneVisuals();
        Loaded += (_, _) => UpdateResponsiveLayout();
        BuildChips();
        RefreshCandidates();

        var app = App.Current;
        app.Devices.ReportReceived += Devices_ReportReceived;
        app.Devices.ConnectionChanged += (_, connected) => Dispatcher.Invoke(() => OnConnectionChanged(connected));
        app.Devices.DeviceNameChanged += (_, name) => Dispatcher.Invoke(() => SetDeviceName(name));
        app.Devices.BatteryChanged += (_, level) => Dispatcher.Invoke(() => UpdateBattery(level));
        // 后台周期扫描到新候选（DS4 / Xbox / Switch 模式）后，列表自动刷新，
        // 不用再手动点「重新扫描」。
        app.Devices.CandidatesChanged += (_, _) => Dispatcher.Invoke(RefreshCandidates);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        Unloaded += (_, _) => { _timer.Stop(); ThemeManager.ThemeChanged -= OnThemeChanged; };
        Loaded += (_, _) =>
        {
            _timer.Start();
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;
            RefreshChipBrushes();
        };
    }

    public void Relayout()
    {
        UpdateResponsiveLayout();
        InvalidateMeasure();
        UpdateLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (CardsGrid == null) return;

        var windowWidth = Window.GetWindow(this)?.ActualWidth ?? ActualWidth;
        var narrow = windowWidth < 760;
        if (_lastNarrow == narrow) return;
        _lastNarrow = narrow;
        if (narrow)
        {
            CardsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            CardsGrid.ColumnDefinitions[1].Width = new GridLength(0);
            CardsGrid.ColumnDefinitions[2].Width = new GridLength(0);
            Grid.SetColumn(LeftCard, 0);
            Grid.SetRow(LeftCard, 0);
            Grid.SetColumn(RightCard, 0);
            Grid.SetRow(RightCard, 1);
            RightCard.Margin = new Thickness(0, 16, 0, 0);
        }
        else
        {
            CardsGrid.ColumnDefinitions[0].Width = new GridLength(1.05, GridUnitType.Star);
            CardsGrid.ColumnDefinitions[1].Width = new GridLength(16);
            CardsGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(LeftCard, 0);
            Grid.SetRow(LeftCard, 0);
            Grid.SetColumn(RightCard, 2);
            Grid.SetRow(RightCard, 0);
            RightCard.Margin = new Thickness(0);
        }
    }

    private void UpdateDeadzoneVisuals()
    {
        var settings = App.Current.Engine.Profile.Settings;
        SetDeadzoneRing(LeftDeadzoneRing, settings.LeftStickDeadzone);
        SetDeadzoneRing(RightDeadzoneRing, settings.RightStickDeadzone);
    }

    private static void SetDeadzoneRing(System.Windows.Shapes.Ellipse ring, double deadzone)
    {
        var diameter = 112 * Math.Clamp(deadzone, 0, 0.95);
        if (diameter < 6)
        {
            ring.Visibility = Visibility.Collapsed;
            return;
        }
        ring.Width = diameter;
        ring.Height = diameter;
        System.Windows.Controls.Canvas.SetLeft(ring, (112 - diameter) / 2);
        System.Windows.Controls.Canvas.SetTop(ring, (112 - diameter) / 2);
        ring.Visibility = Visibility.Visible;
    }

    private void OnThemeChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(RefreshChipBrushes);

    private void RefreshChipBrushes()
    {
        ChipIdle = (Brush)FindResource("Surface2");
        ChipActive = (Brush)FindResource("Accent");
        ChipBorder = (Brush)FindResource("Border");
        ChipText = (Brush)FindResource("TextMuted");
        foreach (var (_, chip) in _chips)
        {
            chip.Border.Background = ChipIdle;
            chip.Border.BorderBrush = ChipBorder;
            chip.Text.Foreground = ChipText;
        }
        if (_vibrateChip?.Child is TextBlock text) text.Foreground = ChipText;
    }

    private void BuildChips()
    {
        foreach (var button in BladeButtonsExtensions.All.Where(b => b != BladeButtons.Z))
        {
            var text = new TextBlock
            {
                Text = button.ToShortName(),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ChipText,
            };
            var border = new Border
            {
                Width = 62,
                Height = 30,
                Margin = new Thickness(0, 0, 7, 7),
                CornerRadius = new CornerRadius(7),
                Background = ChipIdle,
                BorderBrush = ChipBorder,
                BorderThickness = new Thickness(1),
                Child = text,
            };
            _chips[button] = (border, text);
            ButtonChips.Children.Add(border);
        }

        var vibrateText = new TextBlock
        {
            Text = "震动测试",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ChipText,
        };
        var vibrate = new Border
        {
            Width = 62,
            Height = 30,
            Margin = new Thickness(0, 0, 7, 7),
            CornerRadius = new CornerRadius(7),
            Background = ChipIdle,
            BorderBrush = ChipBorder,
            BorderThickness = new Thickness(1),
            Child = vibrateText,
            Cursor = System.Windows.Input.Cursors.Hand,
            Visibility = Visibility.Collapsed,
        };
        vibrate.MouseLeftButtonUp += async (_, _) => await RunVibrationAsync();
        _vibrateChip = vibrate;
        ButtonChips.Children.Add(vibrate);
    }

    private void Devices_ReportReceived(object? sender, BladeReport report)
    {
        _latest = report;
        _reportCount++;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _rateStart).TotalSeconds >= 1)
        {
            _rate = _reportCount;
            _reportCount = 0;
            _rateStart = now;
            ReportRateText.Text = $"{_rate} Hz";
        }

        var report = _latest;
        if (report == null) return;

        foreach (var (button, chip) in _chips)
        {
            var pressed = report.IsPressed(button);
            chip.Border.Background = pressed ? ChipActive : ChipIdle;
            chip.Border.BorderBrush = pressed ? ChipActive : ChipBorder;
            chip.Text.Foreground = pressed ? Brushes.White : ChipText;
        }

        SetStick(LeftStickDot, report.LeftXNorm, report.LeftYNorm);
        SetStick(RightStickDot, report.RightXNorm, report.RightYNorm);
        LeftStickText.Text = $"{report.LeftX}, {report.LeftY}";
        RightStickText.Text = $"{report.RightX}, {report.RightY}";
        LeftTriggerBar.Value = report.LeftTrigger / 2.55;
        RightTriggerBar.Value = report.RightTrigger / 2.55;
        LeftTriggerText.Text = report.LeftTrigger.ToString();
        RightTriggerText.Text = report.RightTrigger.ToString();

        var raw = BitConverter.ToString(report.RawContent);
        RawReportText.Text = $"按键 0x{(uint)report.Buttons:X8}    内容 {raw}";
    }

    private static void SetStick(System.Windows.Shapes.Ellipse dot, float x, float y)
    {
        // 保持圆形轨迹：超出单位圆的斜向满偏按向量长度归一化到圆周。
        var magnitude = MathF.Sqrt(x * x + y * y);
        if (magnitude > 1f)
        {
            x /= magnitude;
            y /= magnitude;
        }
        var cx = 56 + x * 42 - 9;
        var cy = 56 - y * 42 - 9;
        Canvas.SetLeft(dot, cx);
        Canvas.SetTop(dot, cy);
    }

    public void RefreshCandidates()
    {
        var list = App.Current.Devices.Candidates;
        CandidateList.ItemsSource = list;
        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (list.Count > 0 && CandidateList.SelectedIndex < 0) CandidateList.SelectedIndex = 0;
    }

    private void UpdateBattery(int level)
    {
    }

    private void SetDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        // 始终使用原版黑色风刃 Plus 图；白色 SE 图是误改，已删除。
        ControllerImage.Effect = null;
        ControllerImage.Source = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/LeadjoyBlade;component/Assets/blade_plus.png"));
    }

    public void OnConnectionChanged(bool connected)
    {
        ImageOverlay.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.IsEnabled = connected;
        ConnectButton.Content = connected ? "切换设备" : "连接选中设备";
        if (!connected) _latest = null;
        if (_vibrateChip != null)
            _vibrateChip.Visibility = connected &&
                                      App.Current.Devices.ConnectedKind == BladeTransportKind.XInput
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async void ConnectButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is not BladeDeviceCandidate candidate) return;
        await ConnectAsync(candidate);
    }

    private async Task ConnectAsync(BladeDeviceCandidate candidate)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            var ok = await App.Current.Devices.ConnectAsync(candidate);
            if (!ok) MessageBox.Show("连接失败，请查看诊断日志了解原因。", "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async void VibrateButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunVibrationAsync();
    }

    private async Task RunVibrationAsync()
    {
        var ok = await App.Current.Devices.VibrateAsync(0.8, 0.8, 500);
        if (!ok)
            MessageBox.Show("未检测到可震动的链路（Xbox XInput / DS4 HID）。",
                "震动测试", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void DisconnectButton_OnClick(object sender, RoutedEventArgs e)
    {
        await App.Current.Devices.DisconnectAsync();
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        App.Current.Devices.ClearCandidates();
        RefreshCandidates();
        await App.Current.Devices.ScanPairedAsync();
        RefreshCandidates();
    }

    private async void ShowAllCheck_OnChanged(object sender, RoutedEventArgs e)
    {
        if (App.Current.Devices == null) return;
        App.Current.Devices.IncludeAllBle = ShowAllCheck.IsChecked == true;
        App.Current.Devices.ClearCandidates();
        await App.Current.Devices.ScanPairedAsync();
        RefreshCandidates();
    }

    private async void ConnectAddress_OnClick(object sender, RoutedEventArgs e)
    {
        var text = AddressBox.Text.Trim().Replace(":", "").Replace("-", "").Replace(" ", "");
        if (!ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var address))
        {
            MessageBox.Show("请输入 12 位十六进制蓝牙地址，例如 A1B2C3D4E5F6。", "地址格式不正确",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await ConnectAsync(new BladeDeviceCandidate($"ble:{address:X12}", $"蓝牙 {address:X12}",
            BladeTransportKind.Ble, address));
    }

    private async void BleSelfTest_OnClick(object sender, RoutedEventArgs e)
    {
        var manager = App.Current.Devices;
        ShowAllCheck.IsChecked = true;
        manager.IncludeAllBle = true;
        manager.ClearCandidates();
        await manager.ScanPairedAsync();
        await Task.Delay(10000);

        var names = manager.AdvertisementNames;
        var message = names.Count == 0
            ? "10 秒内没有发现任何蓝牙广播（0 个）。\n\n请依次检查：\n" +
              "1. 关闭手机上的「动感指尖 / Leadjoy」App，最好暂时关闭手机蓝牙；\n" +
              "2. 在 Windows「蓝牙和其他设备」中删除 Xbox Wireless Controller / Wireless Controller；\n" +
              "3. 手柄关机再开机，按住 Home + Y 约 3 秒；\n" +
              "4. 再点一次「蓝牙自检」。\n\n" +
              "如果始终为 0，说明手柄没有进入 BLE 广播模式，或蓝牙适配器被其他程序占用。"
            : $"10 秒内发现 {names.Count} 个蓝牙广播：\n\n• " + string.Join("\n• ", names.Take(20)) +
              "\n\n如果其中有 Blade Plus / 风刃，请选中它并点「连接选中设备」。";

        MessageBox.Show(message, "蓝牙自检", MessageBoxButton.OK,
            names.Count == 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void OpenBleSettings_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:bluetooth",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "无法打开蓝牙设置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CandidateList_OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (CandidateList.SelectedItem is BladeDeviceCandidate candidate) await ConnectAsync(candidate);
    }
}



