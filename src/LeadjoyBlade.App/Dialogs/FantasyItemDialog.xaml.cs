using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LeadjoyBlade.Core.Mapping;
using Microsoft.Win32;

namespace LeadjoyBlade.App.Dialogs;

public partial class FantasyItemDialog : Window
{
    private static readonly string[] Swatches =
    [
        "#8B3A3A", "#3A5A8B", "#3A8B5A", "#8B5A3A", "#6B3A8B", "#3A8B8B",
        "#B03A2E", "#7D3C98", "#2E86C1", "#148F77", "#B7950B", "#5D6D7E",
        "#512E5F", "#1E8449", "#A04000", "#34495E",
    ];

    private readonly FantasyItem _item;
    private string? _imageBase64;
    private bool _loading = true;
    private bool _syncingColor;
    private Color _color = Color.FromRgb(0x8B, 0x3A, 0x3A);

    public FantasyItemDialog(FantasyItem item)
    {
        InitializeComponent();
        _item = item;
        TitleText.Text = string.IsNullOrWhiteSpace(item.Id) ? "新建幻想映射" : "编辑幻想映射";
        NameBox.Text = item.Name;
        _imageBase64 = item.ImageBase64;
        InitSwatches();
        IconTextBox.Text = item.IconText ?? "";
        SetColor(ParseColor(item.IconColor, _color), updateText: true, updateSliders: true, regenerate: false);
        Editor.LoadAction(item.Action);
        UpdatePreview();
        _loading = false;
        if (!string.IsNullOrWhiteSpace(IconTextBox.Text)) RegenerateIcon();
    }

    private void InitSwatches()
    {
        foreach (var hex in Swatches)
        {
            var color = ParseColor(hex, Colors.Gray);
            var swatch = new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xF0, 0xC8)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = hex,
                ToolTip = hex,
            };
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                SetColor(ParseColor(swatch.Tag as string, _color), true, true, true);
                e.Handled = true;
            };
            SwatchesPanel.Children.Add(swatch);
        }
    }

    private void SetColor(Color color, bool updateText, bool updateSliders, bool regenerate)
    {
        _color = color;
        ColorPreview.Background = new SolidColorBrush(color);
        _syncingColor = true;
        if (updateSliders)
        {
            RedSlider.Value = color.R;
            GreenSlider.Value = color.G;
            BlueSlider.Value = color.B;
            RedText.Text = color.R.ToString();
            GreenText.Text = color.G.ToString();
            BlueText.Text = color.B.ToString();
        }
        if (updateText)
            IconColorBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _syncingColor = false;
        if (regenerate && !_loading) RegenerateIcon();
    }

    private void ColorSlider_OnChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _syncingColor || RedText == null) return;
        var color = Color.FromRgb((byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value);
        RedText.Text = color.R.ToString();
        GreenText.Text = color.G.ToString();
        BlueText.Text = color.B.ToString();
        SetColor(color, updateText: true, updateSliders: false, regenerate: true);
    }

    private void ColorText_OnChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _syncingColor || IconColorBox == null) return;
        var parsed = ParseColor(IconColorBox.Text, _color);
        if (!string.Equals(IconColorBox.Text.Trim(), $"#{_color.R:X2}{_color.G:X2}{_color.B:X2}",
                StringComparison.OrdinalIgnoreCase))
            SetColor(parsed, updateText: false, updateSliders: true, regenerate: true);
    }

    private void IconText_OnChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        RegenerateIcon();
    }

    private void RegenerateIcon()
    {
        var text = IconTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > 2) text = text[..2];
        _imageBase64 = MakeIconBase64(text, _color);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (string.IsNullOrWhiteSpace(_imageBase64))
        {
            IconPreview.Source = null;
            return;
        }
        try
        {
            var bytes = Convert.FromBase64String(_imageBase64);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            IconPreview.Source = image;
        }
        catch { IconPreview.Source = null; }
    }

    private void PickImage_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.UriSource = new Uri(dialog.FileName);
            source.EndInit();
            source.Freeze();

            // 居中裁成正方形，再用最近邻放大到 128×128，避免插值把黑色描边缩成左/上细、右/下粗。
            var width = source.PixelWidth;
            var height = source.PixelHeight;
            var side = Math.Min(width, height);
            var cropX = (width - side) / 2;
            var cropY = (height - side) / 2;
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var srcStride = width * 4;
            var srcPixels = new byte[srcStride * height];
            converted.CopyPixels(srcPixels, srcStride, 0);

            const int target = 128;
            var dstStride = target * 4;
            var dstPixels = new byte[dstStride * target];
            for (var y = 0; y < target; y++)
            {
                var sy = cropY + y * side / target;
                for (var x = 0; x < target; x++)
                {
                    var sx = cropX + x * side / target;
                    var si = sy * srcStride + sx * 4;
                    var di = y * dstStride + x * 4;
                    dstPixels[di] = srcPixels[si];
                    dstPixels[di + 1] = srcPixels[si + 1];
                    dstPixels[di + 2] = srcPixels[si + 2];
                    dstPixels[di + 3] = srcPixels[si + 3];
                }
            }

            var transformed = BitmapSource.Create(target, target, 96, 96,
                PixelFormats.Bgra32, null, dstPixels, dstStride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(transformed));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            _imageBase64 = Convert.ToBase64String(stream.ToArray());
            UpdatePreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"图片处理失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearImage_OnClick(object sender, RoutedEventArgs e)
    {
        _imageBase64 = null;
        UpdatePreview();
    }

    private void GenerateIcon_OnClick(object sender, RoutedEventArgs e)
    {
        var text = IconTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = string.IsNullOrWhiteSpace(NameBox.Text) ? "?" : NameBox.Text.Trim()[..1];
            _loading = true;
            IconTextBox.Text = text;
            _loading = false;
        }
        RegenerateIcon();
    }

    internal static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            var text = value.Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            return (Color)ColorConverter.ConvertFromString(text);
        }
        catch { return fallback; }
    }

    internal static string MakeIconBase64(string text, Color background)
    {
        const int size = 128;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // 完全平面：纯色圆角底 + 米金描边 + 均匀黑色中心圆。
            var iconRect = new Rect(2, 2, size - 4, size - 4);
            dc.DrawRoundedRectangle(new SolidColorBrush(background), null, iconRect, 16, 16);
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xD9, 0xB5)), 3),
                iconRect, 16, 16);

            const double orbDiameter = 92;
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xD0, 0x08, 0x07, 0x05)), null,
                new Point(size / 2.0, size / 2.0), orbDiameter / 2, orbDiameter / 2);

            var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var fontSize = text.Length > 1 ? 47 : 68;
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, fontSize, new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xE0)), 1.0)
            {
                TextAlignment = TextAlignment.Left,
            };
            var x = (size - formatted.Width) / 2;
            var y = 58 - formatted.Height / 2;
            dc.DrawText(formatted, new Point(x, y));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        _item.Name = NameBox.Text.Trim();
        _item.ImageBase64 = _imageBase64;
        _item.IconText = IconTextBox.Text.Trim();
        _item.IconColor = IconColorBox.Text.Trim();
        _item.Action = Editor.BuildAction();
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
