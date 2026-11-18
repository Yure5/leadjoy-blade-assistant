using LeadjoyBlade.Core.Protocol;
using Windows.Devices.Bluetooth;

namespace LeadjoyBlade.Core.Devices;

public enum BladeTransportKind
{
    Ble,
    Hid,
    UsbBulk,
    XInput,
    RawGameController,
    SwitchHid,
    DualSenseHid,
    Simulator,
}

/// <summary>发现到的手柄候选设备。</summary>
public sealed record BladeDeviceCandidate(
    string Id,
    string Name,
    BladeTransportKind Kind,
    ulong BluetoothAddress = 0,
    int VendorId = 0,
    int ProductId = 0,
    string? Path = null,
    short Rssi = 0,
    BluetoothAddressType AddressType = BluetoothAddressType.Unspecified)
{
    public string RssiText => Kind == BladeTransportKind.Ble && Rssi != 0 ? $"{Rssi} dBm" : string.Empty;
    public string KindText => Kind switch
    {
        BladeTransportKind.Ble => "蓝牙 LE",
        BladeTransportKind.Hid => "USB HID",
        BladeTransportKind.UsbBulk => "USB 批量",
        BladeTransportKind.XInput => "XInput",
        BladeTransportKind.RawGameController => "HID 手柄",
        BladeTransportKind.SwitchHid => "HID 直读",
        BladeTransportKind.DualSenseHid => "DS5 HID",
        BladeTransportKind.Simulator => "模拟器",
        _ => Kind.ToString(),
    };

    public string ModeKey =>
        Name.Contains("XBOX", StringComparison.OrdinalIgnoreCase) ? "xbox" :
        Name.Contains("DS4", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("DS5", StringComparison.OrdinalIgnoreCase) ? "ds4" :
        Name.Contains("SWITCH", StringComparison.OrdinalIgnoreCase) ? "switch" :
        Kind == BladeTransportKind.Ble ? "ble" : "hid";
}

public interface IBladeTransport : IAsyncDisposable
{
    BladeTransportKind Kind { get; }
    string DisplayName { get; }
    bool IsConnected { get; }
    int Mtu { get; }

    event EventHandler<BladeReport>? ReportReceived;
    event EventHandler<Frame>? FrameReceived;
    event EventHandler<string>? Log;
    event EventHandler? Disconnected;
    /// <summary>电量百分比；-1 表示充电中/未知。</summary>
    event EventHandler<int>? BatteryChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task SendAsync(byte[] frame, CancellationToken cancellationToken = default);
}
