using System.Text.Json;

namespace LeadjoyBlade.Core.Devices;

/// <summary>记住上一次成功连接的 BLE 设备，用于“直连上次设备”。</summary>
public sealed record LastBleDevice(ulong Address, string? DeviceId, string Name);

public static class LastDeviceStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LeadjoyBlade", "last-ble.json");

    public static LastBleDevice? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<LastBleDevice>(File.ReadAllText(FilePath));
        }
        catch
        {
            return null;
        }
    }

    public static void Save(LastBleDevice device)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(device));
        }
        catch
        {
            // 忽略持久化失败
        }
    }
}
