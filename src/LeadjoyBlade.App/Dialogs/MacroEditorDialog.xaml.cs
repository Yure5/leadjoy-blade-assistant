using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LeadjoyBlade.App.Input;
using LeadjoyBlade.Core.Input;
using LeadjoyBlade.Core.Mapping;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.App.Dialogs;

public partial class MacroEditorDialog : Window
{
    private readonly Binding _binding;
    private readonly List<MacroStep> _steps;
    private readonly MacroDefinition? _macroDefinition;
    private bool _recording;
    private bool _capturing;
    private bool _capturingSource;
    private DateTime _lastInput = DateTime.UtcNow;
    private readonly HashSet<BladeButtons> _lastButtons = [];
    private readonly List<MacroStep> _clipboard = [];
    private MacroStep? _draggedStep;
    private Point _dragStart;
    private bool _dragArmed;
    private bool _mouseUsePosition;
    private double _mouseX = 0.5;
    private double _mouseY = 0.5;
    private bool _mouseScreen;
    private string? _mouseMonitor;
    private int _mouseMonitorX;
    private int _mouseMonitorY;
    private int _mouseMonitorWidth;
    private int _mouseMonitorHeight;

    public MacroEditorDialog(MacroDefinition macro)
        : this(new Binding
        {
            Name = macro.Name,
            Source = "M1",
            Action = new BindingAction
            {
                Type = ActionType.Macro,
                Macro = macro.Steps.Select(CloneStep).ToList(),
            },
        })
    {
        _macroDefinition = macro;
    }

