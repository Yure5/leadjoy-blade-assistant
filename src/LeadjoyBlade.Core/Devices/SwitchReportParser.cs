using System.Text;

namespace LeadjoyBlade.Core.Devices;

/// <summary>Switch 模式报文类型。</summary>
public enum SwitchReportKind
{
    Unknown = 0,
    /// <summary>0x30 / 0x21：任天堂全量报文，位于厂商页面 0xFF01，Windows 读不懂。</summary>
    Full = 1,
    /// <summary>0x3F：标准 HID 手柄集合，Windows 自己也能读。</summary>
    Simple = 2,
}

/// <summary>
/// 把一帧 Switch 报文解析成平台无关的手柄状态。
/// 关键点：<c>0x30/0x21</c> 与 <c>0x3F</c> 的字节位置和位序**完全不同**，绝不能共用一套偏移。
/// 依据：B 机（Win10 19045，057E:2009，蓝牙）300 秒 / 170061 帧实测 + HID 描述符，
/// 以及 A 机（Win11 22631）0x30 逐键实测。
/// </summary>
public struct SwitchPadState
{
    public SwitchReportKind Kind;
    public byte ReportId;

    public bool A, B, X, Y;
    public bool L1, R1, L2, R2;
    public bool Up, Down, Left, Right;
    public bool Home, Capture, L3, R3, Plus, Minus;

    /// <summary>-32768..32767，中心 0；向右/向上为正（已统一为软件内部约定）。</summary>
    public int LeftX, LeftY, RightX, RightY;

    public bool AnyButton =>
        A || B || X || Y || L1 || R1 || L2 || R2 ||
        Up || Down || Left || Right ||
        Home || Capture || L3 || R3 || Plus || Minus;
}

public static class SwitchReportParser
{
    public const int MinLength = 12;

    public static SwitchPadState Parse(byte[] buffer, int length)
    {
        var state = new SwitchPadState();
        if (buffer == null || length < MinLength) return state;

        state.ReportId = buffer[0];
        switch (buffer[0])
        {
            case 0x30:
            case 0x21:
                state.Kind = SwitchReportKind.Full;
                ParseFull(ref state, buffer, length);
                break;
            case 0x3F:
                state.Kind = SwitchReportKind.Simple;
                ParseSimple(ref state, buffer, length);
                break;
            default:
                state.Kind = SwitchReportKind.Unknown;
                break;
        }
        return state;
    }

    /// <summary>
    /// 0x30 / 0x21 全量报文（A 机实测基线）。
    /// b3：Y=01 X=02 B=04 A=08 SR=10 SL=20 R=40 ZR=80
    /// b4：Minus=01 Plus=02 RStick=04 LStick=08 Home=10 Capture=20（0x80 常驻，非按键）
    /// b5：Down=01 Up=02 Right=04 Left=08 SR=10 SL=20 L=40 ZL=80
    /// b6..b11：两组 12 位打包摇杆，中心 2048
    /// </summary>
    private static void ParseFull(ref SwitchPadState state, byte[] b, int length)
    {
        var right = b[3];
        var shared = b[4];
        var left = b[5];

        state.Y = (right & 0x01) != 0;
        state.X = (right & 0x02) != 0;
        state.B = (right & 0x04) != 0;
        state.A = (right & 0x08) != 0;
        state.R1 = (right & 0x40) != 0;
        state.R2 = (right & 0x80) != 0;

        state.Minus = (shared & 0x01) != 0;
        state.Plus = (shared & 0x02) != 0;
        state.R3 = (shared & 0x04) != 0;
        state.L3 = (shared & 0x08) != 0;
        state.Home = (shared & 0x10) != 0;
        state.Capture = (shared & 0x20) != 0;

        state.Down = (left & 0x01) != 0;
        state.Up = (left & 0x02) != 0;
        state.Right = (left & 0x04) != 0;
        state.Left = (left & 0x08) != 0;
        state.L1 = (left & 0x40) != 0;
        state.L2 = (left & 0x80) != 0;

        if (length < 12) return;

        var lx = b[6] | ((b[7] & 0x0F) << 8);
        var ly = (b[7] >> 4) | (b[8] << 4);
        var rx = b[9] | ((b[10] & 0x0F) << 8);
        var ry = (b[10] >> 4) | (b[11] << 4);

        // A 机实测：X 向右增大；Y 向上增大（不需要取反）。
        state.LeftX = Scale12(lx);
        state.LeftY = Scale12(ly);
        state.RightX = Scale12(rx);
        state.RightY = Scale12(ry);
    }

