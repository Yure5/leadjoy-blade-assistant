namespace LeadjoyBlade.Binput;

/// <summary>
/// 键盘的**大布局** —— 设置 → 键盘 → 「键盘布局」那一格往里钻一层挑。
/// <para>它和 <see cref="KeyShape"/> 是两件事: KeyShape 只管圆角(方块 / 圆形),
/// PadLayout 管的是**整个键盘区是方阵还是圆盘**。默认方阵, 一个像素都不变。</para>
/// </summary>
public enum PadLayout
{
    Square = 0,     // 3×3 方阵(老样子)
    Wheel = 1,      // 内切圆盘: 中心一颗圆 + 外圈 8 瓣扇环
}

public static class PadLayouts
{
    public static bool Has(PadLayout l) => l == PadLayout.Square || l == PadLayout.Wheel;

    public static string Name(PadLayout l) => l == PadLayout.Wheel ? "轮盘" : "方阵";

    /// <summary>设置里那一格按一下就换到另一档。</summary>
    public static PadLayout Next(PadLayout l) => l == PadLayout.Wheel ? PadLayout.Square : PadLayout.Wheel;
}
