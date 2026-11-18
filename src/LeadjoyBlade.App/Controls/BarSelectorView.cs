using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LeadjoyBlade.App.Controls;

/// <summary>
/// RB 长按时的 8 个热键栏切换指示（FF14 WXHB 选择界面风格）。
/// 不做黑色底板：左侧数字对准左十字键四个方位，右侧数字对准右十字键四个方位。
/// </summary>
public sealed class BarSelectorView : Canvas
{
    private static readonly Brush Cream = Freeze(Color.FromRgb(0xF3, 0xE6, 0xD0));
    private static readonly Brush Gold = Freeze(Color.FromRgb(0xF2, 0xC8, 0x79));
    private static readonly Brush BoxBorder = Freeze(Color.FromArgb(0x88, 0xC8, 0xA4, 0x6A));
    private static readonly FontFamily Font = new("Microsoft YaHei UI, Segoe UI");

    private readonly Dictionary<int, TextBlock> _numbers = [];
    private readonly Dictionary<int, Border> _boxes = [];
    private int _current = -1;

    public double SlotSize { get; set; } = 56;

    public void Render(int current)
    {
        Children.Clear();
        _numbers.Clear();
        _boxes.Clear();
        _current = current;

        var s = SlotSize;
        var h = s * 0.62;
        var v = s * 0.55;
        var cx1 = s * 1.20;
        var cx2 = s * 3.50;
        var cy = s * 1.38;

        Width = Math.Ceiling(s * 4.9);
        Height = Math.Ceiling(s * 2.80);

        // 方向键：上 5、左 8、右 6、下 7。
        AddNumber(4, 5, cx1, cy - v, s);
        AddNumber(7, 8, cx1 - h, cy, s);
        AddNumber(5, 6, cx1 + h, cy, s);
        AddNumber(6, 7, cx1, cy + v, s);
        AddDpad(cx1, cy, s);

        // ABXY：上 1（Y）、左 4（X）、右 2（B）、下 3（A）。
        AddNumber(0, 1, cx2, cy - v, s);
        AddNumber(3, 4, cx2 - h, cy, s);
        AddNumber(1, 2, cx2 + h, cy, s);
        AddNumber(2, 3, cx2, cy + v, s);
        AddAbxy(cx2, cy, s);

        var switchText = new TextBlock
        {
            Text = "切换",
            FontFamily = Font,
            FontSize = s * 0.22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Gold,
            Width = s * 2.2,
            TextAlignment = TextAlignment.Center,
            Effect = Shadow(Colors.Black, 3),
        };
        Canvas.SetLeft(switchText, Width / 2 - s * 1.1);
        Canvas.SetTop(switchText, s * 2.42);
        Children.Add(switchText);
        UpdateCurrent(current);
    }

    public void UpdateCurrent(int current)
    {
        var previous = _current;
        _current = current;
        foreach (var (index, block) in _numbers)
        {
            var selected = index == current;
            block.Foreground = selected ? Gold : Cream;
            block.FontWeight = FontWeights.Bold;
            block.Opacity = 1.0;
            block.RenderTransform = new ScaleTransform(selected ? 1.08 : 1.0, selected ? 1.08 : 1.0);
            block.RenderTransformOrigin = new Point(0.5, 0.5);
            block.Effect = Shadow(selected ? Colors.Gold : Colors.Black, selected ? 7 : 3);
            if (_boxes.TryGetValue(index, out var box))
            {
                box.Background = selected
                    ? Freeze(Color.FromArgb(0x66, 0xFF, 0xD9, 0x8A))
                    : Freeze(Color.FromArgb(0x33, 0x0B, 0x09, 0x06));
                box.BorderBrush = selected ? Gold : BoxBorder;
                box.BorderThickness = new Thickness(selected ? 2 : 1);
            }
        }

        if (previous >= 0 && previous != current && current >= 0)
            PlayNumberPulse(current);
    }

    private void PlayNumberPulse(int index)
    {
        if (!_boxes.TryGetValue(index, out var box)) return;
        var size = box.Width * 1.1;
        var cx = Canvas.GetLeft(box) + box.Width / 2;
        var cy = Canvas.GetTop(box) + box.Height / 2;
        var ring = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = Brushes.Transparent,
            Stroke = Gold,
            StrokeThickness = 1.6,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var scale = new ScaleTransform(0.7, 0.7);
        ring.RenderTransform = scale;
        Canvas.SetLeft(ring, cx - size / 2);
        Canvas.SetTop(ring, cy - size / 2);
        Children.Add(ring);

        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.9, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.9, duration) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => Children.Remove(ring);
        ring.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    public void SetVisible(bool visible, bool animate)
    {
        // 指示图标要立刻出现，不做淡入。
        BeginAnimation(OpacityProperty, null);
        Opacity = visible ? 1 : 0;
    }

