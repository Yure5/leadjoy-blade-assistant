using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LeadjoyBlade.App.Dialogs;
using LeadjoyBlade.App.Input;
using LeadjoyBlade.Core.Input;
using LeadjoyBlade.Core.Mapping;

namespace LeadjoyBlade.App.Controls;

public partial class ActionEditor : UserControl
{
    private readonly ObservableCollection<MacroStepRow> _macroRows = [];
    private ActionType _type = ActionType.Key;
    private int _key = 0x20;
    private bool _capturingKey;
    private bool _capturingMacroKey;
    private bool _loadingAction;
    private bool _loadingMacros;
    private bool _mouseUsePosition;
    private double _mouseX = 0.5;
    private double _mouseY = 0.5;
    private bool _mouseScreen;
    private string? _mouseMonitor;
    private int _mouseMonitorX;
    private int _mouseMonitorY;
    private int _mouseMonitorWidth;
    private int _mouseMonitorHeight;

    private sealed class MacroChoice
    {
        public required MacroDefinition Macro { get; init; }
        public string Name => Macro.Name;
        public string StepsText => Macro.StepsText;
        public string Display => Macro.Name;
        public override string ToString() => Display;
    }

    public ActionEditor()
    {
        InitializeComponent();
        MacroList.ItemsSource = _macroRows;
        TypeCombo.SelectedIndex = 0;
    }

    public void LoadAction(BindingAction action)
    {
        _loadingAction = true;
        _type = action.Type;
        _key = action.Key;
        KeyText.Text = KeyCatalog.GetName(_key);
        TextBox.Text = action.Text;
        CtrlCheck.IsChecked = action.Modifiers.Contains(KeyCatalog.VkControl);
        AltCheck.IsChecked = action.Modifiers.Contains(KeyCatalog.VkAlt);
        ShiftCheck.IsChecked = action.Modifiers.Contains(KeyCatalog.VkShift);
        WinCheck.IsChecked = action.Modifiers.Contains(KeyCatalog.VkLWin);
        TurboCheck.IsChecked = action.Turbo;
        ToggleCheck.IsChecked = action.Toggle;
        TurboIntervalBox.Text = action.TurboIntervalMs.ToString();

        SelectTypeItem(action.Type);
        SelectComboByTag(MouseButtonCombo, action.MouseButton.ToString());
        SelectComboByTag(WheelCombo, action.WheelDelta >= 0 ? "120" : "-120");
        _mouseUsePosition = action.MouseUsePosition;
        _mouseX = action.MouseX;
        _mouseY = action.MouseY;
        _mouseScreen = action.MouseScreen;
        _mouseMonitor = action.MouseMonitor;
        _mouseMonitorX = action.MouseMonitorX;
        _mouseMonitorY = action.MouseMonitorY;
        _mouseMonitorWidth = action.MouseMonitorWidth;
        _mouseMonitorHeight = action.MouseMonitorHeight;
        UpdateMousePositionUi();

        _macroRows.Clear();
        foreach (var step in action.Macro) _macroRows.Add(new MacroStepRow(step));
        MacroList.Items.Refresh();
        RefreshMacroChoices(action.MacroId, action.MacroName);
        UpdatePanels();
        _loadingAction = false;
    }

    public BindingAction BuildAction()
    {
        var modifiers = new List<int>();
        if (CtrlCheck.IsChecked == true) modifiers.Add(KeyCatalog.VkControl);
        if (AltCheck.IsChecked == true) modifiers.Add(KeyCatalog.VkAlt);
        if (ShiftCheck.IsChecked == true) modifiers.Add(KeyCatalog.VkShift);
        if (WinCheck.IsChecked == true) modifiers.Add(KeyCatalog.VkLWin);

        _ = int.TryParse(TurboIntervalBox.Text, out var interval);
        var macroChoice = MacroSelectCombo.SelectedItem as MacroChoice;
        var action = new BindingAction
        {
            Type = _type,
            Key = _key,
            Text = TextBox.Text,
            Modifiers = modifiers,
            Turbo = TurboCheck.IsChecked == true,
            Toggle = ToggleCheck.IsChecked == true,
            TurboIntervalMs = Math.Clamp(interval, 15, 2000),
            Macro = macroChoice != null
                ? macroChoice.Macro.Steps.Select(CloneMacroStep).ToList()
                : _macroRows.Select(r => CloneMacroStep(r.Step)).ToList(),
            MacroId = macroChoice?.Macro.Id ?? "",
            MacroName = macroChoice?.Macro.Name ?? "",
            MacroSource = "",
            MouseUsePosition = _mouseUsePosition,
            MouseX = _mouseX,
            MouseY = _mouseY,
            MouseScreen = _mouseScreen,
            MouseMonitor = _mouseScreen ? _mouseMonitor : null,
            MouseMonitorX = _mouseMonitorX,
            MouseMonitorY = _mouseMonitorY,
            MouseMonitorWidth = _mouseMonitorWidth,
            MouseMonitorHeight = _mouseMonitorHeight,
        };

        if (MouseButtonCombo.SelectedItem is ComboBoxItem mouseItem &&
            Enum.TryParse<MouseButtonKind>(mouseItem.Tag?.ToString(), out var mouseButton))
            action.MouseButton = mouseButton;

        if (WheelCombo.SelectedItem is ComboBoxItem wheelItem && int.TryParse(wheelItem.Tag?.ToString(), out var wheel))
            action.WheelDelta = wheel;

        return action;
    }

