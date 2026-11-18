using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LeadjoyBlade.Binput;

/// <summary>一轮圆盘的全部几何参数。所有数都从容器尺寸推, 不写死 360。</summary>
public readonly struct WheelGeom
{
    public readonly double Size;      // 圆盘直径(取容器的短边)
    public readonly double ROut;      // 扇环外沿半径
    public readonly double RIn;       // 扇环内沿半径(被中心圆咬掉一口之后)
    public readonly double RMid;      // 外圈 8 颗的质心所在半径
    public readonly double RCenter;   // 中心圆半径

    public WheelGeom(double size, double rOut, double rIn, double rMid, double rCenter)
    {
        Size = size; ROut = rOut; RIn = rIn; RMid = rMid; RCenter = rCenter;
    }
}

/// <summary>
/// 九键的摆位容器。**方阵那条路完全走 UniformGrid 原来的代码**(一个数都没改),
/// 只有轮盘那一档才改走极坐标。
/// <para>【为什么 9 颗键都铺满整个圆盘】每颗 WheelKey 自己在 0..Size 的坐标系里
/// 画出它那一瓣扇环, 圆心统一在 (Size/2, Size/2)。这样几何是精确的 —— 外弧、
/// 内弧都是完整的圆弧, 不会被任何矩形边界切平; 命中测试也只认 Path 填充的那一瓣。
/// 代价是 9 颗透明区域互相重叠, 但它们的可见/可点区域互不重叠, 不影响。</para>
/// </summary>
public class SlotPanel : UniformGrid
{
    /// <summary>圆盘离容器边留的一圈边(描边不被切)。</summary>
    public const double EDGE_PAD = 6;
    /// <summary>中心圆直径。用户拍板: 半径缩到原来的 2/3 → 144 * 2/3 = 96。</summary>
    public const double CENTER_D = 96;
    /// <summary>中心圆与扇环之间留的缝。</summary>
    public const double CENTER_GAP = 6;
    /// <summary>
    /// 瓣与瓣之间那条缝的**像素宽度**。
    /// <para>【为什么不是角度缝】以前两边都是过圆心的半径线, 夹角固定 1.5°,
    /// 于是缝宽 = r × 角度 —— 外面离得远、靠近中心离得近, 一条缝越往外越宽。
    /// 现在改成把边界线各自**平行外移** half 像素: 相邻两瓣共用同一个角度,
    /// 一条往左偏 half、一条往右偏 half, 两条线平行、间距恒为 SEAM_W,
    /// 而且都不再连向圆心。</para>
    /// </summary>
    public const double SEAM_W = 4.0;
    /// <summary>文字盒固定尺寸, 摆到每瓣的质心上。</summary>
    public const double TEXT_W = 150;
    public const double TEXT_H = 104;
    /// <summary>
    /// 轮盘下文字盒整体缩一点。左右两瓣(4/6)里, 水平排开的字母受的是扇环
    /// **径向厚度**的限制(不是弧向宽度), 扇环内收之后 "PQRS"/"WXYZ" 会顶到边。
    /// 缩文字盒比改字号干净 —— 方阵那条路的字号一个数都不动。
    /// </summary>
    public const double TEXT_SCALE = 0.82;