    public MacroEditorDialog(Binding binding)
    {
        InitializeComponent();
        _binding = binding;
        _steps = binding.Action.Macro.Select(CloneStep).ToList();

        NameBox.Text = binding.Name;
        foreach (var source in MappingSources.All)
            TriggerCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{source.DisplayName}  ({source.Category})",
                Tag = source.Id,
            });
        foreach (ComboBoxItem item in TriggerCombo.Items)
        {
            if (item.Tag?.ToString() == binding.Source)
            {
                TriggerCombo.SelectedItem = item;
                break;
            }
        }
        if (TriggerCombo.SelectedItem == null) TriggerCombo.SelectedIndex = 0;

        DeviceCombo.Items.Add(new ComboBoxItem { Content = "手柄", Tag = "gamepad" });
        DeviceCombo.Items.Add(new ComboBoxItem { Content = "键盘", Tag = "keyboard" });
        DeviceCombo.Items.Add(new ComboBoxItem { Content = "鼠标", Tag = "mouse" });
        DeviceCombo.SelectedIndex = 0;
        RepopulateComposer();

        App.Current.Devices.ReportReceived += Devices_OnReport;
        Closed += (_, _) => App.Current.Devices.ReportReceived -= Devices_OnReport;

        Refresh();
    }

    private void Refresh(int? selected = null)
    {
        StepsList.ItemsSource = _steps.Select((step, index) => new MacroStepRow(index + 1, step)).ToList();
        StepsList.Items.Refresh();
        UpdateTextSummary();
        if (StepsList.Items.Count == 0) return;
        var target = Math.Clamp(selected ?? StepsList.Items.Count - 1, 0, StepsList.Items.Count - 1);
        StepsList.SelectedIndex = target;
        Dispatcher.BeginInvoke(() => StepsList.ScrollIntoView(StepsList.Items[target]));
    }

    private void UpdateTextSummary()
    {
        if (TextSummaryText == null) return;
        var texts = _steps
            .Where(s => s.Kind == MacroStepKind.Text && !string.IsNullOrWhiteSpace(s.Text))
            .Select(s => s.Text.Replace("\r", " ").Replace("\n", " ").Trim())
            .ToList();
        if (texts.Count == 0)
        {
            TextSummaryText.Text = "";
            TextSummaryText.Visibility = Visibility.Collapsed;
            return;
        }
        TextSummaryText.Text = "文本：" + string.Join(" / ", texts);
        TextSummaryText.ToolTip = TextSummaryText.Text;
        TextSummaryText.Visibility = Visibility.Visible;
    }

    private void StepsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (StepsList.SelectedItem is not MacroStepRow row || !row.IsText) return;
        var index = _steps.IndexOf(row.Step);
        if (index < 0) return;
        var dialog = new TextPromptDialog("编辑文本步骤", row.Step.Text) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        row.Step.Text = dialog.Result;
        Refresh(index);
    }

    private void InsertStep(MacroStep step)
    {
        var index = StepsList.SelectedIndex >= 0 && StepsList.SelectedIndex < _steps.Count
            ? StepsList.SelectedIndex + 1
            : _steps.Count;
        _steps.Insert(index, step);
        Refresh(index);
    }

    private void StepsList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = e.OriginalSource is FrameworkElement { Tag: "DragHandle" };
        if (!_dragArmed) return;
        var container = ItemsControl.ContainerFromElement(StepsList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        _draggedStep = (container?.DataContext as MacroStepRow)?.Step;
        _dragStart = e.GetPosition(StepsList);
    }

    private void StepsList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
        _draggedStep = null;
    }

    private void StepsList_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || _draggedStep == null || e.LeftButton != MouseButtonState.Pressed) return;
        var offset = e.GetPosition(StepsList) - _dragStart;
        if (Math.Abs(offset.X) < 6 && Math.Abs(offset.Y) < 6) return;
        _dragArmed = false;
        DragDrop.DoDragDrop(StepsList, _draggedStep, DragDropEffects.Move);
        _draggedStep = null;
    }

    private void StepsList_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _draggedStep != null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void StepsList_OnDrop(object sender, DragEventArgs e)
    {
        if (_draggedStep == null) return;
        var oldIndex = _steps.IndexOf(_draggedStep);
        if (oldIndex < 0) return;

        var container = ItemsControl.ContainerFromElement(StepsList, e.OriginalSource as DependencyObject) as ListBoxItem;
        var targetRow = container?.DataContext as MacroStepRow;
        var newIndex = targetRow == null ? _steps.Count - 1 : _steps.IndexOf(targetRow.Step);
        if (newIndex < 0 || newIndex == oldIndex) return;

        _steps.RemoveAt(oldIndex);
        if (newIndex > oldIndex) newIndex--;
        newIndex = Math.Clamp(newIndex, 0, _steps.Count);
        _steps.Insert(newIndex, _draggedStep);
        Refresh(newIndex);
        _draggedStep = null;
    }

    private void Devices_OnReport(object? sender, BladeReport report) =>
        Dispatcher.BeginInvoke(() => HandleReport(report));

    private void HandleReport(BladeReport report)
    {
        var now = DateTime.UtcNow;
        var newlyPressed = BladeButtonsExtensions.All
            .Where(button => report.IsPressed(button) && !_lastButtons.Contains(button))
            .ToList();
        _lastButtons.Clear();
        foreach (var button in BladeButtonsExtensions.All)
            if (report.IsPressed(button)) _lastButtons.Add(button);

        if (_capturingSource)
        {
            if (newlyPressed.Count > 0) SelectComposer("gamepad", newlyPressed[0].ToString());
            return;
        }

        if (!_recording || newlyPressed.Count == 0) return;
        var gap = (int)(now - _lastInput).TotalMilliseconds;
        if (gap > 15)
            _steps.Add(new MacroStep { Kind = MacroStepKind.Delay, DelayMs = Math.Clamp(gap, 10, 5000) });

        foreach (var button in newlyPressed)
        {
            _steps.Add(new MacroStep
            {
                Kind = MacroStepKind.Gamepad,
                GamepadSource = button.ToString(),
                DelayMs = 20,
            });
        }
        _lastInput = now;
        Refresh();
    }

    private void Record_OnClick(object sender, RoutedEventArgs e)
    {
        _recording = !_recording;
        _capturing = false;
        _lastInput = DateTime.UtcNow;
        RecordButton.Content = _recording ? "■ 停止录制" : "● 开始录制";
        HintText.Text = _recording
            ? "正在录制：直接敲键盘，按 Esc 停止。按键之间的间隔会被记录。"
            : "录制已停止。";
    }

    private void DeviceCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceCombo == null) return;
        RepopulateComposer();
    }

    private void RepopulateComposer()
    {
        SourceCombo.Items.Clear();
        ActionCombo.Items.Clear();
        var device = (DeviceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (device == "keyboard")
        {
            foreach (var vk in KeyCatalog.AllKeys)
                SourceCombo.Items.Add(new ComboBoxItem { Content = KeyCatalog.GetName(vk), Tag = vk });
            foreach (var (label, tag) in new[] { ("单击", "tap"), ("按下", "down"), ("弹起", "up") })
                ActionCombo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        }
        else if (device == "mouse")
        {
            foreach (var (label, kind) in new[]
                     {
                         ("左键", MouseButtonKind.Left), ("右键", MouseButtonKind.Right),
                         ("中键", MouseButtonKind.Middle), ("侧键1", MouseButtonKind.X1),
                         ("侧键2", MouseButtonKind.X2),
                     })
                SourceCombo.Items.Add(new ComboBoxItem { Content = label, Tag = kind });
            foreach (var (label, tag) in new[] { ("单击", "tap"), ("双击", "double"), ("按下", "down"), ("弹起", "up") })
                ActionCombo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        }
        else
        {
            foreach (var source in MappingSources.All.Where(s => s.Category == "按键"))
                SourceCombo.Items.Add(new ComboBoxItem { Content = source.DisplayName, Tag = source.Id });
            foreach (var (label, tag) in new[] { ("单击", "tap"), ("按下", "down"), ("弹起", "up") })
                ActionCombo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        }
        if (SourceCombo.Items.Count > 0) SourceCombo.SelectedIndex = 0;
        if (ActionCombo.Items.Count > 0) ActionCombo.SelectedIndex = 0;
        UpdateMousePositionUi();
    }

    private static bool IsMouseKind(MacroStepKind kind) =>
        kind is MacroStepKind.MouseButton or MacroStepKind.MouseDown or MacroStepKind.MouseUp;

    private void UpdateMousePositionUi()
    {
        if (MousePositionButton == null) return;
        var device = (DeviceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var mouse = device == "mouse";
        if (MousePositionRow != null)
            MousePositionRow.Visibility = mouse ? Visibility.Visible : Visibility.Collapsed;
        MousePositionButton.Visibility = mouse ? Visibility.Visible : Visibility.Collapsed;
        MousePositionText.Visibility = mouse ? Visibility.Visible : Visibility.Collapsed;
        if (!mouse) return;
        MousePositionText.Text = _mouseUsePosition
            ? $"鼠标位置：{(_mouseScreen ? "屏幕" : "窗口")} {_mouseX * 100:0.#}% / {_mouseY * 100:0.#}%"
            : "鼠标位置：未设置（点击当前光标）";
    }

    private void LoadMousePosition(MacroStep step)
    {
        _mouseUsePosition = step.MouseUsePosition;
        _mouseX = step.MouseX;
        _mouseY = step.MouseY;
        _mouseScreen = step.MouseScreen;
        _mouseMonitor = step.MouseMonitor;
        _mouseMonitorX = step.MouseMonitorX;
        _mouseMonitorY = step.MouseMonitorY;
        _mouseMonitorWidth = step.MouseMonitorWidth;
        _mouseMonitorHeight = step.MouseMonitorHeight;
        UpdateMousePositionUi();
    }

    private void ApplyMousePosition(MacroStep step)
    {
        step.MouseUsePosition = true;
        step.MouseX = _mouseX;
        step.MouseY = _mouseY;
        step.MouseScreen = _mouseScreen;
        step.MouseMonitor = _mouseScreen ? _mouseMonitor : null;
        step.MouseMonitorX = _mouseMonitorX;
        step.MouseMonitorY = _mouseMonitorY;
        step.MouseMonitorWidth = _mouseMonitorWidth;
        step.MouseMonitorHeight = _mouseMonitorHeight;
    }

    private void StepsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StepsList.SelectedItem is MacroStepRow row && IsMouseKind(row.Step.Kind))
            LoadMousePosition(row.Step);
    }

    private void PickMousePosition_OnClick(object sender, RoutedEventArgs e)
    {
        var result = MousePointCapture.Capture(this, _mouseScreen, _mouseX, _mouseY, _mouseMonitor);
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

        // 选中已有鼠标步骤时，直接更新该步骤；否则位置留给下一次「添加步骤」。
        if (StepsList.SelectedItem is MacroStepRow row && IsMouseKind(row.Step.Kind))
        {
            var index = _steps.IndexOf(row.Step);
            if (index >= 0)
            {
                ApplyMousePosition(row.Step);
                Refresh(index);
            }
        }
    }

    private void AddComposedStep_OnClick(object sender, RoutedEventArgs e)
    {
        var device = (DeviceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var action = (ActionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "tap";
        if (SourceCombo.SelectedItem is not ComboBoxItem source) return;

        if (device == "keyboard" && source.Tag is int vk)
        {
            InsertStep(new MacroStep
            {
                Kind = action switch
                {
                    "down" => MacroStepKind.KeyDown,
                    "up" => MacroStepKind.KeyUp,
                    _ => MacroStepKind.Key,
                },
                Key = vk,
                HoldMs = 30,
                DelayMs = 20,
            });
        }
        else if (device == "gamepad")
        {
            InsertStep(new MacroStep
            {
                Kind = action switch
                {
                    "down" => MacroStepKind.GamepadDown,
                    "up" => MacroStepKind.GamepadUp,
                    _ => MacroStepKind.Gamepad,
                },
                GamepadSource = source.Tag?.ToString() ?? "A",
                DelayMs = 20,
            });
        }
        else if (device == "mouse" && source.Tag is MouseButtonKind kind)
        {
            if (action == "double")
            {
                InsertStep(CreateMouseStep(MacroStepKind.MouseButton, kind));
                InsertStep(new MacroStep { Kind = MacroStepKind.Delay, DelayMs = 40 });
                InsertStep(CreateMouseStep(MacroStepKind.MouseButton, kind));
            }
            else
            {
                InsertStep(CreateMouseStep(action switch
                {
                    "down" => MacroStepKind.MouseDown,
                    "up" => MacroStepKind.MouseUp,
                    _ => MacroStepKind.MouseButton,
                }, kind));
            }
        }
    }

    private MacroStep CreateMouseStep(MacroStepKind kind, MouseButtonKind button)
    {
        var step = new MacroStep
        {
            Kind = kind,
            MouseButton = button,
            HoldMs = 30,
            DelayMs = 20,
        };
        if (_mouseUsePosition) ApplyMousePosition(step);
        return step;
    }

    private void AddKey_OnClick(object sender, RoutedEventArgs e)
    {
        InsertStep(new MacroStep { Kind = MacroStepKind.Key, Key = 0x20, HoldMs = 30, DelayMs = 30 });
    }

    private void AddKeyDown_OnClick(object sender, RoutedEventArgs e)
    {
        InsertStep(new MacroStep { Kind = MacroStepKind.KeyDown, Key = 0x20, DelayMs = 20 });
    }

    private void AddKeyUp_OnClick(object sender, RoutedEventArgs e)
    {
        InsertStep(new MacroStep { Kind = MacroStepKind.KeyUp, Key = 0x20, DelayMs = 20 });
    }

    private void AddDelay_OnClick(object sender, RoutedEventArgs e)
    {
        InsertStep(new MacroStep { Kind = MacroStepKind.Delay, DelayMs = 100 });
    }

    private void AddText_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new TextPromptDialog("添加文本步骤", "要输入的文字") { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.Result)) return;
        InsertStep(new MacroStep { Kind = MacroStepKind.Text, Text = dialog.Result, DelayMs = 80 });
    }

    private void Capture_OnClick(object sender, RoutedEventArgs e)
    {
        _capturingSource = true;
        _capturing = false;
        _recording = false;
        InputMethod.SetIsInputMethodEnabled(this, false);
        RecordButton.Content = "● 开始录制";
        HintText.Text = "等待按键：按键盘、鼠标或手柄键，会自动切换设备并选中。";
        Focus();
    }

    private void Window_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_capturingSource) return;
        MouseButtonKind? kind = e.ChangedButton switch
        {
            MouseButton.Left => MouseButtonKind.Left,
            MouseButton.Right => MouseButtonKind.Right,
            MouseButton.Middle => MouseButtonKind.Middle,
            MouseButton.XButton1 => MouseButtonKind.X1,
            MouseButton.XButton2 => MouseButtonKind.X2,
            _ => null,
        };
        if (kind == null) return;
        SelectComposer("mouse", kind.Value);
        e.Handled = true;
    }

    private void SelectComposer(string device, object sourceTag)
    {
        foreach (ComboBoxItem item in DeviceCombo.Items)
        {
            if (item.Tag?.ToString() != device) continue;
            DeviceCombo.SelectedItem = item;
            break;
        }
        RepopulateComposer();
        foreach (ComboBoxItem item in SourceCombo.Items)
        {
            if (!Equals(item.Tag, sourceTag)) continue;
            SourceCombo.SelectedItem = item;
            break;
        }
        _capturingSource = false;
        InputMethod.SetIsInputMethodEnabled(this, true);
        HintText.Text = "已选中按键，选择动作后点“添加步骤”。";
    }

    private void CaptureStepKey_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not MacroStepRow row) return;
        StepsList.SelectedItem = row;
        _capturing = true;
        _recording = false;
        InputMethod.SetIsInputMethodEnabled(this, false);
        RecordButton.Content = "● 开始录制";
        HintText.Text = "请按下一个键，作为该步骤的按键。";
        Focus();
    }

    private List<MacroStepRow> SelectedRows() => StepsList.SelectedItems
        .OfType<MacroStepRow>()
        .OrderBy(row => _steps.IndexOf(row.Step))
        .ToList();

    private void CopySelection()
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        _clipboard.Clear();
        _clipboard.AddRange(rows.Select(row => CloneStep(row.Step)));
        HintText.Text = $"已复制 {rows.Count} 个步骤。";
    }

    private void PasteBelow()
    {
        if (_clipboard.Count == 0) return;
        var rows = SelectedRows();
        var index = rows.Count > 0 ? _steps.IndexOf(rows[^1].Step) + 1 : _steps.Count;
        _steps.InsertRange(index, _clipboard.Select(CloneStep));
        Refresh(index);
        HintText.Text = $"已粘贴 {_clipboard.Count} 个步骤。";
    }

    private void Copy_OnClick(object sender, RoutedEventArgs e) => CopySelection();

    private void Paste_OnClick(object sender, RoutedEventArgs e) => PasteBelow();

    private void CutSelection()
    {
        if (SelectedRows().Count == 0) return;
        CopySelection();
        DeleteSelection();
        HintText.Text = "已剪切选中步骤。";
    }

    private void Cut_OnClick(object sender, RoutedEventArgs e) => CutSelection();

    private void DeleteSelection()
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        var first = _steps.IndexOf(rows[0].Step);
        foreach (var row in rows.OrderByDescending(r => _steps.IndexOf(r.Step)))
            _steps.Remove(row.Step);
        Refresh(Math.Min(first, _steps.Count - 1));
    }

    private void DeleteStep_OnClick(object sender, RoutedEventArgs e) => DeleteSelection();

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingSource)
        {
            if (e.IsRepeat) return;
            var captureKey = KeyCapture.Resolve(e);
            var vkCapture = KeyInterop.VirtualKeyFromKey(captureKey);
            if (vkCapture != 0) SelectComposer("keyboard", vkCapture);
            e.Handled = true;
            return;
        }

        if (!_recording && !_capturing && StepsList.IsKeyboardFocusWithin)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (e.Key == Key.C)
                {
                    CopySelection();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.V)
                {
                    PasteBelow();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.X)
                {
                    CutSelection();
                    e.Handled = true;
                    return;
                }
            }
            if (e.Key == Key.Delete)
            {
                DeleteSelection();
                e.Handled = true;
                return;
            }
        }

        if (!_recording && !_capturing) return;
        if (e.IsRepeat) return;

        var key = KeyCapture.Resolve(e);
        if (key == Key.Escape && _recording)
        {
            _recording = false;
            InputMethod.SetIsInputMethodEnabled(this, true);
            RecordButton.Content = "● 开始录制";
            HintText.Text = "录制已停止。";
            e.Handled = true;
            return;
        }

        if (_capturing)
        {
            var index = StepsList.SelectedIndex;
            if (index >= 0 && index < _steps.Count)
            {
                _steps[index].Kind = MacroStepKind.Key;
                _steps[index].Key = KeyInterop.VirtualKeyFromKey(key);
                Refresh();
                StepsList.SelectedIndex = index;
            }
            _capturing = false;
            InputMethod.SetIsInputMethodEnabled(this, true);
            HintText.Text = "按键已更新。";
            e.Handled = true;
            return;
        }

        if (_recording)
        {
            var now = DateTime.UtcNow;
            var gap = (int)(now - _lastInput).TotalMilliseconds;
            if (gap > 15)
                _steps.Add(new MacroStep { Kind = MacroStepKind.Delay, DelayMs = Math.Clamp(gap, 10, 5000) });

            var vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return;
            var modifiers = new List<int>();
            if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers.Add(KeyCatalog.VkControl);
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers.Add(KeyCatalog.VkShift);
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers.Add(KeyCatalog.VkAlt);
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers.Add(KeyCatalog.VkLWin);
            }

            _steps.Add(new MacroStep
            {
                Kind = MacroStepKind.Key,
                Key = vk,
                Modifiers = modifiers,
                HoldMs = 30,
                DelayMs = 20,
            });
            _lastInput = now;
            Refresh();
            e.Handled = true;
        }
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        _binding.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "新宏" : NameBox.Text.Trim();
        if (TriggerCombo.SelectedItem is ComboBoxItem item)
            _binding.Source = item.Tag?.ToString() ?? "M1";
        _binding.Action.Type = ActionType.Macro;
        _binding.Action.Macro = _steps.Select(CloneStep).ToList();
        if (_macroDefinition != null)
        {
            _macroDefinition.Name = _binding.Name;
            _macroDefinition.Steps = _binding.Action.Macro;
        }
        DialogResult = true;
        Close();
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

    private static MacroStep CloneStep(MacroStep step) => new()
    {
        Kind = step.Kind,
        Key = step.Key,
        Modifiers = [.. step.Modifiers],
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

public sealed class MacroStepRow : System.ComponentModel.INotifyPropertyChanged
{
    public MacroStepRow(int index, MacroStep step)
    {
        Index = index;
        Step = step;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }
    public MacroStep Step { get; }

    public bool EditableKey => Step.Kind is MacroStepKind.Key or MacroStepKind.KeyDown or MacroStepKind.KeyUp;
    public bool IsText => Step.Kind == MacroStepKind.Text;
    public string TextContent => Step.Text;
    public bool ShowKey => EditableKey;
    public bool IsDelay => Step.Kind == MacroStepKind.Delay;
    public bool ShowSource => Step.Kind is MacroStepKind.Gamepad or MacroStepKind.GamepadDown or MacroStepKind.GamepadUp
        or MacroStepKind.MouseButton or MacroStepKind.MouseDown or MacroStepKind.MouseUp;
    public bool ShowHold => Step.Kind is MacroStepKind.Key or MacroStepKind.MouseButton;
    public bool ShowGap => Step.Kind is MacroStepKind.Key or MacroStepKind.KeyDown or MacroStepKind.KeyUp
        or MacroStepKind.Gamepad or MacroStepKind.GamepadDown or MacroStepKind.GamepadUp
        or MacroStepKind.MouseButton or MacroStepKind.MouseDown or MacroStepKind.MouseUp
        or MacroStepKind.Text;

    public string KeyName => Step.Kind switch
    {
        MacroStepKind.Key or MacroStepKind.KeyDown or MacroStepKind.KeyUp => KeyCatalog.GetName(Step.Key),
        MacroStepKind.Gamepad or MacroStepKind.GamepadDown or MacroStepKind.GamepadUp =>
            MappingSources.GetDisplayName(Step.GamepadSource),
        MacroStepKind.MouseButton or MacroStepKind.MouseDown or MacroStepKind.MouseUp =>
            Step.MouseUsePosition
                ? $"鼠标 {Step.MouseButton} {(Step.MouseScreen ? "屏幕" : "窗口")} {Step.MouseX * 100:0.#}%/{Step.MouseY * 100:0.#}%"
                : $"鼠标 {Step.MouseButton}（当前光标）",
        MacroStepKind.Delay => $"等待 {Step.DelayMs}ms",
        MacroStepKind.Text => $"文本「{Step.Text}」",
        _ => "—",
    };

    public int HoldMs
    {
        get => Step.HoldMs;
        set
        {
            if (value == Step.HoldMs) return;
            Step.HoldMs = Math.Clamp(value, 0, 60000);
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HoldMs)));
        }
    }

    public int DelayMs
    {
        get => Step.DelayMs;
        set
        {
            if (value == Step.DelayMs) return;
            Step.DelayMs = Math.Clamp(value, 0, 60000);
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DelayMs)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(KeyName)));
        }
    }

    public string KindText => Step.Kind switch
    {
        MacroStepKind.Key => "按键",
        MacroStepKind.KeyDown => "按下",
        MacroStepKind.KeyUp => "弹起",
        MacroStepKind.Delay => "延迟",
        MacroStepKind.MouseButton => "鼠标",
        MacroStepKind.MouseDown => "鼠标按下",
        MacroStepKind.MouseUp => "鼠标弹起",
        MacroStepKind.Gamepad => "手柄键",
        MacroStepKind.GamepadDown => "手柄按下",
        MacroStepKind.GamepadUp => "手柄弹起",
        MacroStepKind.Text => "文本",
        _ => "步骤",
    };
}