    private void SelectTypeItem(ActionType type)
    {
        foreach (ComboBoxItem item in TypeCombo.Items)
        {
            if (item.Tag?.ToString() == type.ToString())
            {
                TypeCombo.SelectedItem = item;
                break;
            }
        }
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
        if (TypeCombo.SelectedItem is ComboBoxItem item && Enum.TryParse<ActionType>(item.Tag?.ToString(), out var type))
            _type = type;
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        if (KeyPanel == null) return;
        KeyPanel.Visibility = _type == ActionType.Key ? Visibility.Visible : Visibility.Collapsed;
        TextPanel.Visibility = _type == ActionType.Text ? Visibility.Visible : Visibility.Collapsed;
        MousePanel.Visibility = _type is ActionType.MouseButton or ActionType.MouseWheel ? Visibility.Visible : Visibility.Collapsed;
        MouseButtonPanel.Visibility = _type == ActionType.MouseButton ? Visibility.Visible : Visibility.Collapsed;
        WheelPanel.Visibility = _type == ActionType.MouseWheel ? Visibility.Visible : Visibility.Collapsed;
        MacroPanel.Visibility = _type == ActionType.Macro ? Visibility.Visible : Visibility.Collapsed;
        if (_type is ActionType.MouseButton or ActionType.MouseWheel) UpdateMousePositionUi();
        if (_type == ActionType.Macro && MacroSelectCombo.Items.Count == 0 && !_loadingAction)
            RefreshMacroChoices("", "");
    }

    private void UpdateMousePositionUi()
    {
        if (MousePositionText == null) return;
        MousePositionText.Text = _mouseUsePosition
            ? $"鼠标位置：{(_mouseScreen ? "屏幕" : "窗口")} {_mouseX * 100:0.#}% / {_mouseY * 100:0.#}%"
            : "鼠标位置：未设置（点击当前光标）";
    }

    private void PickMousePosition_OnClick(object sender, RoutedEventArgs e)
    {
        var result = MousePointCapture.Capture(Window.GetWindow(this), _mouseScreen, _mouseX, _mouseY, _mouseMonitor);
        if (result == null) return;
        _mouseUsePosition = true;
        _mouseX = result.X;
        _mouseY = result.Y;
        _mouseScreen = result.Screen;
        _mouseMonitor = result.Monitor;
        _mouseMonitorX = result.MonitorX;
        _mouseMonitorY = result.MonitorY;
        _mouseMonitorWidth = result.MonitorWidth;
        _mouseMonitorHeight = result.MonitorHeight;
        UpdateMousePositionUi();
    }

