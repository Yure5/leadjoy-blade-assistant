using System.Windows;
using System.Windows.Media;

namespace LeadjoyBlade.App;

public static class ThemeManager
{
    public static bool IsLight { get; private set; }
    public static event EventHandler? ThemeChanged;

    public static void Apply(bool light)
    {
        IsLight = light;
        var resources = Application.Current.Resources;
        if (light)
        {
            resources["Bg"] = Brush("#F2F4F8");
            resources["Surface"] = Brush("#FFFFFF");
            resources["Surface2"] = Brush("#E9EDF4");
            resources["Border"] = Brush("#D5DCE8");
            resources["Text"] = Brush("#1B2433");
            resources["TextMuted"] = Brush("#5E6A7E");
            resources["Accent"] = Brush("#E23B3D");
            resources["AccentHover"] = Brush("#C92F31");
            resources["Accent2"] = Brush("#2F6BFF");
            resources["Accent2Hover"] = Brush("#4F82FF");
            resources["Accent2Pressed"] = Brush("#2456C9");
            resources["BtnNeutral"] = Brush("#FFFFFF");
            resources["BtnNeutralDeep"] = Brush("#E4E9F2");
            resources["BtnNeutralHover"] = Brush("#EEF2F8");
            resources["Success"] = Brush("#1F9D63");
            resources["Warning"] = Brush("#B7791F");
            resources["Danger"] = Brush("#D64545");
            resources["ScrollThumb"] = Brush("#B9C3D4");
            resources["SelectionTint"] = Brush("#33E23B3D");
        }
        else
        {
            resources["Bg"] = Brush("#0A0C11");
            resources["Surface"] = Brush("#121620");
            resources["Surface2"] = Brush("#191E2A");
            resources["Border"] = Brush("#252B38");
            resources["Text"] = Brush("#E9EDF6");
            resources["TextMuted"] = Brush("#8B94A8");
            resources["Accent"] = Brush("#FF4D4F");
            resources["AccentHover"] = Brush("#FF6B6D");
            resources["Accent2"] = Brush("#5B8CFF");
            resources["Accent2Hover"] = Brush("#7AA2FF");
            resources["Accent2Pressed"] = Brush("#3E6FD8");
            resources["BtnNeutral"] = Brush("#161B26");
            resources["BtnNeutralDeep"] = Brush("#202838");
            resources["BtnNeutralHover"] = Brush("#252D3D");
            resources["Success"] = Brush("#3DD68C");
            resources["Warning"] = Brush("#F2B84B");
            resources["Danger"] = Brush("#FF5C5C");
            resources["ScrollThumb"] = Brush("#333B4D");
            resources["SelectionTint"] = Brush("#33FF4D4F");
        }
        RaiseChanged();
    }

    private static void RaiseChanged() => ThemeChanged?.Invoke(null, EventArgs.Empty);

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

