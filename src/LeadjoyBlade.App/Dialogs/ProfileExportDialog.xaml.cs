using System.Windows;
using System.Windows.Input;

namespace LeadjoyBlade.App.Dialogs;

public partial class ProfileExportDialog : Window
{
    public ProfileExportDialog(IEnumerable<string> profiles)
    {
        InitializeComponent();
        foreach (var profile in profiles) ProfileList.Items.Add(profile);
        if (ProfileList.Items.Count > 0) ProfileList.SelectedIndex = 0;
    }

    public List<string> SelectedProfiles => ProfileList.SelectedItems.Cast<string>().ToList();

    private void SelectAll_OnClick(object sender, RoutedEventArgs e)
    {
        ProfileList.SelectAll();
    }

    private void Export_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProfiles.Count == 0)
        {
            MessageBox.Show("请至少选择一个配置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
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
