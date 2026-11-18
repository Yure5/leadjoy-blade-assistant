using System.Windows;
using System.Windows.Controls;
using LeadjoyBlade.App.Dialogs;
using LeadjoyBlade.Core.Mapping;

namespace LeadjoyBlade.App.Views;

public partial class MacroPage : UserControl
{
    public MacroPage()
    {
        InitializeComponent();
        Reload();
    }

    public void Reload()
    {
        if (MacroList == null) return;
        var rows = App.Current.Store.MacroLibrary.Items
            .Select(m => new MacroRow(m))
            .ToList();
        MacroList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Add_OnClick(object sender, RoutedEventArgs e)
    {
        var macro = new MacroDefinition { Name = "新宏" };
        var dialog = new MacroEditorDialog(macro) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        App.Current.Store.MacroLibrary.Items.Add(macro);
        App.Current.Store.SaveMacroLibrary();
        Reload();
    }

    private void Edit_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not MacroRow row) return;
        var dialog = new MacroEditorDialog(row.Macro) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        App.Current.Store.SaveMacroLibrary();
        Reload();
    }

    private void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not MacroRow row) return;
        App.Current.Store.MacroLibrary.Items.Remove(row.Macro);
        App.Current.Store.SaveMacroLibrary();
        Reload();
    }
}

public sealed class MacroRow
{
    public MacroRow(MacroDefinition macro) => Macro = macro;
    public MacroDefinition Macro { get; }
    public string Name => string.IsNullOrWhiteSpace(Macro.Name) ? "未命名宏" : Macro.Name;
    public string StepsText => $"{Macro.Steps.Count} 步";
}
