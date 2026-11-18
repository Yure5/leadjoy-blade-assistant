using System.Buffers;

namespace LeadjoyBlade.Core.Protocol;

/// <summary>
/// 与风刃手柄通信的命令构造器。命令 ID 来自 Android MsgPackCmd。
/// 手柄只需要收到“开启手柄上报”即可持续推送 0x20 状态帧，其余命令用于可选的板载配置。
/// </summary>
public static class BladeProtocol
{
    public const int CmdGamepadReport = 40;
    public const int CmdScreenInit = 1001;
    public const int CmdReadConfig = 1002;
    public const int CmdSetConfig = 1003;
    public const int CmdSaveAndApply = 1004;
    public const int CmdToggleMapping = 1005;

    public static byte[] EnableGamepadReport(bool enabled) =>
        MsgPack.PackMap(("c", CmdGamepadReport), ("s", enabled ? 1 : 0));

    public static byte[] ReadConfig() =>
        MsgPack.PackMap(("c", CmdReadConfig));

    public static byte[] SaveAndApply() =>
        MsgPack.PackMap(("c", CmdSaveAndApply));

    public static byte[] ToggleMapping(bool enabled) =>
        MsgPack.PackMap(("c", CmdToggleMapping), ("s", enabled ? 1 : 0));

    public static byte[] ScreenInit(int width, int height, int orientation) =>
        MsgPack.PackMap(("c", CmdScreenInit),
            ("s", new Dictionary<string, object?>
            {
                ["sw"] = width,
                ["sh"] = height,
                ["so"] = orientation,
            }));

    public static List<byte[]> BuildFrames(byte[] msgPackPayload, int mtu) =>
        FrameReader.Encode(msgPackPayload, mtu);

    /// <summary>把若干条命令编码成待发送帧（短帧）。</summary>
    public static List<byte[]> BuildCommandFrames(byte[] msgPackPayload, int mtu)
    {
        var result = new List<byte[]>();
        try
        {
            result.AddRange(FrameReader.Encode(msgPackPayload, mtu));
        }
        catch (InvalidOperationException)
        {
            // 超过单包上限时按最小 MTU 重新切分
            result.AddRange(FrameReader.Encode(msgPackPayload, 64));
        }
        return result;
    }
}
