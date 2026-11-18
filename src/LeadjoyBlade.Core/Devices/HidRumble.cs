using HidSharp;

namespace LeadjoyBlade.Core.Devices;

/// <summary>不依赖 XInput 的震动：DS4 通过 HID 输出报告直接驱动马达。</summary>
public static class HidRumble
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static bool TryVibrateDs4(double left, double right, int milliseconds)
    {
        foreach (var device in DeviceList.Local.GetHidDevices(0x054C))
        {
            var path = device.DevicePath ?? string.Empty;
            var maxOutput = 0;
            try { maxOutput = device.GetMaxOutputReportLength(); } catch { }
            DiagnosticsLog.Write("ble.log",
                $"rumble hid {device.VendorID:X4}:{device.ProductID:X4} out={maxOutput} path={path}");

            if (!device.TryOpen(out var stream))
            {
                DiagnosticsLog.Write("ble.log", $"rumble open failed {device.VendorID:X4}:{device.ProductID:X4}");
                continue;
            }

            try
            {
                var isBluetooth = path.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                                  path.Contains("00001124", StringComparison.OrdinalIgnoreCase);
                byte leftSpeed = (byte)Math.Clamp(left * 255.0, 0, 255);
                byte rightSpeed = (byte)Math.Clamp(right * 255.0, 0, 255);
                var length = isBluetooth ? Math.Max(78, maxOutput) : Math.Max(32, maxOutput);
                var report = new byte[length];

                if (isBluetooth)
                {
                    report[0] = 0x11;
                    report[1] = 0x80;
                    report[3] = 0x00;
                    report[4] = 0xFF;
                    report[5] = 0x00;
                    report[6] = 0xFF;
                    report[7] = rightSpeed;
                    report[8] = leftSpeed;
                    var crc = ComputeCrc(report, 74);
                    BitConverter.GetBytes(crc).CopyTo(report, 74);
                }
                else
                {
                    report[0] = 0x05;
                    report[1] = 0xFF;
                    report[2] = 0x00;
                    report[3] = 0x00;
                    report[4] = 0xFF;
                    report[5] = rightSpeed;
                    report[6] = leftSpeed;
                }

                stream.Write(report, 0, report.Length);
                DiagnosticsLog.Write("ble.log", $"rumble write {length}B bt={isBluetooth}");
                if (milliseconds > 0) Thread.Sleep(milliseconds);
                Array.Clear(report, 1, report.Length - 1);
                if (isBluetooth)
                {
                    var crc = ComputeCrc(report, 74);
                    BitConverter.GetBytes(crc).CopyTo(report, 74);
                }
                stream.Write(report, 0, report.Length);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Write("ble.log", $"rumble write failed: {ex.Message}");
            }
            finally
            {
                stream.Dispose();
            }
        }
        return false;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    private static uint ComputeCrc(byte[] data, int count)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = 0; i < count; i++)
            crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
