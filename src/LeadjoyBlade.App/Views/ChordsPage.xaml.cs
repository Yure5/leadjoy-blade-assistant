using System.Windows;
using System.Windows.Controls;
using LeadjoyBlade.App.Dialogs;
using LeadjoyBlade.Core.Mapping;

namespace LeadjoyBlade.App.Views;

public partial class ChordsPage : UserControl
{
    public ChordsPage()
    {
        InitializeComponent();
        Reload();
    }

    public void Reload()
    {
        if (ChordList == null) return;
        var rows = App.Current.Engine.Profile.Chords.Select(c => new ChordRow(c)).ToList();
        ChordList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Add_OnClick(object sender, RoutedEventArgs e)
    {
        var chord = new ChordBinding
        {
            Name = "新组合键",
            Action = new BindingAction { Type = ActionType.Key, Key = 0x0D },
        };
        var dialog = new ChordEditorDialog(chord) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        App.Current.Engine.Profile.Chords.Add(chord);
        Save();
        Reload();
    }

    private void Edit_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ChordRow row) return;
        var chord = row.Chord;
        var copy = new ChordBinding
        {
            Name = chord.Name,
            Sources = [.. chord.Sources],
            Enabled = chord.Enabled,
            Action = new BindingAction
            {
                Type = chord.Action.Type,
                Key = chord.Action.Key,
                Text = chord.Action.Text,
                Modifiers = [.. chord.Action.Modifiers],
                MouseButton = chord.Action.MouseButton,
                WheelDelta = chord.Action.WheelDelta,
                Turbo = chord.Action.Turbo,
                Toggle = chord.Action.Toggle,
                TurboIntervalMs = chord.Action.TurboIntervalMs,
                MacroId = chord.Action.MacroId,
                MacroName = chord.Action.MacroName,
                MacroSource = chord.Action.MacroSource,
                Macro = chord.Action.Macro.Select(s => new MacroStep
                {
                    Kind = s.Kind,
                    Key = s.Key,
                    Modifiers = [.. s.Modifiers],
                    HoldMs = s.HoldMs,
                    DelayMs = s.DelayMs,
                    MouseButton = s.MouseButton,
                }).ToList(),
            },
        };
        var dialog = new ChordEditorDialog(copy) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        chord.Name = copy.Name;
        chord.Sources = copy.Sources;
        chord.Action = copy.Action;
        Save();
        Reload();
    }

    private void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ChordRow row) return;
        App.Current.Engine.Profile.Chords.Remove(row.Chord);
        Save();
        Reload();
    }

    private void Save()
    {
        App.Current.Store.Save(App.Current.Engine.Profile);
    }
}

public sealed class ChordRow
{
    public ChordBinding Chord { get; }
    public ChordRow(ChordBinding chord) => Chord = chord;

    public string Name => Chord.Name;
    public string SourcesText => string.Join(" + ", Chord.Sources.Select(MappingSources.GetDisplayName));
    public string ActionText => Chord.Action.Describe();
    public string ActionKind => Chord.Action.Type switch
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
