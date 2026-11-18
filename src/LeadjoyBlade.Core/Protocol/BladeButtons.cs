namespace LeadjoyBlade.Core.Protocol;

/// <summary>
/// 风刃系列手柄的全部物理按键。位序与设备上报的 32 位按键位图一一对应。
/// 数据来源：Leadjoy Android 1.1.9 的 com.leadjoy.mapper.model.GamepadKey。
/// </summary>
[Flags]
public enum BladeButtons : uint
{
    None = 0,
    A = 1u << 0,
    B = 1u << 1,
    C = 1u << 2,
    X = 1u << 3,
    Y = 1u << 4,
    Z = 1u << 5,
    L1 = 1u << 6,
    R1 = 1u << 7,
    L2 = 1u << 8,
    R2 = 1u << 9,
    Select = 1u << 10,
    Start = 1u << 11,
    Home = 1u << 12,
    L3 = 1u << 13,
    R3 = 1u << 14,
    Capture = 1u << 15,
    Up = 1u << 16,
    Down = 1u << 17,
    Left = 1u << 18,
    Right = 1u << 19,
    Menu = 1u << 20,
    Mode = 1u << 21,
    Fn = 1u << 22,
    M1 = 1u << 23,
    M2 = 1u << 24,
    M3 = 1u << 25,
    M4 = 1u << 26,
    M5 = 1u << 27,
    M6 = 1u << 28,
    M7 = 1u << 29,
    M8 = 1u << 30,
    Power = 1u << 31,
}

public static class BladeButtonsExtensions
{
    /// <summary>按物理位置排序的全部按键，用于 UI 与测试页。</summary>
    public static readonly BladeButtons[] All =
    [
        BladeButtons.A, BladeButtons.B, BladeButtons.X, BladeButtons.Y,
        BladeButtons.C, BladeButtons.Z,
        BladeButtons.Up, BladeButtons.Down, BladeButtons.Left, BladeButtons.Right,
        BladeButtons.L1, BladeButtons.R1, BladeButtons.L2, BladeButtons.R2,
        BladeButtons.L3, BladeButtons.R3,
        BladeButtons.M1, BladeButtons.M2,
        BladeButtons.Select, BladeButtons.Start, BladeButtons.Home, BladeButtons.Capture,
        BladeButtons.Fn,
    ];

    public static string ToDisplayName(this BladeButtons button) => button switch
    {
        BladeButtons.A => "A",
        BladeButtons.B => "B",
        BladeButtons.C => "C",
        BladeButtons.X => "X",
        BladeButtons.Y => "Y",
        BladeButtons.Z => "Z",
        BladeButtons.L1 => "LB (L1)",
        BladeButtons.R1 => "RB (R1)",
        BladeButtons.L2 => "LT (L2)",
        BladeButtons.R2 => "RT (R2)",
        BladeButtons.Select => "Select / 视图",
        BladeButtons.Start => "Start / 菜单",
        BladeButtons.Home => "Home 主键",
        BladeButtons.L3 => "L3 左摇杆按下",
        BladeButtons.R3 => "R3 右摇杆按下",
        BladeButtons.Capture => "Capture 截图键",
        BladeButtons.Up => "十字键 ↑",
        BladeButtons.Down => "十字键 ↓",
        BladeButtons.Left => "十字键 ←",
        BladeButtons.Right => "十字键 →",
        BladeButtons.Menu => "Menu 菜单键",
        BladeButtons.Mode => "Mode 模式键",
        BladeButtons.Fn => "Fn 功能键",
        BladeButtons.M1 => "L4 左背键",
        BladeButtons.M2 => "R4 右背键",
        BladeButtons.M3 => "M3 背键",
        BladeButtons.M4 => "M4 背键",
        BladeButtons.M5 => "M5 背键",
        BladeButtons.M6 => "M6 背键",
        BladeButtons.M7 => "M7 背键",
        BladeButtons.M8 => "M8 背键",
        BladeButtons.Power => "Power 电源键",
        _ => button.ToString(),
    };

    public static string ToShortName(this BladeButtons button) => button switch
    {
        BladeButtons.Select => "SELECT",
        BladeButtons.Start => "START",
        BladeButtons.Home => "HOME",
        BladeButtons.Capture => "CAPTURE",
        BladeButtons.Menu => "MENU",
        BladeButtons.Mode => "MODE",
        BladeButtons.Fn => "FN",
        BladeButtons.Power => "POWER",
        BladeButtons.L1 => "LB",
        BladeButtons.R1 => "RB",
        BladeButtons.L2 => "LT",
        BladeButtons.R2 => "RT",
        BladeButtons.L3 => "L3",
        BladeButtons.R3 => "R3",
        BladeButtons.M1 => "L4",
        BladeButtons.M2 => "R4",
        BladeButtons.Up => "↑",
        BladeButtons.Down => "↓",
        BladeButtons.Left => "←",
        BladeButtons.Right => "→",
        _ => button.ToString(),
    };
}
