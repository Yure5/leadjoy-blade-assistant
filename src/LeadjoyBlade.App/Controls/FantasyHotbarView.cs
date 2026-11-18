using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LeadjoyBlade.Core.Mapping;

namespace LeadjoyBlade.App.Controls;

/// <summary>
/// FF14 风格双十字热键栏。
/// 每半为两个 4 格菱形（方向键 / ABXY），上下两格靠近，左右两格位于上下格中间两侧。
/// 两半之间是竖分隔线 + 可点击锁；底部显示“热键栏 N”和 RB。
/// </summary>
public sealed class FantasyHotbarView : Canvas
{
    private static readonly Brush FilledBg = Freeze(Color.FromArgb(0xD9, 0x18, 0x12, 0x0E));
    private static readonly Brush EmptyBg = Freeze(Color.FromArgb(0x4D, 0x18, 0x12, 0x0E));
    private static readonly Brush EmptyBgStrong = Freeze(Color.FromArgb(0x99, 0x18, 0x12, 0x0E));
    private static readonly Brush FilledBorder = Freeze(Color.FromArgb(0xB3, 0xC8, 0xA4, 0x6A));
    private static readonly Brush EmptyBorder = Freeze(Color.FromArgb(0x66, 0xC8, 0xA4, 0x6A));
    private static readonly Brush EmptyBorderStrong = Freeze(Color.FromArgb(0x99, 0xC8, 0xA4, 0x6A));
    private static readonly Brush ActiveBorder = Freeze(Color.FromRgb(0xFF, 0xD9, 0x8A));
    private static readonly Brush AccentBorder = Freeze(Color.FromRgb(0xFF, 0x4D, 0x4F));
    private static readonly Brush TextBrush = Freeze(Color.FromRgb(0xF3, 0xE6, 0xD0));
    private static readonly Brush MutedBrush = Freeze(Color.FromRgb(0x9A, 0x8F, 0x7E));
    private static readonly Brush GoldBrush = Freeze(Color.FromRgb(0xF2, 0xC8, 0x79));
    private static readonly Brush DividerBrush = Freeze(Color.FromArgb(0x88, 0xC8, 0xA4, 0x6A));
    private static readonly Brush LockBg = Freeze(Color.FromArgb(0xCC, 0x12, 0x0E, 0x0A));
    private static readonly FontFamily Font = new("Microsoft YaHei UI, Segoe UI");

    private readonly List<HalfVisual> _halves = [];
    private readonly List<SlotEntry> _entries = [];
    private System.Windows.Shapes.Path? _lockShackle;
    private Rectangle? _lockBody;
    private bool _locked;
    private int _selected = -1;

    private sealed class HalfVisual
    {
        public required Grid Group { get; init; }
        public required Canvas Glow { get; init; }
        public required Canvas Combo { get; init; }
        public required ScaleTransform GroupScale { get; init; }
        public required Grid LabelBox { get; init; }
        public required TextBlock LabelText { get; init; }
        public required bool Left { get; init; }
    }

    private sealed record SlotEntry(Border Border, HalfVisual Half, bool HasItem, TextBlock? KeyText);

    public double SlotSize { get; set; } = 47;
    public double KeyFontSize { get; set; } = 9;
    public double NameFontSize { get; set; } = 10;
    public double FooterFontSize { get; set; } = 12;
    public double IdleOpacity { get; set; } = 0.92;
    public bool ShowSlotKeys { get; set; } = true;
    public bool ShowSetBadge { get; set; } = true;
    public bool ShowLock { get; set; } = true;
    public bool StrongEmptySlots { get; set; }

    public bool Locked => _locked;
    public int SelectedIndex => _selected;
    public IReadOnlyList<Border> SlotBorders => _entries.Select(e => e.Border).ToList();

    public event EventHandler? LockClicked;

