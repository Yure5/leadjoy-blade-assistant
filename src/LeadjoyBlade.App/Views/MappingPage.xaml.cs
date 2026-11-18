using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LeadjoyBlade.App.Dialogs;
using LeadjoyBlade.Core.Mapping;

namespace LeadjoyBlade.App.Views;

public partial class MappingPage : UserControl
{
    private BindingLayer _layer = BindingLayer.Base;
    private bool _loadingMouse = true;
    private readonly DispatcherTimer _saveTimer;

    public MappingPage()
    {
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            App.Current.Store.Save(App.Current.Engine.Profile);
        };
        Reload();
    }

    public void Reload()
    {
        if (BindingList == null) return;
        var profile = App.Current.Engine.Profile;

        var rows = profile.Bindings
            .Where(b => b.Layer == _layer)
            .OrderBy(b => SourceOrder(b.Source))
            .Select(b => new BindingRow(b))
            .ToList();

        BindingList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _loadingMouse = true;
        var settings = profile.Settings;
        StickCombo.SelectedIndex = settings.LeftStickAsMouse ? 1 : settings.RightStickAsMouse ? 2 : 0;
        SensSlider.Value = Math.Clamp(settings.MouseSensitivity, SensSlider.Minimum, SensSlider.Maximum);
        AccelSlider.Value = Math.Clamp(settings.MouseAcceleration, AccelSlider.Minimum, AccelSlider.Maximum);
        DeadSlider.Value = Math.Clamp(settings.MouseDeadzonePercent, DeadSlider.Minimum, DeadSlider.Maximum);
        UpdateMouseLabels();
        _loadingMouse = false;
    }

    private void StickCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMouse) return;
        var tag = (StickCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var settings = App.Current.Engine.Profile.Settings;
        settings.LeftStickAsMouse = tag == "left";
        settings.RightStickAsMouse = tag == "right";
        ScheduleSave();
    }

    private void SettingSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingMouse) return;
        var settings = App.Current.Engine.Profile.Settings;
        settings.MouseSensitivity = SensSlider.Value;
        settings.MouseAcceleration = AccelSlider.Value;
        settings.MouseDeadzonePercent = (int)Math.Round(DeadSlider.Value);
        UpdateMouseLabels();
        ScheduleSave();
    }

    private void UpdateMouseLabels()
    {
        SensText.Text = SensSlider.Value.ToString("0.0");
        AccelText.Text = AccelSlider.Value.ToString("0.00");
        DeadText.Text = $"{DeadSlider.Value:0}%";
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static int SourceOrder(string source)
    {
        var index = MappingSources.All.ToList().FindIndex(s => s.Id == source);
        return index < 0 ? 999 : index;
    }

    private void LayerTab_OnChecked(object sender, RoutedEventArgs e)
    {
        _layer = BindingLayer.Base;
        Reload();
    }

    private void Add_OnClick(object sender, RoutedEventArgs e)
    {
        var profile = App.Current.Engine.Profile;
        var binding = new Binding
        {
            Source = "A",
            Layer = _layer,
            Action = new BindingAction { Type = ActionType.Key, Key = 0x20 },
        };
        var dialog = new BindingEditorDialog(binding) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        var existing = profile.Bindings.FirstOrDefault(b => b.Source == binding.Source && b.Layer == binding.Layer);
        if (existing != null) profile.Bindings.Remove(existing);
        profile.Bindings.Add(binding);
        Save();
        Reload();
    }

    private void Edit_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not BindingRow row) return;
        var profile = App.Current.Engine.Profile;
        var index = profile.Bindings.IndexOf(row.Binding);
        if (index < 0) return;

        var copy = new Binding
        {
            Source = row.Binding.Source,
            Layer = row.Binding.Layer,
            Enabled = row.Binding.Enabled,
            Action = CloneAction(row.Binding.Action),
        };
        var dialog = new BindingEditorDialog(copy) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        profile.Bindings[index] = copy;
        Save();
        Reload();
    }

    private void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not BindingRow row) return;
        var profile = App.Current.Engine.Profile;
        profile.Bindings.Remove(row.Binding);
        Save();
        Reload();
    }

    private void Save()
    {
        var profile = App.Current.Engine.Profile;
        App.Current.Store.Save(profile);
        SaveHint.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
    }

    private static BindingAction CloneAction(BindingAction source) => new()
    {
        Type = source.Type,
        Key = source.Key,
        Text = source.Text,
        Modifiers = [.. source.Modifiers],
        MouseButton = source.MouseButton,
        WheelDelta = source.WheelDelta,
        Turbo = source.Turbo,
        Toggle = source.Toggle,
        TurboIntervalMs = source.TurboIntervalMs,
        MacroId = source.MacroId,
        MacroName = source.MacroName,
        MacroSource = source.MacroSource,
        Macro = source.Macro.Select(s => new MacroStep
        {
            Kind = s.Kind,
            Key = s.Key,
            Modifiers = [.. s.Modifiers],
            HoldMs = s.HoldMs,
            DelayMs = s.DelayMs,
            MouseButton = s.MouseButton,
        }).ToList(),
    };
}

public sealed class BindingRow
{
    public Binding Binding { get; }
    public BindingRow(Binding binding) => Binding = binding;

    public string SourceName => MappingSources.GetDisplayName(Binding.Source);
    public string SourceCategory => MappingSources.All.FirstOrDefault(s => s.Id == Binding.Source)?.Category ?? "";
    public string ActionText => Binding.Action.Describe();
    public string ActionKind => Binding.Action.Type switch
    {
        ActionType.Key => "键盘",
        ActionType.Text => "文本",
        ActionType.MouseButton => "鼠标",
        ActionType.MouseWheel => "滚轮",
        ActionType.Macro => "宏",
        ActionType.Disabled => "禁用",
        _ => "动作",
    };
}

