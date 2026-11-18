using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LeadjoyBlade.App.Input;
using LeadjoyBlade.Core.Input;
using LeadjoyBlade.Core.Mapping;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.App.Dialogs;

public partial class SystemMenuMacroStepDialog : Window
{
    private readonly SystemMenuStepKind _kind;
    private int _key = 0x20;
    private bool _capturingKey;
    private bool _mouseScreen;
    private string? _mouseMonitor;
    private int _mouseMonitorX;
    private int _mouseMonitorY;
    private int _mouseMonitorWidth;
    private int _mouseMonitorHeight;
    private bool _capturingGamepad;
    private readonly HashSet<BladeButtons> _capturedGamepad = [];
    private DispatcherTimer? _gamepadCaptureTimer;

    private sealed class ButtonChoice
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public override string ToString() => Name;
    }

    public SystemMenuMacroStep Result { get; private set; } = new();

    public SystemMenuMacroStepDialog(SystemMenuStepKind kind, SystemMenuMacroStep? step)
    {
        _kind = kind;
        InitializeComponent();
        TitleText.Text = kind switch
        {
            SystemMenuStepKind.Key => "宏步骤 · 键盘按键",
            SystemMenuStepKind.Gamepad => "宏步骤 · 手柄按键",
            SystemMenuStepKind.MouseClick => "宏步骤 · 鼠标点击",
            SystemMenuStepKind.Text => "宏步骤 · 文本输入",
            _ => "宏步骤 · 延迟",
        };

        foreach (var button in BladeButtonsExtensions.All)
        {
            GamepadList.Items.Add(new ButtonChoice
            {
                Id = button.ToString(),
                Name = button.ToShortName(),
            });
        }

        var source = step?.Clone() ?? new SystemMenuMacroStep { Kind = kind };
        _key = source.Key;
        KeyText.Text = KeyCatalog.GetName(_key);
        CtrlCheck.IsChecked = source.Modifiers.Contains(KeyCatalog.VkControl);
        AltCheck.IsChecked = source.Modifiers.Contains(KeyCatalog.VkAlt);
        ShiftCheck.IsChecked = source.Modifiers.Contains(KeyCatalog.VkShift);
        WinCheck.IsChecked = source.Modifiers.Contains(KeyCatalog.VkLWin);
        MouseXBox.Text = (source.MouseX * 100).ToString("0.#");
        MouseYBox.Text = (source.MouseY * 100).ToString("0.#");
        _mouseScreen = source.MouseScreen;
        _mouseMonitor = source.MouseMonitor;
        _mouseMonitorX = source.MouseMonitorX;
        _mouseMonitorY = source.MouseMonitorY;
        _mouseMonitorWidth = source.MouseMonitorWidth;
        _mouseMonitorHeight = source.MouseMonitorHeight;
        HoldBox.Text = source.HoldMs.ToString();
        DelayBox.Text = source.DelayMs.ToString();
        ActionTextBox.Text = source.Text ?? "";
        SelectComboByTag(MouseButtonCombo, source.MouseButton.ToString());
        SelectGamepad(source.EffectiveGamepadButtons);

        KeyPanel.Visibility = kind == SystemMenuStepKind.Key ? Visibility.Visible : Visibility.Collapsed;
        GamepadPanel.Visibility = kind == SystemMenuStepKind.Gamepad ? Visibility.Visible : Visibility.Collapsed;
        MousePanel.Visibility = kind == SystemMenuStepKind.MouseClick ? Visibility.Visible : Visibility.Collapsed;
        TextPanel.Visibility = kind == SystemMenuStepKind.Text ? Visibility.Visible : Visibility.Collapsed;
        DelayPanel.Visibility = kind == SystemMenuStepKind.Delay ? Visibility.Visible : Visibility.Collapsed;
        HoldBox.IsEnabled = kind != SystemMenuStepKind.Delay;
        Unloaded += (_, _) => StopGamepadCapture();
    }

    private void SelectGamepad(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        GamepadList.SelectedItems.Clear();
        foreach (var item in GamepadList.Items.OfType<ButtonChoice>())
            if (selected.Contains(item.Id)) GamepadList.SelectedItems.Add(item);
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag?.ToString() == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        _ = int.TryParse(HoldBox.Text, out var hold);
        _ = int.TryParse(DelayBox.Text, out var delay);
        var result = new SystemMenuMacroStep
        {
            Kind = _kind,
            Key = _key,
            Modifiers = CurrentModifiers(),
            MouseX = ReadPercent(MouseXBox.Text),
            MouseY = ReadPercent(MouseYBox.Text),
            MouseScreen = _mouseScreen,
            MouseMonitor = _mouseScreen ? _mouseMonitor : null,
            MouseMonitorX = _mouseMonitorX,
            MouseMonitorY = _mouseMonitorY,
            MouseMonitorWidth = _mouseMonitorWidth,
            MouseMonitorHeight = _mouseMonitorHeight,
            HoldMs = Math.Clamp(hold, 5, 5000),
            DelayMs = Math.Clamp(delay, 0, 10000),
            Text = ActionTextBox.Text,
        };
        if (MouseButtonCombo.SelectedItem is ComboBoxItem mouseItem
            && Enum.TryParse<MouseButtonKind>(mouseItem.Tag?.ToString(), out var mouseButton))
            result.MouseButton = mouseButton;
        if (GamepadList.SelectedItems.Count > 0)
        {
            result.GamepadButtons = GamepadList.SelectedItems
                .OfType<ButtonChoice>()
                .Select(x => x.Id)
                .ToList();
            result.GamepadButton = result.GamepadButtons[0];
        }
        Result = result;
        DialogResult = true;
        Close();
    }

    private List<int> CurrentModifiers()
    {
        var modifiers = new List<int>();
        if (CtrlCheck.IsChecked == true) modifiers.Add(KeyCatalog.VkControl);
        if (AltCheck.IsChecked == true) modifiers.Add(KeyCatalog.VkAlt);
        if (ShiftCheck.IsChecked == true) modifiers.Add(KeyCatalog.VkShift);
        if (WinCheck.IsChecked == true) modifiers.Add(KeyCatalog.VkLWin);
        return modifiers;
    }

    private static double ReadPercent(string text)
    {
        if (!double.TryParse(text.Trim().TrimEnd('%'), out var value)) return 0.5;
        return Math.Clamp(value / 100.0, 0, 1);
    }

    private void EndCapture()
    {
        _capturingKey = false;
        InputMethod.SetIsInputMethodEnabled(this, true);
    }

    private void CaptureKey_OnClick(object sender, RoutedEventArgs e)
    {
        _capturingKey = true;
        InputMethod.SetIsInputMethodEnabled(this, false);
        KeyText.Text = "请按下键盘按键…";
        Focus();
        Keyboard.Focus(this);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingKey) return;
        var key = KeyCapture.Resolve(e);
        var modifierKey = key switch
        {
            Key.LeftCtrl or Key.RightCtrl => KeyCatalog.VkControl,
            Key.LeftAlt or Key.RightAlt => KeyCatalog.VkAlt,
            Key.LeftShift or Key.RightShift => KeyCatalog.VkShift,
            Key.LWin or Key.RWin => KeyCatalog.VkLWin,
            _ => 0,
        };
        if (modifierKey != 0)
        {
            _key = modifierKey;
            KeyText.Text = KeyCatalog.GetName(modifierKey);
            CtrlCheck.IsChecked = modifierKey == KeyCatalog.VkControl;
            AltCheck.IsChecked = modifierKey == KeyCatalog.VkAlt;
            ShiftCheck.IsChecked = modifierKey == KeyCatalog.VkShift;
            WinCheck.IsChecked = modifierKey == KeyCatalog.VkLWin;
            EndCapture();
            e.Handled = true;
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        _key = vk;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) CtrlCheck.IsChecked = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) AltCheck.IsChecked = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ShiftCheck.IsChecked = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) WinCheck.IsChecked = true;
        KeyText.Text = KeyCatalog.GetName(vk);
        EndCapture();
        e.Handled = true;
    }

    private void PickMousePosition_OnClick(object sender, RoutedEventArgs e)
    {
        var result = MousePointCapture.Capture(this, _mouseScreen,
            ReadPercent(MouseXBox.Text), ReadPercent(MouseYBox.Text), _mouseMonitor);
        if (result == null) return;

        MouseXBox.Text = (result.X * 100).ToString("0.#");
        MouseYBox.Text = (result.Y * 100).ToString("0.#");
        _mouseScreen = result.Screen;
        _mouseMonitor = result.Monitor;
        _mouseMonitorX = result.MonitorX;
        _mouseMonitorY = result.MonitorY;
        _mouseMonitorWidth = result.MonitorWidth;
        _mouseMonitorHeight = result.MonitorHeight;
    }

    private void CaptureGamepad_OnClick(object sender, RoutedEventArgs e)
    {
        if (_capturingGamepad)
        {
            StopGamepadCapture();
            return;
        }
        _capturingGamepad = true;
        _capturedGamepad.Clear();
        GamepadList.SelectedItems.Clear();
        CaptureGamepadButton.Content = "请按手柄…";
        App.Current.Devices.ReportReceived += Devices_OnGamepadCapture;
        _gamepadCaptureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _gamepadCaptureTimer.Tick += (_, _) => StopGamepadCapture();
    }

    private void Devices_OnGamepadCapture(object? sender, BladeReport report)
    {
        if (!_capturingGamepad) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_capturingGamepad) return;
            foreach (var button in BladeButtonsExtensions.All)
            {
                if (report.IsPressed(button)) _capturedGamepad.Add(button);
            }
            if (_capturedGamepad.Count == 0) return;
            GamepadList.SelectedItems.Clear();
            foreach (var item in GamepadList.Items.OfType<ButtonChoice>())
            {
                if (Enum.TryParse<BladeButtons>(item.Id, true, out var button) &&
                    _capturedGamepad.Contains(button))
                    GamepadList.SelectedItems.Add(item);
            }
            if (_gamepadCaptureTimer is { IsEnabled: false }) _gamepadCaptureTimer.Start();
        }));
    }

    private void StopGamepadCapture()
    {
        if (!_capturingGamepad) return;
        _capturingGamepad = false;
        App.Current.Devices.ReportReceived -= Devices_OnGamepadCapture;
        _gamepadCaptureTimer?.Stop();
        _gamepadCaptureTimer = null;
        if (CaptureGamepadButton != null) CaptureGamepadButton.Content = "捕获按键";
    }

    private void Title_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

