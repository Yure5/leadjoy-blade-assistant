using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LeadjoyBlade.Binput;
using LeadjoyBlade.Core.Mapping;
using BinputConfig = LeadjoyBlade.Binput.Config;

namespace LeadjoyBlade.App.Views;

/// <summary>
/// 工作台 → 手柄输入法。
/// 这里只做“控制台 + 状态”，真正的悬浮键盘 / 设置页由 BinputHost 里的 OverlayWindow 负责
/// （它必须是一个独立置顶窗口，才能盖在游戏上，也才能像原版一样被手柄唤出）。
/// </summary>
public partial class GamepadImePage : UserControl
{
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _scaleSaveTimer;
    private bool _syncing;
    private bool _loaded;
    private bool _scaleDirty;
    private bool _opacityDirty;

    public GamepadImePage()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => RefreshState();
        _scaleSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _scaleSaveTimer.Tick += (_, _) => SaveScaleNow();
        Loaded += (_, _) =>
        {
            _loaded = true;
            InitWakeCombo();
            RefreshState();
            _timer.Start();
            if (Host != null) Host.OverlayLayoutChanged += OnOverlayLayoutChanged;
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            _timer.Stop();
            _scaleSaveTimer.Stop();
            SaveScaleNow();
            if (Host != null) Host.OverlayLayoutChanged -= OnOverlayLayoutChanged;
        };
    }

    private BinputHost Host => App.Current.Ime;
    private static ImeProfileSettings ImeSettings => App.Current.Engine.Profile.Ime;

    private void InitWakeCombo()
    {
        if (WakeCombo.Items.Count > 0) return;
        AddWake("", "L3 + R3（默认）");
        AddWake("pad:Back", "Back");
        AddWake("pad:Start", "Start");
        AddWake("pad:Back+Start", "Back + Start");
        AddWake("pad:L3", "L3");
        AddWake("pad:R3", "R3");
        for (int i = 1; i <= 12; i++) AddWake($"kb:f{i}", $"F{i}");
    }

    private void AddWake(string spec, string label)
        => WakeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = spec });

    private static string WakeLabel(string? spec)
    {
        if (string.IsNullOrEmpty(spec)) return "L3 + R3";
        if (spec.StartsWith("pad:", StringComparison.OrdinalIgnoreCase))
        {
            var body = spec[4..];
            return string.Join(" + ", body.Split('+', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => char.ToUpperInvariant(t[0]) + t[1..]));
        }
        if (spec.StartsWith("kb:", StringComparison.OrdinalIgnoreCase))
        {
            var body = spec[3..];
            return body.Length > 0 ? char.ToUpperInvariant(body[0]) + body[1..] : spec;
        }
        return spec;
    }

    private static string CurrentWakeSpec() => ImeSettings.WakeSpec ?? "";

    private void RefreshState()
    {
        if (!_loaded) return;
        var host = Host;
        bool running = host?.Running == true;
        bool visible = host?.Visible == true;

        _syncing = true;

        EnableSwitch.IsChecked = ImeSettings.Enabled;
        EnableLabel.Text = ImeSettings.Enabled ? "已启用" : "启用";
        StatusDot.Fill = running
            ? (Brush)FindResource("Success")
            : new SolidColorBrush(Color.FromRgb(0x4A, 0x52, 0x66));
        StatusText.Text = !running ? "未启用"
            : visible ? "运行中 · 键盘已唤出"
            : "运行中 · 键盘已收起";
        ProfileText.Text = $"当前配置：{App.Current.Engine.Profile.Name}";

        PadSourceText.Text = running
            ? (string.IsNullOrWhiteSpace(host!.PadSource) ? "未检测到手柄" : host.PadSource)
            : "—";
        LayoutText.Text = running ? host!.LayoutName : "—";
        ModeText.Text = running
            ? host!.ModeName
            : (ImeSettings.Preview ? "预输入版" : "直出版");
        WakeText.Text = running ? host!.WakeKeysText : WakeLabel(ImeSettings.WakeSpec);

        ModeCombo.SelectedIndex = (running ? host!.Preview : ImeSettings.Preview) ? 0 : 1;
        var spec = CurrentWakeSpec();
        foreach (var item in WakeCombo.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && string.Equals(tag, spec, StringComparison.OrdinalIgnoreCase))
            {
                WakeCombo.SelectedItem = item;
                break;
            }
        }

        var scale = Math.Clamp(running ? host!.OverlayScale : BinputConfig.LoadAll().Scale, 0.6, 1.8);
        if (!ScaleSlider.IsMouseCaptureWithin)
            ScaleSlider.Value = Math.Round(scale * 100);
        ScaleText.Text = $"{Math.Round(scale * 100):0}%";

        var opacity = Math.Clamp(running ? host!.OverlayOpacity : BinputConfig.LoadOverlayOpacity(), 0.2, 1.0);
        if (!OpacitySlider.IsMouseCaptureWithin)
            OpacitySlider.Value = Math.Round(opacity * 100);
        OpacityText.Text = $"{Math.Round(opacity * 100):0}%";

        var conflicts = ImeWakeConflicts.Find(App.Current.Engine.Profile);
        WakeConflictText.Text = conflicts.Count == 0
            ? ""
            : "⚠ 唤醒键冲突：" + string.Join("；", conflicts);
        WakeConflictText.Visibility = conflicts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        HideKeyboardButton.IsEnabled = running && visible;
        OpenSettingsButton.IsEnabled = true;
        AdjustWindowButton.IsEnabled = true;

        DataPathText.Text = $"数据目录：{BinputConfig.Dir}";
        var dictFile = Path.Combine(AppContext.BaseDirectory, "dict.txt");
        DictionaryText.Text = File.Exists(dictFile)
            ? $"词库：程序目录 dict.txt（{new FileInfo(dictFile).Length / 1024} KB，{T9Engine.PhraseCount} 条拼音）"
            : $"词库：已内嵌（{T9Engine.PhraseCount} 条拼音，{T9Engine.HotCount} 个音节热度）";

        _syncing = false;
    }

    private void EnsureRunning()
    {
        if (!App.Current.ImeRunning) App.Current.StartIme();
    }

    private void EnableSwitch_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || !_loaded) return;
        App.Current.SetImeEnabled(EnableSwitch.IsChecked == true);
        RefreshState();
    }

    private void ScaleSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !_loaded) return;
        var scale = Math.Clamp(ScaleSlider.Value / 100.0, 0.6, 1.8);
        ScaleText.Text = $"{ScaleSlider.Value:0}%";
        Host?.SetOverlayScale(scale);
        _scaleDirty = true;
        _scaleSaveTimer.Stop();
        _scaleSaveTimer.Start();
    }

    private void OpacitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !_loaded) return;
        var opacity = Math.Clamp(OpacitySlider.Value / 100.0, 0.2, 1.0);
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
        Host?.SetOverlayOpacity(opacity);
        _opacityDirty = true;
        _scaleSaveTimer.Stop();
        _scaleSaveTimer.Start();
    }

    private void SaveScaleNow()
    {
        _scaleSaveTimer.Stop();
        if (!_scaleDirty && !_opacityDirty) return;
        _scaleDirty = false;
        _opacityDirty = false;
        if (Host != null)
        {
            Host.SaveOverlayLayout();
        }
        else
        {
            var saved = BinputConfig.LoadAll();
            BinputConfig.SaveAll(Math.Clamp(ScaleSlider.Value / 100.0, 0.6, 1.8), saved.Ox, saved.Oy);
            BinputConfig.SaveOverlayOpacity(Math.Clamp(OpacitySlider.Value / 100.0, 0.2, 1.0));
        }
    }

    private void OnOverlayLayoutChanged(object? sender, EventArgs e)
    {
        if (!_loaded) return;
        var scale = Math.Clamp(Host?.OverlayScale ?? BinputConfig.LoadAll().Scale, 0.6, 1.8);
        var opacity = Math.Clamp(Host?.OverlayOpacity ?? BinputConfig.LoadOverlayOpacity(), 0.2, 1.0);
        _syncing = true;
        if (!ScaleSlider.IsMouseCaptureWithin)
            ScaleSlider.Value = Math.Round(scale * 100);
        if (!OpacitySlider.IsMouseCaptureWithin)
            OpacitySlider.Value = Math.Round(opacity * 100);
        ScaleText.Text = $"{Math.Round(scale * 100):0}%";
        OpacityText.Text = $"{Math.Round(opacity * 100):0}%";
        _syncing = false;
    }

    private void ModeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !_loaded) return;
        bool preview = ModeCombo.SelectedIndex == 0;
        var ime = ImeSettings;
        ime.Preview = preview;
        ime.Configured = true;
        App.Current.Store.Save(App.Current.Engine.Profile);
        if (App.Current.ImeRunning) App.Current.Ime.Preview = preview;
        RefreshState();
    }

    private void WakeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !_loaded) return;
        var spec = (WakeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var ime = ImeSettings;
        ime.WakeSpec = spec;
        ime.Configured = true;
        App.Current.Store.Save(App.Current.Engine.Profile);
        if (App.Current.ImeRunning) App.Current.Ime.SetWake(spec);
        App.Current.RefreshFantasyBaseKeys();
        RefreshState();
    }

    private void ShowKeyboard_OnClick(object sender, RoutedEventArgs e)
    {
        EnsureRunning();
        Host?.ShowKeyboard();
        RefreshState();
    }

    private void HideKeyboard_OnClick(object sender, RoutedEventArgs e)
    {
        Host?.HideKeyboard();
        RefreshState();
    }

    private void OpenSettings_OnClick(object sender, RoutedEventArgs e)
    {
        EnsureRunning();
        Host?.OpenSettings();
        RefreshState();
    }

    private void AdjustWindow_OnClick(object sender, RoutedEventArgs e)
    {
        EnsureRunning();
        Host?.ShowKeyboard();
        Host?.AdjustWindow();
        RefreshState();
    }

    private void OpenDataDir_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(BinputConfig.Dir);
            Process.Start(new ProcessStartInfo(BinputConfig.Dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打不开数据目录：" + ex.Message, "手柄输入法",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenLog_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(BinputConfig.Dir);
            if (!File.Exists(BinputConfig.LogFile))
                File.WriteAllText(BinputConfig.LogFile, "(还没有日志)\r\n");
            Process.Start(new ProcessStartInfo(BinputConfig.LogFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打不开日志：" + ex.Message, "手柄输入法",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void About_OnClick(object sender, RoutedEventArgs e)
    {
        var text = Host?.About()
            ?? "柄输 Binput 2.5.0 · 莫古雨 · https://github.com/moguyu-binput/Binput";
        MessageBox.Show(text, "关于手柄输入法", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
