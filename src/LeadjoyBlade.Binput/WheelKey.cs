using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LeadjoyBlade.Binput;

/// <summary>
/// 轮盘上的一颗键 —— **一等公民的扇环 / 圆控件**。
/// <para>里面只有两样东西: 一个 Path 画形状(填充 + 描边 + 命中), 一个文字盒摆在
/// 那一瓣的质心上。没有 Border、没有 Clip、没有"底下垫一张底板": 方块键在轮盘下
/// 是整颗 <c>Visibility=Collapsed</c> 的, 这里才是真正被看见、被点中、被动效作用的键。</para>
/// <para>文字用的是**原来那组 TextBlock**(Build 里造的 _key9Num / _key9Sub / 高光),
/// 切布局时在方块键和这里之间搬一次家, 所以填字、皮肤、光标那套逻辑一行都不用改。</para>
/// </summary>
public sealed class WheelKey : Grid
{
    readonly Path _shape = new Path
    {
        Stretch = Stretch.None,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        SnapsToDevicePixels = true,
    };
    readonly Canvas _layer = new Canvas { IsHitTestVisible = false };
    readonly Grid _text = new Grid
    {
        Width = SlotPanel.TEXT_W,
        Height = SlotPanel.TEXT_H,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
    };

    public int Slot { get; }
    /// <summary>设置页用: 这一格盖住的几个扇区(九键时为 null, 走单扇区那条老路)。</summary>
    int[] _slots;
    bool _big, _blank;
    /// <summary>标点页用: ring 0/1/2, 扇区 sec, 外圈左右半 half。-1 = 不是标点格。</summary>
    int _ring = -1, _gridSec, _gridHalf;

    public WheelKey(int slot)
    {
        Slot = slot;
        Background = null;                 // Grid 自己一像素都不画
        _text.RenderTransformOrigin = new Point(0.5, 0.5);
        _text.RenderTransform = new ScaleTransform(SlotPanel.TEXT_SCALE, SlotPanel.TEXT_SCALE);
        Children.Add(_shape);
        _layer.Children.Add(_text);
        Children.Add(_layer);
    }

    /// <summary>把方块键上那摞文字搬进来(先在原父容器里摘掉)。</summary>
    public void TakeText(FrameworkElement e)
    {
        _text.Children.Clear();
        if (e == null) return;
        if (e.Parent is Panel old) old.Children.Remove(e);
        else if (e.Parent is Border owner) owner.Child = null;   // 方块键的 Child 就是那摞文字
        e.HorizontalAlignment = HorizontalAlignment.Center;
        e.VerticalAlignment = VerticalAlignment.Center;
        _text.Children.Add(e);
    }

    /// <summary>切回方阵时把文字还回去。</summary>
    public FrameworkElement GiveText()
    {
        if (_text.Children.Count == 0) return null;
        var e = (FrameworkElement)_text.Children[0];
        _text.Children.Remove(e);
        e.HorizontalAlignment = HorizontalAlignment.Center;
        e.VerticalAlignment = VerticalAlignment.Center;
        return e;
    }

    /// <summary>设置页那一格: 盖住几个扇区 / 是不是九格并成的大圆 / 是不是中心空白圆。</summary>
    public void SetShape(int[] slots, bool big, bool blank)
    {
        _slots = slots; _big = big; _blank = blank;
    }

    /// <summary>标点页那一格: 中心 / 内圈 / 外圈半扇区。</summary>
    public void SetGridShape(int ring, int sec, int half)
    {
        _ring = ring; _gridSec = sec; _gridHalf = half;
    }