    /// <summary>渲染热键栏；extensionStart 不为空时只渲染该半的 8 格并居中。</summary>
    public void Render(
        FantasySet set,
        FantasyLibrary library,
        int setIndex,
        int? extensionStart = null,
        bool locked = false,
        bool animate = false,
        bool fromRight = false)
    {
        Children.Clear();
        _entries.Clear();
        _halves.Clear();
        _lockShackle = null;
        _lockBody = null;
        _selected = -1;
        _locked = locked;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        RenderTransform = Transform.Identity;

        var s = SlotSize;
        var v = s * 0.60;
        var h = s * 1.00;
        var d = s * 3.07;
        var gap = s * 0.30;
        var pad = 4.0;
        var halfW = d + 2 * h + s;
        var halfH = 2 * v + s;
        var extension = extensionStart.HasValue;
        var lockSpace = ShowLock ? s * 0.30 : 0;
        var footerH = ShowSetBadge ? Math.Max(22, s * 0.56) : 0;
        var width = extension ? halfW + pad * 2 : halfW * 2 + gap + pad * 2;
        var top = pad;
        var footerY = top + halfH + lockSpace;
        var height = footerY + footerH + 4;

        Width = Math.Ceiling(width);
        Height = Math.Ceiling(height);

        if (extensionStart is int start)
        {
            var x = (width - halfW) / 2;
            AddHalf(x, top, start == 0, halfW, halfH, s, v, h, d, set, library, start == 0 ? "LT" : "RT");
        }
        else
        {
            var leftX = pad;
            var rightX = pad + halfW + gap;
            AddHalf(leftX, top, true, halfW, halfH, s, v, h, d, set, library, "LT");
            AddHalf(rightX, top, false, halfW, halfH, s, v, h, d, set, library, "RT");

            var dividerX = leftX + halfW + gap / 2;
            AddDivider(dividerX, top, halfH, s, locked);
        }

        if (ShowSetBadge) AddFooter(width / 2, footerY, footerH, setIndex, s);

        if (animate) PlaySwitchIn(fromRight);
    }

    private void AddHalf(
        double x,
        double top,
        bool left,
        double halfW,
        double halfH,
        double s,
        double v,
        double h,
        double d,
        FantasySet set,
        FantasyLibrary library,
        string modifier)
    {
        var groupScale = new ScaleTransform(1, 1);
        var group = new Grid
        {
            Width = halfW,
            Height = halfH,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = groupScale,
        };

        var cx1 = h + s / 2;
        var cy = v + s / 2;
        var cx2 = cx1 + d;
        var baseIndex = left ? 0 : 8;

        // 一整块柔光覆盖该侧 8 格，外扩一点包住双十字，边缘 alpha 到 0。
        var glow = new Canvas { Width = halfW, Height = halfH, Opacity = 0, IsHitTestVisible = false };
        var glowImage = new Image
        {
            Source = GetGlowTexture(),
            Width = halfW + s * 0.48,
            Height = halfH + s * 0.70,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(glowImage, (halfW - glowImage.Width) / 2);
        Canvas.SetTop(glowImage, (halfH - glowImage.Height) / 2);
        glow.Children.Add(glowImage);
        group.Children.Add(glow);

        var canvas = new Canvas { Width = halfW, Height = halfH };
        group.Children.Add(canvas);

        var labelText = new TextBlock
        {
            Text = modifier,
            FontFamily = Font,
            FontSize = s * 0.22,
            FontWeight = FontWeights.SemiBold,
            Foreground = GoldBrush,
            Opacity = 0.9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var labelBox = new Grid
        {
            Width = s * 1.8,
            Height = s * 0.36,
            IsHitTestVisible = false,
        };
        labelBox.Children.Add(labelText);

        var combo = new Canvas
        {
            Width = halfW,
            Height = halfH,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        AddDpadIndicator(combo, cx1, cy, s);
        AddAbxyIndicator(combo, cx2, cy, s);

        var half = new HalfVisual
        {
            Group = group,
            Glow = glow,
            Combo = combo,
            GroupScale = groupScale,
            LabelBox = labelBox,
            LabelText = labelText,
            Left = left,
        };
        _halves.Add(half);

        AddSlot(canvas, baseIndex + 0, cx1, cy - v, half, set, library);
        AddSlot(canvas, baseIndex + 1, cx1, cy + v, half, set, library);
        AddSlot(canvas, baseIndex + 2, cx1 - h, cy, half, set, library);
        AddSlot(canvas, baseIndex + 3, cx1 + h, cy, half, set, library);
        AddSlot(canvas, baseIndex + 4, cx2, cy - v, half, set, library);
        AddSlot(canvas, baseIndex + 5, cx2 - h, cy, half, set, library);
        AddSlot(canvas, baseIndex + 6, cx2 + h, cy, half, set, library);
        AddSlot(canvas, baseIndex + 7, cx2, cy + v, half, set, library);

        Canvas.SetLeft(labelBox, (cx1 + cx2) / 2 - s * 0.9);
        Canvas.SetTop(labelBox, s * 0.08);
        canvas.Children.Add(labelBox);
        canvas.Children.Add(combo);

        Canvas.SetLeft(group, x);
        Canvas.SetTop(group, top);
        Children.Add(group);
    }

    private static void AddDpadIndicator(Canvas canvas, double cx, double cy, double s)
    {
        // 十字轮廓，四个方位内部各一个小三角；加暗色底和渐变，增加一点浮起感。
        var shadow = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M9,2 H15 V8 H21 V14 H15 V20 H9 V14 H3 V8 H9 Z"),
            Fill = Freeze(Color.FromArgb(0x66, 0x00, 0x00, 0x00)),
            Stretch = Stretch.Fill,
            Width = s * 0.62,
            Height = s * 0.62,
        };
        Canvas.SetLeft(shadow, cx - s * 0.31);
        Canvas.SetTop(shadow, cy - s * 0.29);
        canvas.Children.Add(shadow);

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
        };
        Canvas.SetLeft(plus, cx - s * 0.31);
        Canvas.SetTop(plus, cy - s * 0.31);
        canvas.Children.Add(plus);

        AddTriangle(canvas, cx, cy, s, 0);
        AddTriangle(canvas, cx, cy, s, 90);
        AddTriangle(canvas, cx, cy, s, 180);
        AddTriangle(canvas, cx, cy, s, 270);
    }

    private static void AddTriangle(Canvas canvas, double cx, double cy, double s, double direction)
    {
        Point[] points = direction switch
        {
            0 => [new(cx - s * 0.06, cy - s * 0.14), new(cx + s * 0.06, cy - s * 0.14), new(cx, cy - s * 0.285)],
            90 => [new(cx + s * 0.14, cy - s * 0.06), new(cx + s * 0.14, cy + s * 0.06), new(cx + s * 0.285, cy)],
            180 => [new(cx - s * 0.06, cy + s * 0.14), new(cx + s * 0.06, cy + s * 0.14), new(cx, cy + s * 0.285)],
            _ => [new(cx - s * 0.14, cy - s * 0.06), new(cx - s * 0.14, cy + s * 0.06), new(cx - s * 0.285, cy)],
        };
        var triangle = new Polygon
        {
            Points = new PointCollection(points),
            Fill = Freeze(Color.FromArgb(0xEE, 0xF3, 0xE6, 0xD0)),
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 2.5,
                ShadowDepth = 1.2,
                Opacity = 0.95,
            },
        };
        canvas.Children.Add(triangle);
    }

