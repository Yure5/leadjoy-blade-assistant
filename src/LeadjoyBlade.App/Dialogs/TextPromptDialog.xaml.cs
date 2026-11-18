using System.Windows;
using System.Windows.Input;

namespace LeadjoyBlade.App.Dialogs;

public partial class TextPromptDialog : Window
{
    public string Result { get; private set; } = "";

    public TextPromptDialog(string title, string value)
    {
        InitializeComponent();
        TitleText.Text = title;
        Title = title;
        ValueBox.Text = value;
        Result = value;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        Result = ValueBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(Result)) return;
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