    bool _wheel;
    public bool Wheel
    {
        get => _wheel;
        set
        {
            if (_wheel == value) return;
            _wheel = value;
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    public static WheelGeom GeomOf(double w, double h)
    {
        double size = Math.Min(w, h);
        double rDisk = size / 2;                         // 圆盘半径 = 内切圆(窗口不变大)
        // 扇环外沿是**反推**出来的: 光标浮起那一下(FxPop)会以圆盘中心为轴把键
        // 放大到 ScaleTile × PopOver 倍, 外沿撑到 rOut × 倍率。让这个最大值
        // 刚好落在圆盘内(留 EDGE_PAD 的描边余量), 键就永远鼓不到盘子外面。
        // 静止时圆盘因此比按键外沿大出一圈 —— 那一圈留给页码点 / 标题沿弧写字。
        // 好处是圆盘始终等于容器尺寸, 不会被 360 的框裁成八边形。
        double rOut = (rDisk - EDGE_PAD) / PopReach();
        double rCenter = CENTER_D / 2;
        double rIn = rCenter + CENTER_GAP;
        if (rOut < rIn + 24) rOut = rIn + 24;            // 极小的容器也别把扇环压没
        return new WheelGeom(size, rOut, rIn, (rIn + rOut) / 2, rCenter);
    }

    /// <summary>按键"浮起 + 过冲"那一瞬的最大缩放倍率。</summary>
    public static double PopReach() => OverlayWindow.KeyPopReach;

    /// <summary>
    /// 九宫格编号 -> 圆盘上的方位角(度, 屏幕坐标: 0°=右, 90°=下)。
    /// 顺时针从上开始: 2 上 / 3 右上 / 6 右 / 9 右下 / 8 下 / 7 左下 / 4 左 / 1 左上。
    /// </summary>
    public static double AngleOf(int key) => key switch
    {
        1 => 225, 2 => 270, 3 => 315,
        4 => 180,            6 => 0,
        7 => 135, 8 => 90,  9 => 45,
        _ => 0,
    };

    /// <summary>扇环四个角倒圆的半径。和方阵键的圆角一个量级, 又不把 4px 瓣缝撑太开。</summary>
    public const double CORNER_R = 8;

    /// <summary>
    /// 画一块"两条平行边界 + 外弧 + 内弧"的扇环, **四个角都倒圆**。
    /// <para>做法: 每个角点沿两条边各退 cr 取截点, 中间用一条以原角点为控制点的
    /// 二次贝塞尔过渡。弧边上的截点按 <c>角度退 cr/r</c> 精确落在同心圆上,
    /// 不是拿弦长近似 —— 否则 ArcSegment 会被迫缩放半径, 圆弧就变形了。</para>
    /// <para>cr 按这一瓣的角跨度 / 径向厚度自动收小, 免得标点页那些 22.5° 的窄
    /// 半扇区两头一倒圆内弧就没了; 实在太窄就退回原来的尖角路径。</para>
    /// </summary>
    static Geometry SectorPlate(double size, double rIn, double rOut,
                                double a0, double a1, double half)
    {
        var o = new Point(size / 2, size / 2);
        double tIn = Math.Sqrt(Math.Max(1.0, rIn * rIn - half * half));
        double tOut = Math.Sqrt(Math.Max(1.0, rOut * rOut - half * half));
        double dIn = Math.Atan2(half, tIn), dOut = Math.Atan2(half, tOut);
        double span = (a1 - a0) - 2 * dIn;                 // 内弧真正可用的角跨度
        double cr = Math.Min(CORNER_R, Math.Max(0.0, rIn * span * 0.32));
        cr = Math.Min(cr, (rOut - rIn) / 3.0);

        var inL = EdgePoint(o, a0, +half, rIn);
        var outL = EdgePoint(o, a0, +half, rOut);
        var inR = EdgePoint(o, a1, -half, rIn);
        var outR = EdgePoint(o, a1, -half, rOut);

        var fig = new PathFigure { IsClosed = true, IsFilled = true };
        if (cr < 0.5)
        {
            fig.StartPoint = inL;
            fig.Segments.Add(new LineSegment(outL, true));
            fig.Segments.Add(new ArcSegment(outR, new Size(rOut, rOut), 0, false,
                                            SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(inR, true));
            fig.Segments.Add(new ArcSegment(inL, new Size(rIn, rIn), 0, false,
                                            SweepDirection.Counterclockwise, true));
        }
        else
        {
            double sIn = cr / rIn, sOut = cr / rOut;
            double angInL = a0 + dIn, angOutL = a0 + dOut;
            double angInR = a1 - dIn, angOutR = a1 - dOut;
            Point OnArc(double r, double t) =>
                new Point(o.X + r * Math.Cos(t), o.Y + r * Math.Sin(t));
            var u0 = new Vector(Math.Cos(a0), Math.Sin(a0));
            var u1 = new Vector(Math.Cos(a1), Math.Sin(a1));
            var p0 = OnArc(rIn, angInL + sIn);        // 内弧左截点(起点)
            var p1 = inL + u0 * cr;                    // 左边界下截点
            var p2 = outL - u0 * cr;                   // 左边界上截点
            var p3 = OnArc(rOut, angOutL + sOut);      // 外弧左截点
            var p4 = OnArc(rOut, angOutR - sOut);      // 外弧右截点
            var p5 = outR - u1 * cr;                   // 右边界上截点
            var p6 = inR + u1 * cr;                    // 右边界下截点
            var p7 = OnArc(rIn, angInR - sIn);         // 内弧右截点
            bool largeOut = (angOutR - sOut) - (angOutL + sOut) > Math.PI;
            bool largeIn = (angInR - sIn) - (angInL + sIn) > Math.PI;

            fig.StartPoint = p0;
            fig.Segments.Add(new QuadraticBezierSegment(inL, p1, true));
            fig.Segments.Add(new LineSegment(p2, true));
            fig.Segments.Add(new QuadraticBezierSegment(outL, p3, true));
            fig.Segments.Add(new ArcSegment(p4, new Size(rOut, rOut), 0, largeOut,
                                            SweepDirection.Clockwise, true));
            fig.Segments.Add(new QuadraticBezierSegment(outR, p5, true));
            fig.Segments.Add(new LineSegment(p6, true));
            fig.Segments.Add(new QuadraticBezierSegment(inR, p7, true));
            fig.Segments.Add(new ArcSegment(p0, new Size(rIn, rIn), 0, largeIn,
                                            SweepDirection.Counterclockwise, true));
        }
        var pg = new PathGeometry();
        pg.Figures.Add(fig);
        return pg;
    }

    /// <summary>
    /// 一颗键**自己**的形状, 在 0..Size 的坐标系里画(圆心 = Size/2, Size/2)。
    /// <para>中心那颗 = 正圆; 外圈 = 四条边围成的真扇环, 四角带圆角。
    /// 这里是**直接画出来的形状**, 没有 Clip、没有垫底板、没有方块参与。</para>
    /// </summary>
    public static Geometry PlateOf(int slot, double size, WheelGeom g)
    {
        var o = new Point(size / 2, size / 2);
        if (slot == 5)
            return new EllipseGeometry(o, g.RCenter, g.RCenter);

        double a = AngleOf(slot) * Math.PI / 180.0;
        // 边界线各平移 half 像素夹出等宽平行缝; 四个角交给 SectorPlate 倒圆。
        return SectorPlate(size, g.RIn, g.ROut,
                           a - Math.PI / 8.0, a + Math.PI / 8.0, SEAM_W / 2.0);
    }

    /// <summary>
    /// 设置页 / 窗口F 那一格的形状。slots 是它盖住的几个扇区(键号 1..9)。
    /// <para>big = 九格并成一整个大圆(「关于」页); blank = 中心那颗空白圆。</para>
    /// <para>连续几个扇区合成一瓣时**画一整块大扇环**, 不是把几瓣拼起来 ——
    /// 拼起来中间会留着瓣缝, 长键看着就像被切了几刀。</para>
    /// </summary>
    public static Geometry CellPlate(int[] slots, bool big, bool blank, double size, WheelGeom g)
    {
        var o = new Point(size / 2, size / 2);
        if (blank) return new EllipseGeometry(o, g.RCenter, g.RCenter);
        if (big) return new EllipseGeometry(o, g.ROut, g.ROut);
        if (slots == null || slots.Length == 0) return PlateOf(5, size, g);
        if (slots.Length == 1) return PlateOf(slots[0], size, g);

        double mn = 999, mx = -999;
        foreach (int s in slots) { double a = AngleOf(s); mn = Math.Min(mn, a); mx = Math.Max(mx, a); }
        if (mx - mn > 180)                       // 跨 0° 的那种(比如右列 3/6/9), 用并集兜底
        {
            var grp = new GeometryGroup();
            foreach (int s in slots) grp.Children.Add(PlateOf(s, size, g));
            return grp;
        }
        double a0 = (mn - 22.5) * Math.PI / 180.0;
        double a1 = (mx + 22.5) * Math.PI / 180.0;
        return SectorPlate(size, g.RIn, g.ROut, a0, a1, SEAM_W / 2.0);
    }

    /// <summary>
    /// 标点页(5×5 = 中心 1 + 内圈 8 + 外圈 16)那一格的形状。
    /// <para>ring 0 = 中心「类型」圆; 1 = 内弧→中弧的 8 瓣; 2 = 中弧→外弧的
    /// 16 个半扇区(每个扇区中间一条**径向**线把一瓣劈成两半, 正好 8×2=16)。</para>
    /// <para>sec: 0=右, 1=右下, … 7=右上(每 45°); half: 外圈那一瓣的左半 / 右半。</para>
    /// </summary>
    public static Geometry GridPlate(int ring, int sec, int half, double size, WheelGeom g)
    {
        var o = new Point(size / 2, size / 2);
        if (ring == 0) return new EllipseGeometry(o, g.RCenter, g.RCenter);
        double rMid = (g.RIn + g.ROut) / 2;
        double r0 = ring == 1 ? g.RIn : rMid + 2;
        double r1 = ring == 1 ? rMid - 2 : g.ROut;
        double c = sec * 45.0;
        double a0 = ring == 1 ? c - 22.5 : c + (half == 0 ? -22.5 : 0);
        double a1 = ring == 1 ? c + 22.5 : c + (half == 0 ? 0 : 22.5);
        return SectorPlate(size, r0, r1,
                           a0 * Math.PI / 180.0, a1 * Math.PI / 180.0, SEAM_W / 2.0);
    }

    /// <summary>
    /// 一条"平行于 ang 方向、但整体平移了 offset 像素"的边界线,
    /// 与半径 r 的圆的交点。t = √(r² − offset²), 于是端点仍落在同心圆弧上,
    /// 外弧 / 内弧的半径一个都不用改。
    /// </summary>
    static Point EdgePoint(Point o, double ang, double offset, double r)
    {
        double ux = Math.Cos(ang), uy = Math.Sin(ang);
        double nx = -uy, ny = ux;               // 指向角度增大的那一侧
        double t = Math.Sqrt(Math.Max(0.0, r * r - offset * offset));
        return new Point(o.X + t * ux + offset * nx, o.Y + t * uy + offset * ny);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (!_wheel) return base.MeasureOverride(constraint);
        double s = Math.Min(constraint.Width, constraint.Height);
        if (double.IsInfinity(s) || s <= 0) s = Math.Min(Width, Height);
        foreach (UIElement c in InternalChildren) c.Measure(new Size(s, s));
        return new Size(double.IsInfinity(constraint.Width) ? s : constraint.Width,
                        double.IsInfinity(constraint.Height) ? s : constraint.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_wheel) return base.ArrangeOverride(finalSize);
        double s = Math.Min(finalSize.Width, finalSize.Height);
        double ox = (finalSize.Width - s) / 2;
        double oy = (finalSize.Height - s) / 2;
        foreach (UIElement c in InternalChildren)
            c.Arrange(new Rect(ox, oy, s, s));
        return finalSize;
    }
}
