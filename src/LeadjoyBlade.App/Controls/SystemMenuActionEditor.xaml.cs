using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LeadjoyBlade.App.Dialogs;
using LeadjoyBlade.App.Input;
using LeadjoyBlade.Core.Input;
using LeadjoyBlade.Core.Mapping;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.App.Controls;

public partial class SystemMenuActionEditor : UserControl
{
    private readonly ObservableCollection<StepRow> _stepRows = [];
    private SystemMenuActionKind _kind = SystemMenuActionKind.Key;
    private int _key = 0x20;
    private bool _capturingKey;
    private bool _loading;
    private bool _mouseScreen;
    private string? _mouseMonitor;
    private int _mouseMonitorX;
    private int _mouseMonitorY;
    private int _mouseMonitorWidth;
    private int _mouseMonitorHeight;
    private bool _capturingGamepad;
    private readonly HashSet<BladeButtons> _capturedGamepad = [];
    private DispatcherTimer? _gamepadCaptureTimer;

    private sealed class GamepadChoice
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public override string ToString() => Name;
    }

    private sealed class StepRow
    {
        public required SystemMenuMacroStep Step { get; init; }
        public int Index { get; set; }
        public string Summary => Step.Describe();
        public string KindText => Step.Kind switch
        {
            SystemMenuStepKind.Key => "键盘",
            SystemMenuStepKind.Gamepad => "手柄",
            SystemMenuStepKind.MouseClick => "鼠标",
            SystemMenuStepKind.Text => "文本",
            SystemMenuStepKind.Delay => "延迟",
            _ => "步骤",
        };
        public string KeyText => Step.Kind switch
        {
            SystemMenuStepKind.Key => Step.Modifiers.Count == 0
                ? KeyCatalog.GetName(Step.Key)
                : string.Join("+", Step.Modifiers.Select(KeyCatalog.GetName)) + "+" + KeyCatalog.GetName(Step.Key),
            SystemMenuStepKind.Gamepad => string.Join(" + ",
                Step.EffectiveGamepadButtons.Select(MappingSources.GetDisplayName)),
            SystemMenuStepKind.MouseClick =>
                $"{SystemMenuAction.MouseButtonText(Step.MouseButton)} {Step.MouseX * 100:0.#}%/{Step.MouseY * 100:0.#}%（{(Step.MouseScreen ? "屏幕" : "窗口")}）",
            SystemMenuStepKind.Text => Step.Text,
            SystemMenuStepKind.Delay => "等待",
            _ => Step.Describe(),
        };
        public bool ShowKeyBox => Step.Kind is SystemMenuStepKind.Key or SystemMenuStepKind.Gamepad
            or SystemMenuStepKind.MouseClick;
        public bool ShowText => Step.Kind == SystemMenuStepKind.Text;
        public bool ShowDelay => Step.Kind == SystemMenuStepKind.Delay;
        public bool ShowHold => ShowKeyBox;
    }

    public SystemMenuActionEditor()
    {
        _loading = true;
        InitializeComponent();
        StepList.ItemsSource = _stepRows;
        foreach (var button in BladeButtonsExtensions.All)
        {
            GamepadList.Items.Add(new GamepadChoice
            {
                Id = button.ToString(),
                Name = button.ToShortName(),
            });
        }
        _loading = false;
        SelectTypeItem(SystemMenuActionKind.Key);
        UpdatePanels();
        Unloaded += (_, _) => StopGamepadCapture();
    }

    public void LoadAction(SystemMenuAction action)
    {
        _loading = true;
        _kind = action.Kind;
        _key = action.Key;
        KeyText.Text = KeyCatalog.GetName(_key);
        CtrlCheck.IsChecked = action.Modifiers.Contains(KeyCatalog.VkControl);
        AltCheck.IsChecked = action.Modifiers.Contains(KeyCatalog.VkAlt);
        ShiftCheck.IsChecked = action.Modifiers.Contains(KeyCatalog.VkShift);
        WinCheck.IsChecked = action.Modifiers.Contains(KeyCatalog.VkLWin);
        ActionTextBox.Text = action.Text ?? "";
        MouseXBox.Text = (action.MouseX * 100).ToString("0.#");
        MouseYBox.Text = (action.MouseY * 100).ToString("0.#");
        _mouseScreen = action.MouseScreen;
        _mouseMonitor = action.MouseMonitor;
        _mouseMonitorX = action.MouseMonitorX;
        _mouseMonitorY = action.MouseMonitorY;
        _mouseMonitorWidth = action.MouseMonitorWidth;
        _mouseMonitorHeight = action.MouseMonitorHeight;
        SelectComboByTag(MouseButtonCombo, action.MouseButton.ToString());

        GamepadList.SelectedItems.Clear();
        var selected = action.GamepadButtons.ToHashSet(StringComparer.Ordinal);
        foreach (var item in GamepadList.Items.OfType<GamepadChoice>())
            if (selected.Contains(item.Id)) GamepadList.SelectedItems.Add(item);

        _stepRows.Clear();
        foreach (var step in action.Steps) _stepRows.Add(new StepRow { Step = step.Clone() });
        RenumberRows();

        SelectTypeItem(_kind);
        UpdatePanels();
        _loading = false;
    }

    public SystemMenuAction BuildAction()
    {
        var action = new SystemMenuAction
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
            Steps = _stepRows.Select(r =>
            {
                var step = r.Step.Clone();
                step.HoldMs = Math.Clamp(step.HoldMs, 5, 5000);
                step.DelayMs = Math.Clamp(step.DelayMs, 0, 10000);
                return step;
            }).ToList(),
            Text = ActionTextBox.Text,
        };
        if (MouseButtonCombo.SelectedItem is ComboBoxItem item
            && Enum.TryParse<MouseButtonKind>(item.Tag?.ToString(), out var mouseButton))
            action.MouseButton = mouseButton;
        action.GamepadButtons = GamepadList.SelectedItems.OfType<GamepadChoice>().Select(x => x.Id).ToList();
        return action;
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

    private void SelectTypeItem(SystemMenuActionKind kind)
    {
        foreach (ComboBoxItem item in TypeCombo.Items)
        {
            if (item.Tag?.ToString() == kind.ToString())
            {
                TypeCombo.SelectedItem = item;
                return;
            }
        }
        TypeCombo.SelectedIndex = 0;
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

    private void TypeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (TypeCombo.SelectedItem is ComboBoxItem item
            && Enum.TryParse<SystemMenuActionKind>(item.Tag?.ToString(), out var kind))
            _kind = kind;
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        if (KeyPanel == null) return;
        if (_kind != SystemMenuActionKind.Gamepad) StopGamepadCapture();
        KeyPanel.Visibility = _kind == SystemMenuActionKind.Key ? Visibility.Visible : Visibility.Collapsed;
        GamepadPanel.Visibility = _kind == SystemMenuActionKind.Gamepad ? Visibility.Visible : Visibility.Collapsed;
        MousePanel.Visibility = _kind == SystemMenuActionKind.MouseClick ? Visibility.Visible : Visibility.Collapsed;
        TextPanel.Visibility = _kind == SystemMenuActionKind.Text ? Visibility.Visible : Visibility.Collapsed;
        MacroPanel.Visibility = _kind == SystemMenuActionKind.Macro ? Visibility.Visible : Visibility.Collapsed;
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
        var owner = Window.GetWindow(this);
        var result = MousePointCapture.Capture(owner, _mouseScreen,
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

    private void StepList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private void StepList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (StepList.SelectedItem is StepRow) EditSelectedStep();
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

    private void Devices_OnGamepadCapture(object? sender, LeadjoyBlade.Core.Protocol.BladeReport report)
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
            foreach (var item in GamepadList.Items.OfType<GamepadChoice>())
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

    private void AddKeyStep_OnClick(object sender, RoutedEventArgs e) =>
        AddStep(SystemMenuStepKind.Key, new SystemMenuMacroStep { Kind = SystemMenuStepKind.Key, Key = 0x20 });

    private void AddGamepadStep_OnClick(object sender, RoutedEventArgs e) =>
        AddStep(SystemMenuStepKind.Gamepad, new SystemMenuMacroStep { Kind = SystemMenuStepKind.Gamepad, GamepadButton = "A" });

    private void AddMouseStep_OnClick(object sender, RoutedEventArgs e) =>
        AddStep(SystemMenuStepKind.MouseClick, new SystemMenuMacroStep { Kind = SystemMenuStepKind.MouseClick });

    private void AddTextStep_OnClick(object sender, RoutedEventArgs e) =>
        AddStep(SystemMenuStepKind.Text, new SystemMenuMacroStep { Kind = SystemMenuStepKind.Text, Text = "", DelayMs = 80 });

    private void AddDelayStep_OnClick(object sender, RoutedEventArgs e) =>
        AddStep(SystemMenuStepKind.Delay, new SystemMenuMacroStep { Kind = SystemMenuStepKind.Delay, DelayMs = 100 });

    private void AddStep(SystemMenuStepKind kind, SystemMenuMacroStep template)
    {
        var dialog = new SystemMenuMacroStepDialog(kind, template) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        _stepRows.Add(new StepRow { Step = dialog.Result });
        RenumberRows();
        StepList.SelectedIndex = _stepRows.Count - 1;
        StepList.ScrollIntoView(_stepRows[^1]);
    }

    private void RenumberRows()
    {
        for (var i = 0; i < _stepRows.Count; i++) _stepRows[i].Index = i + 1;
        StepList.Items.Refresh();
    }

    private void EditStep_OnClick(object sender, RoutedEventArgs e) => EditSelectedStep();

    private void EditSelectedStep()
    {
        if (StepList.SelectedItem is not StepRow row) return;
        var dialog = new SystemMenuMacroStepDialog(row.Step.Kind, row.Step) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        var index = _stepRows.IndexOf(row);
        if (index < 0) return;
        _stepRows[index] = new StepRow { Step = dialog.Result };
        RenumberRows();
        StepList.SelectedIndex = index;
        StepList.Items.Refresh();
    }

    private void DeleteStep_OnClick(object sender, RoutedEventArgs e)
    {
        var index = StepList.SelectedIndex;
        if (index < 0 || index >= _stepRows.Count) return;
        _stepRows.RemoveAt(index);
        RenumberRows();
        if (_stepRows.Count > 0) StepList.SelectedIndex = Math.Min(index, _stepRows.Count - 1);
    }

    private void MoveStepUp_OnClick(object sender, RoutedEventArgs e) => MoveStep(-1);

    private void MoveStepDown_OnClick(object sender, RoutedEventArgs e) => MoveStep(1);

    private void MoveStep(int delta)
    {
        var index = StepList.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _stepRows.Count) return;
        (_stepRows[index], _stepRows[target]) = (_stepRows[target], _stepRows[index]);
        RenumberRows();
        StepList.SelectedIndex = target;
        StepList.Items.Refresh();
    }
}