    /// <summary>
    /// 0x3F 标准手柄报文（B 机 300 秒实测 + HID 描述符）。
    /// b1：B=01 A=02 Y=04 X=08 LB=10 RB=20 LT=40 RT=80
    /// b2：Minus=01 Plus=02 L3=04 R3=08 Home=10 Capture=20
    /// b3：hat 0..7（0=上，顺时针），8=居中
    /// b4..b11：X/Y/Rx/Ry 各 16 位小端，中心 0x8000
    /// </summary>
    private static void ParseSimple(ref SwitchPadState state, byte[] b, int length)
    {
        var keys1 = b[1];
        var keys2 = b[2];

        state.B = (keys1 & 0x01) != 0;
        state.A = (keys1 & 0x02) != 0;
        state.Y = (keys1 & 0x04) != 0;
        state.X = (keys1 & 0x08) != 0;
        state.L1 = (keys1 & 0x10) != 0;
        state.R1 = (keys1 & 0x20) != 0;
        state.L2 = (keys1 & 0x40) != 0;
        state.R2 = (keys1 & 0x80) != 0;

        state.Minus = (keys2 & 0x01) != 0;
        state.Plus = (keys2 & 0x02) != 0;
        state.L3 = (keys2 & 0x04) != 0;
        state.R3 = (keys2 & 0x08) != 0;
        state.Home = (keys2 & 0x10) != 0;
        state.Capture = (keys2 & 0x20) != 0;

        switch (b[3] & 0x0F)
        {
            case 0: state.Up = true; break;
            case 1: state.Up = true; state.Right = true; break;
            case 2: state.Right = true; break;
            case 3: state.Right = true; state.Down = true; break;
            case 4: state.Down = true; break;
            case 5: state.Down = true; state.Left = true; break;
            case 6: state.Left = true; break;
            case 7: state.Left = true; state.Up = true; break;
            default: break; // 8 = 居中
        }

        if (length < 12) return;

        // 标准 HID 约定：X 向右为正，Y 向下为正 → 统一成「向上为正」需取反。
        state.LeftX = Scale16(b[4] | (b[5] << 8));
        // Scale16 可能返回 -32768；直接取反会得到 +32768，随后转 short 会回卷成
        // -32768（表现为摇杆推到顶突然跳到底）。这里把取反结果夹回 short 范围。
        state.LeftY = Math.Clamp(-Scale16(b[6] | (b[7] << 8)), short.MinValue, short.MaxValue);
        state.RightX = Scale16(b[8] | (b[9] << 8));
        state.RightY = Math.Clamp(-Scale16(b[10] | (b[11] << 8)), short.MinValue, short.MaxValue);
    }

    private static int Scale12(int value) =>
        Math.Clamp((value - 2048) * 16, short.MinValue, short.MaxValue);

    // 0x3F 报文的轴本身就是完整 16 位（0x0000–0xFFFF，中心 0x8000）。
    // 不能再乘 2：否则摇杆走到一半就撞上 ±32767，后半程报文仍在变但输出被截断。
    private static int Scale16(int value) =>
        Math.Clamp(value - 0x8000, short.MinValue, short.MaxValue);

    /// <summary>便于写入日志的一行摘要。</summary>
    public static string Describe(SwitchPadState state)
    {
        if (state.Kind == SwitchReportKind.Unknown)
            return "UNKNOWN id=0x" + state.ReportId.ToString("X2");

        var builder = new StringBuilder();
        builder.Append("0x").Append(state.ReportId.ToString("X2")).Append(' ');
        if (state.A) builder.Append("A ");
        if (state.B) builder.Append("B ");
        if (state.X) builder.Append("X ");
        if (state.Y) builder.Append("Y ");
        if (state.L1) builder.Append("LB ");
        if (state.R1) builder.Append("RB ");
        if (state.L2) builder.Append("LT ");
        if (state.R2) builder.Append("RT ");
        if (state.Up) builder.Append("Up ");
        if (state.Down) builder.Append("Down ");
        if (state.Left) builder.Append("Left ");
        if (state.Right) builder.Append("Right ");
        if (state.Home) builder.Append("Home ");
        if (state.Capture) builder.Append("Capture ");
        if (state.L3) builder.Append("L3 ");
        if (state.R3) builder.Append("R3 ");
        if (state.Plus) builder.Append("Plus ");
        if (state.Minus) builder.Append("Minus ");
        if (!state.AnyButton) builder.Append("(none) ");
        builder.Append("| LX=").Append(state.LeftX).Append(" LY=").Append(state.LeftY);
        builder.Append(" RX=").Append(state.RightX).Append(" RY=").Append(state.RightY);
        return builder.ToString();
    }
}