    private void RefreshMacroChoices(string id, string name)
    {
        _loadingMacros = true;
        var choices = App.Current.Store.MacroLibrary.Items
            .Select(m => new MacroChoice { Macro = m })
            .ToList();
        MacroSelectCombo.ItemsSource = choices;
        MacroSelectList.ItemsSource = choices;
        var selected = choices.FirstOrDefault(c => !string.IsNullOrWhiteSpace(id) && c.Macro.Id == id)
            ?? choices.FirstOrDefault(c => !string.IsNullOrWhiteSpace(name)
                && string.Equals(c.Macro.Name, name, StringComparison.Ordinal));
        MacroSelectCombo.SelectedItem = selected;
        MacroSelectList.SelectedItem = selected;
        MacroEmptyText.Visibility = choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected != null) LoadMacroPreview(selected.Macro);
        _loadingMacros = false;
    }

    private void LoadMacroPreview(MacroDefinition macro)
    {
        _macroRows.Clear();
        foreach (var step in macro.Steps) _macroRows.Add(new MacroStepRow(step));
        MacroList.Items.Refresh();
    }

    private void MacroSelect_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMacros) return;
        if (MacroSelectCombo.SelectedItem is MacroChoice choice)
        {
            MacroSelectList.SelectedItem = choice;
            LoadMacroPreview(choice.Macro);
        }
    }

    private void MacroSelectList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMacros || MacroSelectList.SelectedItem is not MacroChoice choice) return;
        _loadingMacros = true;
        MacroSelectCombo.SelectedItem = choice;
        _loadingMacros = false;
        LoadMacroPreview(choice.Macro);
    }

    private void EditMacro_OnClick(object sender, RoutedEventArgs e)
    {
        if (MacroSelectCombo.SelectedItem is MacroChoice choice) OpenMacroEditor(choice);
    }

    private void MacroSelectList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MacroSelectList.SelectedItem is not MacroChoice choice) return;
        e.Handled = true;
        OpenMacroEditor(choice);
    }

    private void OpenMacroEditor(MacroChoice choice)
    {
        var dialog = new MacroEditorDialog(choice.Macro) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        App.Current.Store.SaveMacroLibrary();
        RefreshMacroChoices(choice.Macro.Id, choice.Macro.Name);
    }

    private void EndCapture()
    {
        _capturingKey = false;
        _capturingMacroKey = false;
        InputMethod.SetIsInputMethodEnabled(this, true);
    }

    private void CaptureKey_OnClick(object sender, RoutedEventArgs e)
    {
        _capturingKey = true;
        _capturingMacroKey = false;
        InputMethod.SetIsInputMethodEnabled(this, false);
        KeyText.Text = "请按下键盘按键…";
        Focus();
        Keyboard.Focus(this);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingKey && !_capturingMacroKey) return;
        var key = KeyCapture.Resolve(e);

        // 允许把单个修饰键作为动作
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
            if (_capturingKey)
            {
                _key = modifierKey;
                KeyText.Text = KeyCatalog.GetName(modifierKey);
                CtrlCheck.IsChecked = modifierKey == KeyCatalog.VkControl;
                AltCheck.IsChecked = modifierKey == KeyCatalog.VkAlt;
                ShiftCheck.IsChecked = modifierKey == KeyCatalog.VkShift;
                WinCheck.IsChecked = modifierKey == KeyCatalog.VkLWin;
                EndCapture();
            }
            else if (_capturingMacroKey && MacroList.SelectedItem is MacroStepRow row)
            {
                row.Step.Key = modifierKey;
                row.Refresh();
                MacroList.Items.Refresh();
                _capturingMacroKey = false;
            }
            e.Handled = true;
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        if (_capturingKey)
        {
            _key = vk;
            // 捕获时按住的修饰键一并写入组合
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) CtrlCheck.IsChecked = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) AltCheck.IsChecked = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ShiftCheck.IsChecked = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) WinCheck.IsChecked = true;
            KeyText.Text = KeyCatalog.GetName(vk);
            EndCapture();
        }
        else if (_capturingMacroKey && MacroList.SelectedItem is MacroStepRow row)
        {
            row.Step.Key = vk;
            row.Refresh();
            MacroList.Items.Refresh();
            _capturingMacroKey = false;
        }
        e.Handled = true;
    }

    private void AddMacroKey_OnClick(object sender, RoutedEventArgs e)
    {
        var step = new MacroStep { Kind = MacroStepKind.Key, Key = 0x20, HoldMs = 30, DelayMs = 30 };
        _macroRows.Add(new MacroStepRow(step));
        MacroList.SelectedIndex = _macroRows.Count - 1;
        MacroList.Items.Refresh();
    }

    private void AddMacroDelay_OnClick(object sender, RoutedEventArgs e)
    {
        var step = new MacroStep { Kind = MacroStepKind.Delay, DelayMs = 100 };
        _macroRows.Add(new MacroStepRow(step));
        MacroList.SelectedIndex = _macroRows.Count - 1;
        MacroList.Items.Refresh();
    }

    private void AddMacroMouse_OnClick(object sender, RoutedEventArgs e)
    {
        var step = new MacroStep { Kind = MacroStepKind.MouseButton, MouseButton = MouseButtonKind.Left, HoldMs = 30, DelayMs = 30 };
        _macroRows.Add(new MacroStepRow(step));
        MacroList.SelectedIndex = _macroRows.Count - 1;
        MacroList.Items.Refresh();
    }

    private void DeleteMacroStep_OnClick(object sender, RoutedEventArgs e)
    {
        var index = MacroList.SelectedIndex;
        if (index < 0 || index >= _macroRows.Count) return;
        _macroRows.RemoveAt(index);
        MacroList.Items.Refresh();
        MacroStepEditor.Visibility = Visibility.Collapsed;
    }

    private void MacroList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 宏步骤来自工作台宏，只做预览；编辑入口是“编辑所选宏”。
        MacroStepEditor.Visibility = Visibility.Collapsed;
    }

    private void MacroCapture_OnClick(object sender, RoutedEventArgs e)
    {
        if (MacroList.SelectedItem is not MacroStepRow row || row.Step.Kind != MacroStepKind.Key) return;
        _capturingMacroKey = true;
        EndCapture();
        MacroKeyButton.Content = "请按键…";
        Focus();
        Keyboard.Focus(this);
    }

    private void ApplyMacroEditor()
    {
        if (MacroList.SelectedItem is not MacroStepRow row) return;
        _ = int.TryParse(MacroHoldBox.Text, out var hold);
        _ = int.TryParse(MacroDelayBox.Text, out var delay);
        row.Step.HoldMs = Math.Clamp(hold, 5, 5000);
        row.Step.DelayMs = Math.Clamp(delay, 0, 10000);
        row.Refresh();
    }

    private sealed class MacroStepRow : System.ComponentModel.INotifyPropertyChanged
    {
        public MacroStep Step { get; }
        public MacroStepRow(MacroStep step) => Step = step;

        public string Summary => Step.Kind switch
        {
            MacroStepKind.Key => $"按键 {Core.Input.KeyCatalog.GetName(Step.Key)}（按住 {Step.HoldMs}ms，间隔 {Step.DelayMs}ms）",
            MacroStepKind.KeyDown => $"按下 {Core.Input.KeyCatalog.GetName(Step.Key)}",
            MacroStepKind.KeyUp => $"松开 {Core.Input.KeyCatalog.GetName(Step.Key)}",
            MacroStepKind.Delay => $"延迟 {Step.DelayMs}ms",
            MacroStepKind.MouseButton => $"鼠标 {Step.MouseButton}（按住 {Step.HoldMs}ms）",
            MacroStepKind.MouseDown => $"按下鼠标 {Step.MouseButton}",
            MacroStepKind.MouseUp => $"松开鼠标 {Step.MouseButton}",
            MacroStepKind.Gamepad => $"手柄 {MappingSources.GetDisplayName(Step.GamepadSource)}（按住 {Step.HoldMs}ms）",
            MacroStepKind.GamepadDown => $"按下手柄 {MappingSources.GetDisplayName(Step.GamepadSource)}",
            MacroStepKind.GamepadUp => $"松开手柄 {MappingSources.GetDisplayName(Step.GamepadSource)}",
            MacroStepKind.Text => $"文本「{Step.Text}」（间隔 {Step.DelayMs}ms）",
            _ => "步骤",
        };

        public void Refresh() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Summary)));
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private static MacroStep CloneMacroStep(MacroStep step) => new()
    {
        Kind = step.Kind,
        Key = step.Key,
        Modifiers = step.Modifiers.ToList(),
        HoldMs = step.HoldMs,
        DelayMs = step.DelayMs,
        MouseButton = step.MouseButton,
        GamepadSource = step.GamepadSource,
        Text = step.Text,
        MouseUsePosition = step.MouseUsePosition,
        MouseX = step.MouseX,
        MouseY = step.MouseY,
        MouseScreen = step.MouseScreen,
        MouseMonitor = step.MouseMonitor,
        MouseMonitorX = step.MouseMonitorX,
        MouseMonitorY = step.MouseMonitorY,
        MouseMonitorWidth = step.MouseMonitorWidth,
        MouseMonitorHeight = step.MouseMonitorHeight,
    };
}