    private void AddNumber(int index, int value, double cx, double cy, double s)
    {
        var box = new Border
        {
            Width = s * 0.42,
            Height = s * 0.42,
            CornerRadius = new CornerRadius(s * 0.04),
            Background = Freeze(Color.FromArgb(0x33, 0x0B, 0x09, 0x06)),
            BorderBrush = BoxBorder,
            BorderThickness = new Thickness(1),
        };
        var block = new TextBlock
        {
            Text = value.ToString(),
            FontFamily = Font,
            FontSize = s * 0.26,
            FontWeight = FontWeights.Bold,
            Foreground = Cream,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = Shadow(Colors.Black, 3),
        };
        box.Child = block;
        Canvas.SetLeft(box, cx - s * 0.21);
        Canvas.SetTop(box, cy - s * 0.20);
        Children.Add(box);
        _boxes[index] = box;
        _numbers[index] = block;
    }

    private void AddDpad(double cx, double cy, double s)
    {
        var shadow = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M9,2 H15 V8 H21 V14 H15 V20 H9 V14 H3 V8 H9 Z"),
            Fill = Freeze(Color.FromArgb(0x66, 0x00, 0x00, 0x00)),
            Stretch = Stretch.Fill,
            Width = s * 0.62,
            Height = s * 0.62,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(shadow, cx - s * 0.31);
        Canvas.SetTop(shadow, cy - s * 0.29);
        Children.Add(shadow);

        var plus = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M9,2 H15 V8 H21 V14 H15 V20 H9 V14 H3 V8 H9 Z"),
            Fill = new LinearGradientBrush(
                Color.FromRgb(0xF6, 0xEA, 0xD3),
                Color.FromRgb(0xB8, 0xA2, 0x7C), new Point(0.5, 0), new Point(0.5, 1)),
            Stroke = Freeze(Color.FromArgb(0x99, 0x6F, 0x59, 0x3D)),
            StrokeThickness = Math.Max(0.6, s * 0.012),
            Stretch = Stretch.Fill,
            Width = s * 0.62,
            Height = s * 0.62,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(plus, cx - s * 0.31);
        Canvas.SetTop(plus, cy - s * 0.31);
        Children.Add(plus);

        foreach (var direction in new[] { 0, 90, 180, 270 })
            AddTriangle(cx, cy, s, direction);
    }

    private void AddTriangle(double cx, double cy, double s, double direction)
    {
        Point[] points = direction switch
        {
            0 => [new(cx - s * 0.06, cy - s * 0.14), new(cx + s * 0.06, cy - s * 0.14), new(cx, cy - s * 0.285)],
            90 => [new(cx + s * 0.14, cy - s * 0.06), new(cx + s * 0.14, cy + s * 0.06), new(cx + s * 0.285, cy)],
            180 => [new(cx - s * 0.06, cy + s * 0.14), new(cx + s * 0.06, cy + s * 0.14), new(cx, cy + s * 0.285)],
            _ => [new(cx - s * 0.14, cy - s * 0.06), new(cx - s * 0.14, cy + s * 0.06), new(cx - s * 0.285, cy)],
        };
        Children.Add(new Polygon
        {
            Points = new PointCollection(points),
            Fill = Freeze(Color.FromArgb(0xEE, 0xF3, 0xE6, 0xD0)),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 2.5,
                ShadowDepth = 1.2,
                Opacity = 0.95,
            },
        });
    }

    private void AddAbxy(double cx, double cy, double s)
    {
        var d = s * 0.20;
        AddCircle(cx, cy - d, s, "Y", Color.FromRgb(0xC9, 0x97, 0x1A));
        AddCircle(cx - d, cy, s, "X", Color.FromRgb(0x2E, 0x6D, 0xA4));
        AddCircle(cx + d, cy, s, "B", Color.FromRgb(0xB0, 0x3A, 0x2E));
        AddCircle(cx, cy + d, s, "A", Color.FromRgb(0x2E, 0x8B, 0x57));
    }

    private void AddCircle(double cx, double cy, double s, string text, Color color)
    {
        var size = s * 0.22;
        Children.Add(new Ellipse
        {
            Width = size,
            Height = size,
            Fill = Freeze(color),
            Stroke = Freeze(Color.FromArgb(0xCC, 0xFF, 0xF6, 0xE0)),
            StrokeThickness = Math.Max(1, s * 0.018),
            IsHitTestVisible = false,
        });
        Canvas.SetLeft(Children[^1], cx - size / 2);
        Canvas.SetTop(Children[^1], cy - size / 2);

        var block = new TextBlock
        {
            Text = text,
            FontFamily = Font,
            FontSize = s * 0.18,
            FontWeight = FontWeights.Bold,
            Foreground = Cream,
            Width = size,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false,
            Effect = Shadow(Colors.Black, 2),
        };
        Canvas.SetLeft(block, cx - size / 2);
        Canvas.SetTop(block, cy - s * 0.115);
        Children.Add(block);
    }

    private static DropShadowEffect Shadow(Color color, double blur) => new()
    {
        Color = color,
        BlurRadius = blur,
        ShadowDepth = 1.2,
        Opacity = 1.0,
    };

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