    /// <summary>按当前圆盘尺寸重画这一瓣, 并把文字盒挪到它的质心。</summary>
    public void Apply(double size, WheelGeom g)
    {
        if (size <= 0) return;
        _shape.Width = size;
        _shape.Height = size;
        if (_ring >= 0)
            _shape.Data = SlotPanel.GridPlate(_ring, _gridSec, _gridHalf, size, g);
        else
            _shape.Data = (_slots == null && !_big && !_blank)
                ? SlotPanel.PlateOf(Slot, size, g)
                : SlotPanel.CellPlate(_slots, _big, _blank, size, g);

        double a, r;
        if (_ring >= 0)
        {
            double rMid = (g.RIn + g.ROut) / 2;
            a = (_gridSec * 45.0 + (_ring == 2 ? (_gridHalf == 0 ? -11.25 : 11.25) : 0))
                * Math.PI / 180.0;
            r = _ring == 0 ? 0 : _ring == 1 ? (g.RIn + rMid) / 2 : (rMid + g.ROut) / 2;
        }
        else
        {
            int refSlot = (_slots == null && !_big && !_blank) ? Slot
                : (_big || _blank || _slots.Length == 0) ? 5
                : _slots[_slots.Length / 2];
            a = SlotPanel.AngleOf(refSlot) * Math.PI / 180.0;
            r = refSlot == 5 ? 0 : g.RMid;
        }
        double cx = size / 2 + r * Math.Cos(a);
        double cy = size / 2 + r * Math.Sin(a);
        // 【2.4.19 整页大圆要单独给文字盒】「关于」页是九格并成的一整块,
        // 格子里的标题按 3×3 的宽(约 306px)排; 可轮盘的文字盒只有 150×104,
        // 那一大段文字整块跑到盒子外, 圆里看着就是"文字被挡住了"(用户报过)。
        // 大圆页把盒子放大到圆的内接方块、撤掉 0.82 缩放, 再把文字收进盒里居中。
        double tw = _big ? size * 0.56 : SlotPanel.TEXT_W;
        double th = _big ? size * 0.56 : SlotPanel.TEXT_H;
        _text.Width = tw;
        _text.Height = th;
        _text.RenderTransform = _big
            ? new ScaleTransform(1, 1)
            : new ScaleTransform(SlotPanel.TEXT_SCALE, SlotPanel.TEXT_SCALE);
        if (_big && _text.Children.Count > 0)
            FitBigText((FrameworkElement)_text.Children[0], tw - 14, th - 8);
        Canvas.SetLeft(_text, cx - tw / 2);
        Canvas.SetTop(_text, cy - th / 2);
    }

    /// <summary>
    /// 把"一整块大格"里那些按 352 宽排的 TextBlock 收进圆里的文字盒:
    /// 去掉固定宽、改成居中折行, 真排一次版, 太高就半号半号缩到放得下。
    /// 走逻辑树(不是视觉树) —— 文字刚从方块键搬过来, 视觉树可能还没挂上。
    /// </summary>
    static void FitBigText(FrameworkElement e, double maxW, double maxH)
    {
        if (e == null) return;
        var box = new Size(Math.Max(10, maxW), double.PositiveInfinity);
        void Walk(object o)
        {
            if (o is TextBlock t)
            {
                t.Width = double.NaN;                       // 撤掉 306 的固定宽
                t.MaxWidth = maxW;
                t.TextAlignment = TextAlignment.Center;
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.TextWrapping = TextWrapping.Wrap;
                if (t.Text.Length > 0)
                {
                    t.InvalidateMeasure();
                    t.Measure(box);
                    double fs = t.FontSize;
                    while (t.DesiredSize.Height > maxH && fs > 9)
                    {
                        fs -= 0.5;
                        t.FontSize = fs;
                        t.InvalidateMeasure();
                        t.Measure(box);
                    }
                }
            }
            if (o is DependencyObject d)
                foreach (object c in LogicalTreeHelper.GetChildren(d)) Walk(c);
        }
        Walk(e);
    }

    public Brush ShapeFill
    {
        get => _shape.Fill;
        set => _shape.Fill = value;
    }
    public Brush ShapeStroke
    {
        get => _shape.Stroke;
        set => _shape.Stroke = value;
    }
    public double ShapeStrokeThickness
    {
        get => _shape.StrokeThickness;
        set => _shape.StrokeThickness = value;
    }
    public Geometry ShapeData => _shape.Data;
}
