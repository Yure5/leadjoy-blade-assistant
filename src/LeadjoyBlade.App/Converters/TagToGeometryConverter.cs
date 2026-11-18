using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LeadjoyBlade.App.Converters;

public sealed class TagToGeometryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            return Geometry.Parse(value as string ?? string.Empty);
        }
        catch
        {
            return Geometry.Empty;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
