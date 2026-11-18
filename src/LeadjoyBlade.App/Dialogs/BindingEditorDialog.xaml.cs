using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LeadjoyBlade.Core.Mapping;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.App.Dialogs;

public partial class BindingEditorDialog : Window
{
    private readonly Binding _binding;
    private readonly HashSet<string> _lastSources = [];
    private bool _capturing;
    public Binding Result => _binding;

    public BindingEditorDialog(Binding binding)
    {
        InitializeComponent();
        _binding = binding;

        foreach (var source in MappingSources.All)
        {
            SourceCombo.Items.Add(new ComboBoxItem { Content = $"{source.DisplayName}  ({source.Category})", Tag = source.Id });
        }

        SelectSource(binding.Source);
        Editor.LoadAction(binding.Action);
        App.Current.Devices.ReportReceived += Devices_OnReport;
        Closed += (_, _) => App.Current.Devices.ReportReceived -= Devices_OnReport;
    }

    private void Capture_OnClick(object sender, RoutedEventArgs e)
    {
        _capturing = !_capturing;
        CaptureButton.Content = _capturing ? "等待手柄按键…" : "捕获按键";
    }

    private void Devices_OnReport(object? sender, BladeReport report) =>
        Dispatcher.BeginInvoke(() => HandleReport(report));

    private void HandleReport(BladeReport report)
    {
        var pressed = BladeButtonsExtensions.All
            .Where(report.IsPressed)
            .Select(button => button.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var settings = App.Current.Engine.Profile.Settings;
        AddStickSources(pressed, "LS", report.LeftXNorm,
            settings.InvertLeftY ? -report.LeftYNorm : report.LeftYNorm, settings.LeftStickDeadzone);
        AddStickSources(pressed, "RS", report.RightXNorm,
            settings.InvertRightY ? -report.RightYNorm : report.RightYNorm, settings.RightStickDeadzone);

        var newlyPressed = pressed.Where(source => !_lastSources.Contains(source)).ToList();
        _lastSources.Clear();
        foreach (var source in pressed) _lastSources.Add(source);
        if (!_capturing || newlyPressed.Count == 0) return;

        SelectSource(newlyPressed[0]);
        _capturing = false;
        CaptureButton.Content = "捕获按键";
    }

    private static void AddStickSources(HashSet<string> sources, string prefix, double x, double y, double deadzone)
    {
        var dz = Math.Max(0.05, deadzone);
        var ax = Math.Abs(x);
        var ay = Math.Abs(y);
        if (ax < dz && ay < dz) return;

        if (ax >= dz && ay >= dz)
        {
            // 斜向不单独作为映射源，选占优方向；运行时会自动同时按两个方向键
            if (ax >= ay) sources.Add(x > 0 ? $"{prefix}_RIGHT" : $"{prefix}_LEFT");
            else sources.Add(y > 0 ? $"{prefix}_UP" : $"{prefix}_DOWN");
            return;
        }

        if (ax > ay) sources.Add(x > 0 ? $"{prefix}_RIGHT" : $"{prefix}_LEFT");
        else sources.Add(y > 0 ? $"{prefix}_UP" : $"{prefix}_DOWN");
    }

    private void SelectSource(string id)
    {
        foreach (ComboBoxItem item in SourceCombo.Items)
        {
            if (item.Tag?.ToString() == id)
            {
                SourceCombo.SelectedItem = item;
                return;
            }
        }
        SourceCombo.SelectedIndex = 0;
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

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (SourceCombo.SelectedItem is not ComboBoxItem item) return;
        _binding.Source = item.Tag?.ToString() ?? "A";
        _binding.Layer = BindingLayer.Base;
        _binding.Action = Editor.BuildAction();
        DialogResult = true;
        Close();
    }
}
