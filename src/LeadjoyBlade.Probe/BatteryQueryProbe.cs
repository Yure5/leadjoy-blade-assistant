using LeadjoyBlade.Core.Devices;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.Probe;

/// <summary>向 _1B 发送一组 msgpack 命令，打印所有原始通知，用于寻找电量查询命令。</summary>
internal static class BatteryQueryProbe
{
    public static async Task RunAsync(string addressText, string commandsText)
    {
        if (!ulong.TryParse(addressText, System.Globalization.NumberStyles.HexNumber, null, out var address))
        {
            Console.WriteLine("用法：LeadjoyProbe.exe query <地址> <c1,c2,...>");
            return;
        }

        var commands = commandsText.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var n) ? n : -1)
            .Where(n => n >= 0)
            .ToArray();

        await using var transport = new BleBladeTransport(address, "Blade Plus_1B");
        transport.Log += (_, msg) => Console.WriteLine($"[BLE] {msg}");
        transport.RawNotify += (_, bytes) => Console.WriteLine($"[notify] {Convert.ToHexString(bytes)}");
        try
        {
            await transport.ConnectAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"连接失败：{ex.Message}");
            return;
        }

        foreach (var command in commands)
        {
            Console.WriteLine($"[send] c={command}");
            var payload = MsgPack.PackMap(("c", command));
            foreach (var frame in BladeProtocol.BuildCommandFrames(payload, transport.Mtu))
                await transport.SendAsync(frame);
            await Task.Delay(800);
        }

        Console.WriteLine("监听 5 秒…");
        await Task.Delay(5000);
    }
}
