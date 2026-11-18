using System.Windows;
using System.Windows.Input;
using LeadjoyBlade.Core.Mapping;

namespace LeadjoyBlade.App.Dialogs;

public partial class BaseActionDialog : Window
{
    public BindingAction Result { get; private set; } = new();
    public string ResultDescription { get; private set; } = "";
    private readonly string _keyName;
    private readonly string _defaultDescription;

    public BaseActionDialog(string keyName, string description, BindingAction action)
    {
        InitializeComponent();
        _keyName = keyName;
        _defaultDescription = description;
        ResultDescription = description;
        KeyNameText.Text = keyName;
        DescriptionBox.Text = description;
        TitleText.Text = $"编辑基础键 · {keyName}";
        Editor.LoadAction(action);
    }

    public BaseActionDialog(string keyName, BindingAction action)
        : this(keyName, keyName, action)
    {
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        Result = Editor.BuildAction();
        ResultDescription = string.IsNullOrWhiteSpace(DescriptionBox.Text)
            ? _defaultDescription
            : DescriptionBox.Text.Trim();
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
