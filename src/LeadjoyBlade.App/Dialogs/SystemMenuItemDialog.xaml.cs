using System.Windows;
using System.Windows.Input;
using LeadjoyBlade.Core.Mapping;

namespace LeadjoyBlade.App.Dialogs;

public partial class SystemMenuItemDialog : Window
{
    private readonly SystemMenuItem _item;

    public SystemMenuItem Result { get; private set; } = new();

    public SystemMenuItemDialog(SystemMenuItem item)
    {
        InitializeComponent();
        _item = item.Clone();
        NameBox.Text = _item.Name;
        DescriptionBox.Text = _item.Description;
        Editor.LoadAction(_item.Action);
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        Result = new SystemMenuItem
        {
            Id = _item.Id,
            Name = string.IsNullOrWhiteSpace(name) ? "未命名项目" : name,
            Description = DescriptionBox.Text.Trim(),
            Action = Editor.BuildAction(),
        };
        DialogResult = true;
        Close();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Title_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
