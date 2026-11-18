using System.Collections.Concurrent;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace LeadjoyBlade.Probe;

/// <summary>独立 BLE 广播扫描器，用于判断经典链路建立后适配器是否仍在扫描。</summary>
internal static class BleScanProbe
{
    public static async Task RunAsync(int seconds)
    {
        var seen = new ConcurrentDictionary<string, int>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
            AllowExtendedAdvertisements = true,
        };
        watcher.Received += (_, e) =>
        {
            var name = e.Advertisement.LocalName;
            var mf = string.Join(";", e.Advertisement.ManufacturerData.Select(m => $"{m.CompanyId:X4}={Hex(m.Data)}"));
            var sd = string.Join(";", e.Advertisement.DataSections.Select(d => $"{d.DataType:X4}={Hex(d.Data)}"));
            var key = string.IsNullOrWhiteSpace(name)
                ? $"(无名) {e.BluetoothAddress:X12}"
                : $"{name} [{e.BluetoothAddress:X12}]";
            if (mf.Length > 0) key += $" mf={mf}";
            if (sd.Length > 0) key += $" sd={sd}";
            seen.AddOrUpdate(key, 1, (_, v) => v + 1);
        };
        watcher.Start();
        Console.WriteLine($"BLE 扫描 {seconds} 秒（Active + 扩展广播）…");
        await Task.Delay(seconds * 1000);
        watcher.Stop();
        Console.WriteLine($"共 {seen.Count} 个设备：");
        foreach (var kv in seen.OrderByDescending(x => x.Value))
            Console.WriteLine($"  {kv.Key}  x{kv.Value}");
    }

    private static string Hex(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return Convert.ToHexString(bytes);
    }
}