    private static void AddAbxyIndicator(Canvas canvas, double cx, double cy, double s)
    {
        var d = s * 0.20;
        AddAbxyButton(canvas, "Y", cx, cy - d, s, Color.FromRgb(0xC9, 0x97, 0x1A));
        AddAbxyButton(canvas, "X", cx - d, cy, s, Color.FromRgb(0x2E, 0x6D, 0xA4));
        AddAbxyButton(canvas, "B", cx + d, cy, s, Color.FromRgb(0xB0, 0x3A, 0x2E));
        AddAbxyButton(canvas, "A", cx, cy + d, s, Color.FromRgb(0x2E, 0x8B, 0x57));
    }

    private static void AddAbxyButton(Canvas canvas, string text, double cx, double cy, double s, Color color)
    {
        var size = s * 0.22;
        var circle = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = Freeze(color),
            Stroke = Freeze(Color.FromArgb(0xCC, 0xFF, 0xF6, 0xE0)),
            StrokeThickness = Math.Max(1, s * 0.018),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(circle, cx - size / 2);
        Canvas.SetTop(circle, cy - size / 2);
        canvas.Children.Add(circle);

        var block = new TextBlock
        {
            Text = text,
            FontFamily = Font,
            FontSize = s * 0.18,
            FontWeight = FontWeights.Bold,
            Foreground = Freeze(Color.FromRgb(0xF3, 0xE6, 0xD0)),
            Width = size,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 2,
                ShadowDepth = 1,
                Opacity = 0.9,
            },
        };
        Canvas.SetLeft(block, cx - size / 2);
        Canvas.SetTop(block, cy - s * 0.115);
        canvas.Children.Add(block);
    }

    private void AddSlot(Canvas canvas, int index, double cx, double cy, HalfVisual half, FantasySet set, FantasyLibrary library)
    {
        var id = index < set.Slots.Count ? set.Slots[index] : null;
        var item = string.IsNullOrWhiteSpace(id) ? null : library.Items.FirstOrDefault(x => x.Id == id);
        var label = index < FantasySlots.Labels.Length ? FantasySlots.Labels[index] : index.ToString();
        var s = SlotSize;

        TextBlock? key = null;
        var grid = new Grid();
        if (item != null)
        {
            if (!string.IsNullOrWhiteSpace(item.ImageBase64))
            {
                var image = Decode(item.ImageBase64);
                if (image != null)
                {
                    grid.Children.Add(new Image
                    {
                        Source = image,
                        Stretch = Stretch.UniformToFill,
                        ClipToBounds = true,
                        Margin = new Thickness(s * 0.04),
                        SnapsToDevicePixels = true,
                    });
                }
                else
                {
                    grid.Children.Add(NameText(item));
                }
            }
            else
            {
                grid.Children.Add(NameText(item));
            }

            if (item.Action.Type is ActionType.Macro or ActionType.Text)
            {
                AddActionBadge(grid, s, item.Action.Type == ActionType.Macro ? "宏" : "文",
                    item.Action.Type == ActionType.Macro
                        ? Color.FromRgb(0x00, 0xAE, 0xEC)
                        : Color.FromRgb(0xD8, 0x1B, 0x60));
            }
        }
        else if (ShowSlotKeys)
        {
            key = new TextBlock
            {
                Text = label,
                FontFamily = Font,
                FontSize = KeyFontSize,
                Foreground = MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            grid.Children.Add(key);
        }

        var border = new Border
        {
            Width = s,
            Height = s,
            CornerRadius = new CornerRadius(Math.Max(4, s * 0.14)),
            Background = item != null ? FilledBg : StrongEmptySlots ? EmptyBgStrong : EmptyBg,
            BorderBrush = item != null ? FilledBorder : StrongEmptySlots ? EmptyBorderStrong : EmptyBorder,
            BorderThickness = new Thickness(1),
            Child = grid,
            Tag = index,
            SnapsToDevicePixels = true,
            ToolTip = item == null ? label : $"{label}\n{item.DisplayName}",
        };
        if (item != null && !string.IsNullOrWhiteSpace(item.ActionText))
            border.ToolTip = $"{label}\n{item.DisplayName}  ·  {item.ActionText}";

        Canvas.SetLeft(border, cx - s / 2);
        Canvas.SetTop(border, cy - s / 2);
        canvas.Children.Add(border);
        _entries.Add(new SlotEntry(border, half, item != null, key));
    }

    private static void AddActionBadge(Grid grid, double s, string text, Color background)
    {
        var badgeSize = s * 0.32;
        var badgeInset = s * 0.044;
        var bigCorner = s * 0.115;
        var shape = Geometry.Parse(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"M{bigCorner},0 L{badgeSize},0 L{badgeSize},{badgeSize - bigCorner} A{bigCorner},{bigCorner} 0 0 1 {badgeSize - bigCorner},{badgeSize} L0,{badgeSize} L0,{bigCorner} A{bigCorner},{bigCorner} 0 0 1 {bigCorner},0 Z"));
        var badge = new Grid
        {
            Width = badgeSize,
            Height = badgeSize,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, badgeInset, badgeInset),
            IsHitTestVisible = false,
        };
        badge.Children.Add(new System.Windows.Shapes.Path
        {
            Data = shape,
            Fill = Freeze(background),
        });
        badge.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = Font,
            FontSize = Math.Max(7, s * 0.18),
            FontWeight = FontWeights.SemiBold,
            Foreground = Freeze(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        grid.Children.Add(badge);
    }

    private TextBlock NameText(FantasyItem item) => new()
    {
        Text = string.IsNullOrWhiteSpace(item.Name) ? item.Action.Describe() : item.Name,
        FontFamily = Font,
        FontSize = NameFontSize,
        Foreground = TextBrush,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(2),
    };

    private void AddDivider(double x, double top, double halfH, double s, bool locked)
    {
        // 只处理中间的短分隔线：亮暖金圆头线 + 柔和外发光；锁图标不动。
        // 与锁锁定后的颜色一致（GoldBrush #F2C879）。
        var dividerColor = Color.FromRgb(0xF2, 0xC8, 0x79);
        var size = s * 0.55;
        var lineTop = top + halfH * 0.11;
        var lockTop = top + halfH - size * 0.9;
        var lineWidth = Math.Max(1.6, s * 0.045);
        // 与锁环顶部相接，不留缝。
        var lineHeight = Math.Max(8, lockTop - lineTop + 1);

        // 深色描边垫在亮线后面，只露出约 0.6px 边缘，不压暗线的本体。
        var outlineWidth = lineWidth + 1.2;
        var outline = new Rectangle
        {
            Width = outlineWidth,
            Height = lineHeight + 1.2,
            RadiusX = outlineWidth / 2,
            RadiusY = outlineWidth / 2,
            Fill = new SolidColorBrush(Color.FromArgb(0xC0, 0x08, 0x06, 0x04)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(outline, x - outlineWidth / 2);
        Canvas.SetTop(outline, lineTop - 0.6);
        Children.Add(outline);

        var line = new Rectangle
        {
            Width = lineWidth,
            Height = lineHeight,
            RadiusX = lineWidth / 2,
            RadiusY = lineWidth / 2,
            Fill = new SolidColorBrush(dividerColor),
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = dividerColor,
                BlurRadius = Math.Max(6, s * 0.12),
                ShadowDepth = 0,
                Opacity = 0.9,
            },
        };
        Canvas.SetLeft(line, x - lineWidth / 2);
        Canvas.SetTop(line, lineTop);
        Children.Add(line);

        var icon = new Canvas { Width = 24, Height = 24 };
        var body = new Rectangle
        {
            Width = 15,
            Height = 9,
            RadiusX = 2,
            RadiusY = 2,
            StrokeThickness = 0,
        };
        Canvas.SetLeft(body, 4.5);
        Canvas.SetTop(body, 11);
        var shackle = new System.Windows.Shapes.Path
        {
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = Brushes.Transparent,
        };
        icon.Children.Add(shackle);
        icon.Children.Add(body);

        var view = new Viewbox { Width = size * 0.64, Height = size * 0.64, Child = icon };
        var box = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = LockBg,
            BorderBrush = FilledBorder,
            BorderThickness = new Thickness(1.2),
            Child = view,
            Cursor = Cursors.Hand,
            ToolTip = "锁定/解锁热键栏（锁定后格子不可拖动编辑，点击格子触发动作）",
        };
        box.MouseLeftButtonDown += (_, e) => e.Handled = true;
        box.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            LockClicked?.Invoke(this, EventArgs.Empty);
        };
        Canvas.SetLeft(box, x - size / 2);
        Canvas.SetTop(box, top + halfH - size * 0.9);
        Children.Add(box);
        _lockShackle = shackle;
        _lockBody = body;
        SetLocked(locked);
    }

    private void AddFooter(double centerX, double y, double height, int setIndex, double s)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new TextBlock
        {
            Text = $"热键栏 {setIndex + 1}",
            FontFamily = Font,
            FontSize = FooterFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = GoldBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new Border
        {
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(7, 1, 7, 2),
            CornerRadius = new CornerRadius(4),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1.3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "RB",
                FontFamily = Font,
                FontSize = Math.Max(8, FooterFontSize - 2),
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
            },
        });

        var host = new Grid { Width = Width, Height = height, Background = Brushes.Transparent };
        host.Children.Add(row);
        Canvas.SetLeft(host, 0);
        Canvas.SetTop(host, y);
        Children.Add(host);
    }

    /// <summary>高亮当前激活的半栏；未按扳机时保持中性。</summary>
    public void SetActive(bool lt, bool rt, bool extensionPage = false, bool animate = true)
    {
        var any = lt || rt;
        foreach (var half in _halves)
        {
            var active = extensionPage || (half.Left ? lt : rt);
            var dim = !extensionPage && any && !active;
            var groupOpacity = active ? 1.0 : dim ? 0.34 : IdleOpacity;
            Animate(half.Group, OpacityProperty, groupOpacity, animate, 110);
            Animate(half.Glow, OpacityProperty, active ? 1.0 : 0.0, animate, 110);
            Animate(half.Combo, OpacityProperty, active ? 1.0 : 0.0, animate, 110);
            AnimateScale(half.GroupScale, active ? 1.035 : dim ? 0.975 : 1.0, animate, 110);
            half.LabelBox.Opacity = 1;
            half.LabelText.Foreground = active ? ActiveBorder : GoldBrush;

            foreach (var entry in _entries.Where(e => ReferenceEquals(e.Half, half)))
            {
                var selected = entry.Border.Tag is int index && index == _selected && _selected >= 0;
                entry.Border.BorderBrush = selected
                    ? AccentBorder
                    : active
                        ? ActiveBorder
                        : entry.HasItem
                            ? FilledBorder
                            : StrongEmptySlots ? EmptyBorderStrong : EmptyBorder;
                entry.Border.BorderThickness = new Thickness(selected ? 2 : active ? 1.6 : 1);
                if (entry.KeyText != null)
                    entry.KeyText.Foreground = active ? GoldBrush : MutedBrush;
            }
        }
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        if (_lockShackle == null || _lockBody == null) return;
        var brush = locked ? GoldBrush : MutedBrush;
        _lockShackle.Stroke = brush;
        _lockBody.Fill = brush;
        _lockShackle.Data = Geometry.Parse(locked
            ? "M8,11 V7 A4,4 0 0 1 16,7 V11"
            : "M8,11 V7 A4,4 0 0 1 16,7");
        if (_lockBody.Parent is Canvas icon && icon.Parent is Viewbox view && view.Parent is Border box)
        {
            box.BorderBrush = locked ? ActiveBorder : FilledBorder;
            box.ToolTip = locked
                ? "已锁定：格子不可拖动编辑，点击格子触发动作"
                : "已解锁：可拖动编辑，拖动顶部条移动悬浮窗";
        }
    }

    public void PlayTrigger(int index)
    {
        var entry = _entries.FirstOrDefault(e => e.Border.Tag is int i && i == index);
        if (entry == null) return;
        Point center;
        try
        {
            center = entry.Border.TranslatePoint(new Point(SlotSize / 2, SlotSize / 2), this);
        }
        catch
        {
            return;
        }

        var size = SlotSize * 0.82;
        var ring = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = Freeze(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            Stroke = null,
            StrokeThickness = 0,
            IsHitTestVisible = false,
            Opacity = 1.0,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var scale = new ScaleTransform(0.28, 0.28);
        ring.RenderTransform = scale;
        Canvas.SetLeft(ring, center.X - size / 2);
        Canvas.SetTop(ring, center.Y - size / 2);
        Children.Add(ring);

        var duration = TimeSpan.FromMilliseconds(240);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.05, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.05, duration) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => Children.Remove(ring);
        ring.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    public void Select(int index)
    {
        _selected = index;
        SetActive(false, false, false, false);
    }

    public void PlaySwitchIn(bool fromRight)
    {
        // 热键栏切换不再做位移动画，避免与缩放/窗口尺寸变化叠加产生抖动。
        RenderTransform = Transform.Identity;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    private static void Animate(UIElement element, DependencyProperty property, double to, bool animate, int ms)
    {
        if (!animate)
        {
            element.BeginAnimation(property, null);
            element.SetValue(property, to);
            return;
        }
        element.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateScale(ScaleTransform transform, double to, bool animate, int ms)
    {
        if (!animate)
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transform.ScaleX = to;
            transform.ScaleY = to;
            return;
        }
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static BitmapSource? _glowTexture;

    private static ImageSource GetGlowTexture()
    {
        if (_glowTexture != null) return _glowTexture;
        const int w = 160;
        const int h = 96;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var ny = (y + 0.5) / h * 2 - 1;
            for (var x = 0; x < w; x++)
            {
                var nx = (x + 0.5) / w * 2 - 1;
                var d = Math.Pow(Math.Abs(nx), 3.0) + Math.Pow(Math.Abs(ny), 3.0);
                var a = Math.Pow(Math.Max(0, 1 - d), 1.3) * 0x70;
                var i = (y * w + x) * 4;
                pixels[i] = 0x47;
                pixels[i + 1] = 0xB3;
                pixels[i + 2] = 0xF2;
                pixels[i + 3] = (byte)a;
            }
        }
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bitmap.Freeze();
        _glowTexture = bitmap;
        return bitmap;
    }

    private static BitmapImage? Decode(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}


