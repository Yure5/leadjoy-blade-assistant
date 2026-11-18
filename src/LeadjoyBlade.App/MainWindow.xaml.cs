using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using LeadjoyBlade.App.Views;
using LeadjoyBlade.Core.Devices;
using Forms = System.Windows.Forms;

namespace LeadjoyBlade.App;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private readonly Dictionary<string, UserControl> _pages = new();
    private readonly DispatcherTimer _foregroundTimer;
    private Forms.NotifyIcon? _tray;
    private bool _reallyExit;
    private bool _syncingNav;
    private bool _customMaximized;
    private bool _handlingMaximize;
    private Rect _restoreBounds;
    private bool? _lastNarrow;
    private string? _currentProfileName;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public MainWindow()
    {
        InitializeComponent();
        FitToScreen();
        SizeChanged += (_, _) => UpdateHeaderLayout();
        StateChanged += (_, _) => OnWindowStateChanged();
        Loaded += (_, _) => UpdateHeaderLayout();
        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;

        _foregroundTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _foregroundTimer.Tick += ForegroundTimer_Tick;
    }

    private void FitToScreen()
    {
        var work = SystemParameters.WorkArea;
        var maxWidth = Math.Max(640, work.Width - 24);
        var maxHeight = Math.Max(480, work.Height - 24);
        MinWidth = Math.Min(MinWidth, maxWidth);
        MinHeight = Math.Min(MinHeight, maxHeight);
        Width = Math.Min(Width, maxWidth);
        var portrait = work.Height > work.Width;
        var targetHeight = portrait
            ? Math.Min(maxHeight, Math.Max(820, work.Height * 0.82))
            : Math.Min(Height, maxHeight);
        Height = targetHeight;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    private void UpdateHeaderLayout()
    {
        var narrow = ActualWidth < 1000;
        if (_lastNarrow == narrow) return;
        _lastNarrow = narrow;
        BrandBadge.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        ProfileLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        EnableLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        ProfileCombo.Width = narrow ? 110 : 150;
        Sidebar.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = narrow ? new GridLength(0) : new GridLength(218);
        TopNav.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateThemeIcon();
        var app = App.Current;
        foreach (var profile in app.Store.Profiles)
            ProfileCombo.Items.Add(profile.Name);
        ProfileCombo.SelectedItem = app.Store.Settings.LastProfile ?? app.Store.Profiles.FirstOrDefault()?.Name;
        _currentProfileName = ProfileCombo.SelectedItem as string;

        _syncingNav = true;
        foreach (var button in new[] { NavDevice, NavMapping, NavChords, NavMacro, NavFantasy, NavIme, NavSettings, NavDiagnostics })
            button.IsChecked = false;
        foreach (var button in new[] { TopNavDevice, TopNavMapping, TopNavChords, TopNavMacro, TopNavFantasy, TopNavIme, TopNavSettings, TopNavDiagnostics })
            button.IsChecked = false;
        NavDevice.IsChecked = true;
        TopNavDevice.IsChecked = true;
        _syncingNav = false;
        ShowPage("device");

        // 开发/演示：可通过环境变量直接打开指定页面
        var startPage = Environment.GetEnvironmentVariable("LEADJOY_START_PAGE");
        if (!string.IsNullOrWhiteSpace(startPage))
        {
            switch (startPage.ToLowerInvariant())
            {
                case "mapping": NavMapping.IsChecked = true; break;
                case "chords": NavChords.IsChecked = true; break;
                case "macro": NavMacro.IsChecked = true; break;
                case "fantasy": NavFantasy.IsChecked = true; break;
                case "ime": NavIme.IsChecked = true; break;
                case "settings": NavSettings.IsChecked = true; break;
                case "diagnostics": NavDiagnostics.IsChecked = true; break;
            }
        }

        EnableSwitch.IsChecked = app.Engine.Enabled;
        UpdateEnableVisual();
        ApplyFantasyMode(app.Fantasy.Enabled);

        app.Devices.ConnectionChanged += Devices_ConnectionChanged;
        app.Devices.DeviceNameChanged += (_, name) => Dispatcher.Invoke(() => SetDeviceName(name));
        app.Devices.CandidatesChanged += (_, _) => Dispatcher.Invoke(RefreshCandidates);
        LogBus.Message += (_, message) => Dispatcher.BeginInvoke(() =>
        {
            // “发现…”只是候选设备扫描日志，不代表已连接；不要让它长期占着状态栏。
            if (message.StartsWith("发现", StringComparison.Ordinal)) return;
            StatusText.Text = message;
            if (!App.Current.Devices.IsConnected &&
                (message.Contains("断开", StringComparison.Ordinal) ||
                 message.Contains("未连接", StringComparison.Ordinal)))
                StatusText.Text = "等待手柄…";
        });

        CreateTray();
        _foregroundTimer.Start();
        RefreshCandidates();
        SetDeviceName(app.Devices.ConnectedName);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x8001)
        {
            Dispatcher.BeginInvoke(ShowFromTray);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void CreateTray()
    {
        try
        {
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            _tray = new Forms.NotifyIcon
            {
                Icon = icon ?? System.Drawing.SystemIcons.Application,
                Text = "风刃手柄助手",
                Visible = true,
            };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开主界面", null, (_, _) => ShowFromTray());
            menu.Items.Add("启用 / 暂停映射", null, (_, _) => ToggleMapping());
            menu.Items.Add(new Forms.ToolStripSeparator());
            var imeMenu = new Forms.ToolStripMenuItem("手柄输入法");
            imeMenu.DropDownItems.Add("显示 / 隐藏键盘", null, (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (!App.Current.ImeRunning) App.Current.StartIme();
                    App.Current.Ime.ToggleKeyboard();
                });
            });
            imeMenu.DropDownItems.Add("输入法设置…", null, (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (!App.Current.ImeRunning) App.Current.StartIme();
                    App.Current.Ime.OpenSettings();
                });
            });
            imeMenu.DropDownItems.Add("启用 / 停用", null, (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (App.Current.ImeRunning) App.Current.StopIme();
                    else App.Current.StartIme();
                });
            });
            menu.Items.Add(imeMenu);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => ExitApp());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => ShowFromTray();
        }
        catch (Exception ex)
        {
            LogBus.Publish($"创建托盘图标失败：{ex.Message}");
        }
    }

    private void ToggleMapping()
    {
        Dispatcher.Invoke(() => EnableSwitch.IsChecked = !(EnableSwitch.IsChecked == true));
    }

    private void ShowFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            ShowInTaskbar = true;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    public void StartHidden()
    {
        ShowActivated = false;
        ShowInTaskbar = false;
        WindowState = WindowState.Minimized;
        Show();
        Hide();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        Close();
        Application.Current.Shutdown();
    }

    private void TopNav_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingNav) return;
        if (!IsLoaded || PageHost == null) return;
        if (sender is not ToggleButton top) return;
        var key = top.CommandParameter as string ?? top.Tag as string ?? "device";
        _syncingNav = true;
        NavDevice.IsChecked = key == "device";
        NavMapping.IsChecked = key == "mapping";
        NavChords.IsChecked = key == "chords";
        NavMacro.IsChecked = key == "macro";
        NavFantasy.IsChecked = key == "fantasy";
        NavIme.IsChecked = key == "ime";
        NavSettings.IsChecked = key == "settings";
        NavDiagnostics.IsChecked = key == "diagnostics";
        foreach (var other in new[] { TopNavDevice, TopNavMapping, TopNavChords, TopNavMacro, TopNavFantasy, TopNavIme, TopNavSettings, TopNavDiagnostics })
            if (other != top) other.IsChecked = false;
        _syncingNav = false;
        ShowPage(key);
    }

    private void Nav_OnClick(object sender, RoutedEventArgs e)
    {
        // ToggleButton 点击已选中的导航会自己取消选中，这里强制保持选中，避免变白。
        if (sender is ToggleButton button) button.IsChecked = true;
    }

    private void Nav_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingNav) return;
        if (!IsLoaded || PageHost == null) return;
        if (sender is not ToggleButton checkedButton) return;
        foreach (var button in new[] { NavDevice, NavMapping, NavChords, NavMacro, NavFantasy, NavIme, NavSettings, NavDiagnostics })
            if (button != checkedButton) button.IsChecked = false;

        var key = checkedButton switch
        {
            _ when checkedButton == NavMapping => "mapping",
            _ when checkedButton == NavChords => "chords",
            _ when checkedButton == NavMacro => "macro",
            _ when checkedButton == NavFantasy => "fantasy",
            _ when checkedButton == NavIme => "ime",
            _ when checkedButton == NavSettings => "settings",
            _ when checkedButton == NavDiagnostics => "diagnostics",
            _ => "device",
        };
        _syncingNav = true;
        TopNavDevice.IsChecked = key == "device";
        TopNavMapping.IsChecked = key == "mapping";
        TopNavChords.IsChecked = key == "chords";
        TopNavMacro.IsChecked = key == "macro";
        TopNavFantasy.IsChecked = key == "fantasy";
        TopNavIme.IsChecked = key == "ime";
        TopNavSettings.IsChecked = key == "settings";
        TopNavDiagnostics.IsChecked = key == "diagnostics";
        _syncingNav = false;
        ShowPage(key);
    }

    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "mapping" => new MappingPage(),
                "chords" => new ChordsPage(),
                "macro" => new MacroPage(),
                "fantasy" => new FantasyPage(),
                "ime" => new GamepadImePage(),
                "settings" => new SettingsPage(),
                "diagnostics" => new DiagnosticsPage(),
                _ => new DevicePage(),
            };
            _pages[key] = page;
        }
        PageHost.Content = page;
    }

    public void ReloadProfiles()
    {
        var current = App.Current.Store.Settings.LastProfile;
        _currentProfileName = null;
        ProfileCombo.Items.Clear();
        foreach (var profile in App.Current.Store.Profiles)
            ProfileCombo.Items.Add(profile.Name);

        ProfileCombo.SelectedItem = current;
        if (ProfileCombo.SelectedItem == null && ProfileCombo.Items.Count > 0)
            ProfileCombo.SelectedIndex = 0;
        _currentProfileName = ProfileCombo.SelectedItem as string;
        StatusProfile.Text = _currentProfileName ?? "";
    }

    public void SelectProfile(string name)
    {
        if (ProfileCombo.Items.Contains(name))
            ProfileCombo.SelectedItem = name;
    }

    private void ProfileCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {        if (ProfileCombo.SelectedItem is not string name) return;
        if (name == _currentProfileName) return;
        _currentProfileName = name;
        var profile = App.Current.Store.Get(name);
        if (profile == null) return;
        App.Current.Engine.SetProfile(profile);
        App.Current.ApplyImeProfile();
        EnableSwitch.IsChecked = profile.Settings.Enabled;
        App.Current.Engine.Enabled = profile.Settings.Enabled;
        UpdateEnableVisual();
        App.Current.Store.Settings.LastProfile = name;
        App.Current.Store.SaveSettings();
        StatusProfile.Text = name;
        if (_pages.TryGetValue("mapping", out var mapping) && mapping is MappingPage mp) mp.Reload();
        if (_pages.TryGetValue("chords", out var chords) && chords is ChordsPage cp) cp.Reload();
        if (_pages.TryGetValue("macro", out var macro) && macro is MacroPage macroPage) macroPage.Reload();
        if (_pages.TryGetValue("fantasy", out var fantasy) && fantasy is FantasyPage fantasyPage) fantasyPage.Reload();
        if (_pages.TryGetValue("settings", out var settingsPage) && settingsPage is SettingsPage sp) sp.SyncProfileSelection();
        ApplyFantasyMode(App.Current.Fantasy.Enabled);
    }

    private void EnableSwitch_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var enabled = EnableSwitch.IsChecked == true;
        App.Current.Engine.Enabled = enabled;
        App.Current.Engine.Profile.Settings.Enabled = enabled;
        App.Current.Store.Settings.Enabled = enabled;
        App.Current.Store.SaveSettings();
        if (!enabled) App.Current.Fantasy.Suspend();
        UpdateEnableVisual();
    }

    public void RefreshFantasyBaseKeys()
    {
        if (_pages.TryGetValue("fantasy", out var page) && page is FantasyPage fantasyPage)
            fantasyPage.RefreshBaseKeys();
    }

    public void ApplyFantasyMode(bool enabled)    {
        NavMapping.IsEnabled = !enabled;
        NavChords.IsEnabled = !enabled;
        // 宏库独立于映射层，幻想模式下仍可进入宏库编辑。
        NavMacro.IsEnabled = true;
        TopNavMapping.IsEnabled = !enabled;
        TopNavChords.IsEnabled = !enabled;
        TopNavMacro.IsEnabled = true;
        if (enabled && PageHost.Content is MappingPage or ChordsPage)
            ShowPage("fantasy");

        App.Current.Fantasy.ApplyFantasyModeVisibility(enabled);
        if (PageHost.Content is FantasyPage fantasyPage) fantasyPage.SyncOverlayState();
        // 手柄输入法与幻想模式互不占用：输入法页面始终可用。
    }

    private void UpdateEnableVisual()
    {
        var enabled = App.Current.Engine.Enabled;
        EnableLabel.Text = enabled ? "映射开启" : "映射暂停";
    }

    private void Devices_ConnectionChanged(object? sender, bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            var statusBrush = connected
                ? (System.Windows.Media.Brush)FindResource("Success")
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x52, 0x66));
            SideStatusDot.Fill = statusBrush;
            TopStatusDot.Fill = statusBrush;
            SideStatusText.Text = connected ? "已连接" : "未连接";
            TopStatusText.Text = connected ? "已连接" : "未连接";
            TopStatusDevice.Text = connected
                ? App.Current.Devices.ConnectedName ?? "已连接"
                : "等待手柄…";
            if (!connected)
            {
                SideDeviceText.Text = "等待手柄…";
                StatusText.Text = "等待手柄…";
            }
            if (_pages.TryGetValue("device", out var page) && page is DevicePage dp) dp.OnConnectionChanged(connected);
        });
    }

    private void SetDeviceName(string? name)
    {
        SideDeviceText.Text = string.IsNullOrWhiteSpace(name) ? "等待手柄…" : name;
        TopStatusDevice.Text = SideDeviceText.Text;
    }

    private void RefreshCandidates()
    {
        if (_pages.TryGetValue("device", out var page) && page is DevicePage dp) dp.RefreshCandidates();
    }

    private void ForegroundTimer_Tick(object? sender, EventArgs e)
    {
        if (!App.Current.Store.Settings.AutoSwitchByProcess) return;
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return;
            var process = System.Diagnostics.Process.GetProcessById((int)pid);

            // 软件自身窗口不参与自动切换，避免编辑配置时被切回默认配置
            if (process.Id == Environment.ProcessId)
                return;

            var match = App.Current.Store.Profiles.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.ProcessName) && ProcessMatches(p.ProcessName, process));
            if (match != null && match.Name != _currentProfileName)
            {
                ProfileCombo.SelectedItem = match.Name;
                return;
            }

            if (match == null && !App.Current.Store.Settings.KeepProfileWhenInactive)
            {
                var current = App.Current.Store.Get(_currentProfileName);
                if (current is { IsDefault: false } && !string.IsNullOrWhiteSpace(current.ProcessName))
                {
                    var fallback = App.Current.Store.Profiles.FirstOrDefault(p => p.IsDefault);
                    if (fallback != null && fallback.Name != _currentProfileName)
                        ProfileCombo.SelectedItem = fallback.Name;
                }
            }
        }
        catch { }
    }

    private static readonly Dictionary<string, string[]> ProcessAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft Edge"] = ["msedge"],
        ["Google Chrome"] = ["chrome"],
        ["Mozilla Firefox"] = ["firefox"],
        ["Notepad"] = ["notepad"],
        ["记事本"] = ["notepad"],
    };

    private static bool ProcessMatches(string configured, System.Diagnostics.Process process)
    {
        var want = configured.Trim().Trim('"');
        if (want.Length == 0) return false;
        if (string.Equals(want, process.ProcessName, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(want, process.ProcessName + ".exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (ProcessAliases.TryGetValue(want, out var aliases) &&
            aliases.Any(alias => string.Equals(alias, process.ProcessName, StringComparison.OrdinalIgnoreCase)))
            return true;

        try
        {
            var info = process.MainModule?.FileVersionInfo;
            if (info == null) return false;
            foreach (var candidate in new[] { info.FileDescription, info.ProductName, info.FileName })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (string.Equals(want, candidate, StringComparison.OrdinalIgnoreCase)) return true;
                if (candidate.Contains(want, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    private void UpdateThemeIcon()
    {
        MoonIcon.Visibility = ThemeManager.IsLight ? Visibility.Collapsed : Visibility.Visible;
        SunIcon.Visibility = ThemeManager.IsLight ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Theme_OnClick(object sender, RoutedEventArgs e)
    {
        var light = !ThemeManager.IsLight;
        ThemeManager.Apply(light);
        UpdateThemeIcon();
        App.Current.Store.Settings.LightTheme = light;
        App.Current.Store.SaveSettings();
    }

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_OnClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnWindowStateChanged()
    {
        if (_handlingMaximize) return;
        if (WindowState != WindowState.Maximized) return;
        // 自定义标题栏 + WindowChrome 在系统最大化时会盖住任务栏，改为按工作区手动最大化。
        _handlingMaximize = true;
        WindowState = WindowState.Normal;
        ToggleMaximize();
        _handlingMaximize = false;
    }

    private void ToggleMaximize()
    {
        if (_customMaximized)
        {
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            _customMaximized = false;
            return;
        }

        _restoreBounds = new Rect(Left, Top, Width, Height);
        var work = SystemParameters.WorkArea;
        Left = work.Left;
        Top = work.Top;
        Width = work.Width;
        Height = work.Height;
        _customMaximized = true;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyExit) return;
        if (App.Current.Store.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _reallyExit = true;
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray?.Dispose();
        _foregroundTimer.Stop();
        base.OnClosed(e);
    }
}



