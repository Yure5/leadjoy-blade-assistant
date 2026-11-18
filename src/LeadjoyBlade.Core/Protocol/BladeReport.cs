using System.Buffers.Binary;

namespace LeadjoyBlade.Core.Protocol;

/// <summary>
/// 一帧手柄状态。协议中的 type=0x20 报文，内容为 15 字节：
/// [0] 子类型, [1..4] 32 位按键位图 (LE), [5..6] 左摇杆 X, [7..8] 左摇杆 Y,
/// [9..10] 右摇杆 X, [11..12] 右摇杆 Y, [13] 左扳机, [14] 右扳机。
/// </summary>
public sealed class BladeReport
{
    public BladeButtons Buttons { get; init; }
    public short LeftX { get; init; }
    public short LeftY { get; init; }
    public short RightX { get; init; }
    public short RightY { get; init; }
    public byte LeftTrigger { get; init; }
    public byte RightTrigger { get; init; }
    public byte SubType { get; init; }
    public byte[] RawContent { get; init; } = [];
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public float LeftXNorm => LeftX / 32767f;
    public float LeftYNorm => LeftY / 32767f;
    public float RightXNorm => RightX / 32767f;
    public float RightYNorm => RightY / 32767f;
    public float LeftTriggerNorm => LeftTrigger / 255f;
    public float RightTriggerNorm => RightTrigger / 255f;

    public bool IsPressed(BladeButtons button) => (Buttons & button) != 0;

    public static BladeReport Parse(ReadOnlySpan<byte> content)
    {
        if (content.Length < 15)
            throw new ArgumentException($"手柄报文长度不足：{content.Length} < 15", nameof(content));

        var mask = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(1, 4));
        return new BladeReport
        {
            SubType = content[0],
            Buttons = (BladeButtons)mask,
            LeftX = Axis(content.Slice(5, 2)),
            LeftY = InvertAxis(content.Slice(7, 2)),
            RightX = Axis(content.Slice(9, 2)),
            RightY = InvertAxis(content.Slice(11, 2)),
            LeftTrigger = content[13],
            RightTrigger = content[14],
            RawContent = content.ToArray(),
        };
    }

    /// <summary>摇杆为无符号 16 位，0x8000 是物理中点；转换为以 0 为中心的 short。</summary>
    private static short Axis(ReadOnlySpan<byte> value) =>
        (short)(BinaryPrimitives.ReadUInt16LittleEndian(value) - 32768);

    /// <summary>Y 轴反相：原始 0x0000（上）→ +32767，0x8000（中）→ 0，0xFFFF（下）→ -32768。</summary>
    private static short InvertAxis(ReadOnlySpan<byte> value) =>
        (short)Math.Clamp(32768 - BinaryPrimitives.ReadUInt16LittleEndian(value), short.MinValue, short.MaxValue);

    public static bool TryParse(ReadOnlySpan<byte> content, out BladeReport? report)
    {
        report = null;
        if (content.Length < 15) return false;
        // 合理性校验：子类型通常为 0/1，按键位图不应出现全 1。
        var mask = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(1, 4));
        if (mask == uint.MaxValue) return false;
        report = Parse(content);
        return true;
    }

    public string DescribePressed() =>
        string.Join(" + ", BladeButtonsExtensions.All.Where(IsPressed).Select(b => b.ToShortName()));
}
