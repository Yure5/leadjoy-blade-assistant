using System.Diagnostics;
using LeadjoyBlade.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace LeadjoyBlade.Core.Devices;

/// <summary>
/// 风刃系列的 BLE GATT 传输。服务 00008650，写特征 00008655，通知特征 0000865F。
/// 与 Android 端 BLEManager 使用完全相同的 UUID。
/// </summary>
public sealed class BleBladeTransport : IBladeTransport
{
    public static readonly Guid ServiceUuid = new("00008650-0000-1000-8000-00805f9b34fb");
    public static readonly Guid WriteCharUuid = new("00008655-0000-1000-8000-00805f9b34fb");
    public static readonly Guid NotifyCharUuid = new("0000865f-0000-1000-8000-00805f9b34fb");

    private readonly ulong _address;
    private readonly string _name;
    private readonly BluetoothAddressType _addressType;
    private readonly string? _deviceId;
    private readonly FrameReader _reader = new();

    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _writeChar;
    private GattCharacteristic? _notifyChar;
    private GattSession? _session;
    private int _notifyCount;
    private int _lastBattery = int.MinValue;
    private int _ff11Count;
    private int _gamepadCount;
    private long _lastGamepadTicks;
    private double _sumIntervalMs;
    private double _minIntervalMs = double.MaxValue;
    private double _maxIntervalMs;
    private readonly Stopwatch _rateWindow = Stopwatch.StartNew();

    public BleBladeTransport(ulong address, string name,
        BluetoothAddressType addressType = BluetoothAddressType.Unspecified, string? deviceId = null)
    {
        _address = address;
        _name = name;
        _addressType = addressType;
        _deviceId = deviceId;
    }

    public BladeTransportKind Kind => BladeTransportKind.Ble;
    public string DisplayName => string.IsNullOrWhiteSpace(_name) ? $"BLE {_address:X12}" : _name;
    public bool IsConnected { get; private set; }
    public int Mtu { get; private set; } = 185;

    public event EventHandler<BladeReport>? ReportReceived;
    public event EventHandler<Frame>? FrameReceived;
    public event EventHandler<byte[]>? RawNotify;
    public event EventHandler<string>? Log;
    public event EventHandler? Disconnected;
    public event EventHandler<int>? BatteryChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await DisposeAsync().ConfigureAwait(false);

        Log?.Invoke(this, $"正在连接蓝牙设备 {_address:X12} …");
        DiagnosticsLog.Write("ble.log", $"connect start {_address:X12} addrType={_addressType} deviceId={_deviceId}");
        var sw = Stopwatch.StartNew();
        if (!string.IsNullOrWhiteSpace(_deviceId))
        {
            try
            {
                _device = await BluetoothLEDevice.FromIdAsync(_deviceId).AsTask(cancellationToken).ConfigureAwait(false);
                if (_device != null) DiagnosticsLog.Write("ble.log", $"paired device connected by id {_deviceId}");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Write("ble.log", $"FromIdAsync failed: {ex.Message}");
            }
        }

        _device ??= await BluetoothLEDevice.FromBluetoothAddressAsync(_address, _addressType).AsTask(cancellationToken).ConfigureAwait(false);
        if (_device == null && _addressType != BluetoothAddressType.Unspecified)
        {
            DiagnosticsLog.Write("ble.log", $"addrType {_addressType} 未找到设备，回退 Unspecified");
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_address).AsTask(cancellationToken).ConfigureAwait(false);
        }
        if (_device == null)
            throw new InvalidOperationException("广播已消失，无法创建设备对象。请重新按 Home + Y，并立即点“连接选中设备”。");
        DiagnosticsLog.Write("ble.log", $"phase open +{sw.ElapsedMilliseconds}ms");

        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        // 风刃必须先完成 BLE 绑定再枚举 0x8650，否则 GATT 会挂起 15 秒后 Unreachable。
        // 只走自定义配对的 ConfirmOnly，最多 8 秒；不再走默认配对的 10 秒白等。
        var paired = false;
        if (_name.Contains("_1B", StringComparison.OrdinalIgnoreCase))
        {
            paired = await TryPairQuickAsync(cancellationToken).ConfigureAwait(false);
            DiagnosticsLog.Write("ble.log", $"phase quick-pair +{sw.ElapsedMilliseconds}ms paired={paired}");
            if (paired)
            {
                try
                {
                    _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                    _device.Dispose();
                }
                catch { }

                _device = !string.IsNullOrWhiteSpace(_deviceId)
                    ? await BluetoothLEDevice.FromIdAsync(_deviceId).AsTask(cancellationToken).ConfigureAwait(false)
                    : await BluetoothLEDevice.FromBluetoothAddressAsync(_address, _addressType)
                        .AsTask(cancellationToken).ConfigureAwait(false);
                if (_device == null)
                    throw new InvalidOperationException("BLE 绑定成功，但重新打开设备失败。请重新开机后重试。");
                _device.ConnectionStatusChanged += OnConnectionStatusChanged;
                DiagnosticsLog.Write("ble.log", $"device reopened after quick pairing {_address:X12}");
            }
        }

        if (_name.Contains("_1B", StringComparison.OrdinalIgnoreCase) && !paired)
            throw new InvalidOperationException("BLE 绑定未完成（手柄可能尚未就绪），等待下一次广播后重试。");

        try
        {
            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(cancellationToken).ConfigureAwait(false);
            _session.MaintainConnection = true;
            if (_session.MaxPduSize > 23) Mtu = _session.MaxPduSize - 3;
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"GattSession 不可用（忽略）：{ex.Message}");
        }
        DiagnosticsLog.Write("ble.log", $"phase session +{sw.ElapsedMilliseconds}ms");

        GattDeviceServicesResult? services = null;
        Exception? firstError = null;
        try
        {
            services = await WithTimeout(_device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(), 12)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            firstError = ex;
        }

        if (services == null || services.Status != GattCommunicationStatus.Success)
        {
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            try
            {
                services = await WithTimeout(_device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(), 12)
                    .ConfigureAwait(false);
                firstError = null;
            }
            catch (Exception ex)
            {
                firstError = ex;
                services = null;
            }
        }

        // 缓存的设备 ID 可能指向旧的随机地址；失败时退回当前广播地址重建对象。
        if ((services == null || services.Status != GattCommunicationStatus.Success) &&
            !string.IsNullOrWhiteSpace(_deviceId))
        {
            try
            {
                _session?.Dispose();
                _session = null;
                _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                _device.Dispose();
            }
            catch { }

            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_address, _addressType)
                .AsTask(cancellationToken).ConfigureAwait(false);
            _device ??= await BluetoothLEDevice.FromBluetoothAddressAsync(_address)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (_device != null)
            {
                _device.ConnectionStatusChanged += OnConnectionStatusChanged;
                try
                {
                    services = await WithTimeout(
                        _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(), 12).ConfigureAwait(false);
                    firstError = null;
                    DiagnosticsLog.Write("ble.log", $"retry by address {_address:X12} -> {services.Status}");
                }
                catch (Exception ex)
                {
                    firstError = ex;
                    services = null;
                }
            }
        }

        if (services == null)
        {
            DiagnosticsLog.Write("ble.log", $"gatt fail {_address:X12}: {firstError}");
            throw new InvalidOperationException(
                $"连接 GATT 失败：{firstError?.Message}。请确认手柄已按 Home + Y 进入 G-Touch 模式，并在 Windows 蓝牙设置中完成配对。", firstError);
        }

        if (services.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException(
                $"枚举 GATT 服务失败：{services.Status}。请确认已进入 G-Touch 模式（Home + Y）并完成蓝牙配对。");

        _service = services.Services.FirstOrDefault(s => s.Uuid == ServiceUuid);
        DiagnosticsLog.Write("ble.log",
            $"services {_address:X12} status={services.Status}: {string.Join(",", services.Services.Select(s => s.Uuid))}");
        DiagnosticsLog.Write("ble.log", $"phase services +{sw.ElapsedMilliseconds}ms");

        // 诊断：枚举其它服务里可读的特征值，用于定位电量等状态特征。
        try
        {
            foreach (var svc in services.Services.Where(s => s.Uuid != ServiceUuid))
            {
                var otherChars = await WithTimeout(svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(), 5)
                    .ConfigureAwait(false);
                foreach (var ch in otherChars.Characteristics)
                {
                    var value = string.Empty;
                    if ((ch.CharacteristicProperties & GattCharacteristicProperties.Read) != 0)
                    {
                        try
                        {
                            var read = await WithTimeout(ch.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(), 3)
                                .ConfigureAwait(false);
                            if (read.Status == GattCommunicationStatus.Success && read.Value != null)
                                value = BufferToHex(read.Value);
                        }
                        catch { }
                    }
                    DiagnosticsLog.Write("ble.log",
                        $"gatt {svc.Uuid} char {ch.Uuid} props={ch.CharacteristicProperties} val={value}");
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("ble.log", $"gatt diagnostic failed: {ex.Message}");
        }

        if (_service == null)
        {
            var found = string.Join(", ", services.Services.Select(s => s.Uuid));
            throw new InvalidOperationException(
                $"未找到 0x8650 厂商服务。当前设备服务：{found}。若这是 Xbox 控制器，请按 Home + Y 切换到 G-Touch 模式后重试。");
        }

        var chars = await WithTimeout(_service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(), 12)
            .ConfigureAwait(false);
        if (chars.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException(
                $"枚举 GATT 特征失败：{chars.Status}。若为 AccessDenied，请删除 Windows 蓝牙里旧的 " +
                "Blade Plus_1B 配对后重新连接；也可切到 XInput 模式重试。");
        DiagnosticsLog.Write("ble.log", $"phase chars +{sw.ElapsedMilliseconds}ms");

        _writeChar = chars.Characteristics.FirstOrDefault(c => c.Uuid == WriteCharUuid);
        _notifyChar = chars.Characteristics.FirstOrDefault(c => c.Uuid == NotifyCharUuid);
        if (_writeChar == null || _notifyChar == null)
            throw new InvalidOperationException("未找到 0x8655 写特征或 0x865F 通知特征");

        _notifyChar.ValueChanged += OnValueChanged;

        var cccd = await WithTimeout(_notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(), 12).ConfigureAwait(false);

        if (cccd != GattCommunicationStatus.Success)
        {
            Log?.Invoke(this, $"订阅 Notify 失败（{cccd}），尝试 Indicate…");
            cccd = await WithTimeout(_notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Indicate).AsTask(), 12).ConfigureAwait(false);
        }

        if (cccd != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"订阅通知失败：{cccd}");

        // 0xFF10 / 0xFF11 是厂商状态/电量通道，手机 App 很可能从这里读电量。
        try
        {
            var ffService = services.Services.FirstOrDefault(
                s => s.Uuid == new Guid("0000ff10-0000-1000-8000-00805f9b34fb"));
            if (ffService != null)
            {
                var ffChars = await WithTimeout(
                    ffService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(), 5).ConfigureAwait(false);
                var ff11 = ffChars.Characteristics.FirstOrDefault(
                    c => c.Uuid == new Guid("0000ff11-0000-1000-8000-00805f9b34fb"));
                if (ff11 != null)
                {
                    ff11.ValueChanged += OnFf11ValueChanged;
                    var ffCccd = await WithTimeout(ff11.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(), 5).ConfigureAwait(false);
                    DiagnosticsLog.Write("ble.log", $"ff11 subscribed {ffCccd}");
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("ble.log", $"ff11 subscribe failed: {ex.Message}");
        }

        IsConnected = true;
        DiagnosticsLog.Write("ble.log", $"phase cccd +{sw.ElapsedMilliseconds}ms");
        DiagnosticsLog.Write("ble.log", $"connected {_address:X12} service={ServiceUuid} write={WriteCharUuid} notify={NotifyCharUuid}");
        Log?.Invoke(this, $"蓝牙已连接，MTU={Mtu}");
    }

    /// <summary>只做一次自定义 BLE 配对（自动确认 ConfirmOnly / 固定 PIN），超时 8 秒。</summary>
    private async Task<bool> TryPairQuickAsync(CancellationToken cancellationToken)
    {
        if (_device == null) return false;
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(_device.DeviceId)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (info.Pairing.IsPaired) return true;

            var custom = info.Pairing.Custom;
            void Handler(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
            {
                try
                {
                    if (args.PairingKind == DevicePairingKinds.ProvidePin) args.Accept("0000");
                    else args.Accept();
                }
                catch { }
            }

            custom.PairingRequested += Handler;
            try
            {
                var kinds = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin |
                            DevicePairingKinds.ProvidePin | DevicePairingKinds.ConfirmPinMatch;
                var result = await WithTimeout(
                        custom.PairAsync(kinds, DevicePairingProtectionLevel.Encryption).AsTask(), 8)
                    .ConfigureAwait(false);
                DiagnosticsLog.Write("ble.log", $"quick pair {result.Status}");
                return result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired;
            }
            finally
            {
                custom.PairingRequested -= Handler;
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("ble.log", $"quick pair exception {ex.Message}");
            return false;
        }
    }

    /// <summary>尝试通过 Windows 完成 BLE 配对；已配对则直接返回。</summary>
    private async Task<bool> TryPairAsync(CancellationToken cancellationToken)
    {
        if (_device == null) return false;
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(_device.DeviceId).AsTask(cancellationToken).ConfigureAwait(false);
            if (info.Pairing.IsPaired)
            {
                Log?.Invoke(this, "设备已配对");
                return true;
            }

            Log?.Invoke(this, "设备未配对，先尝试默认配对…");
            try
            {
                var simple = await WithTimeout(info.Pairing.PairAsync(DevicePairingProtectionLevel.None).AsTask(), 10)
                    .ConfigureAwait(false);
                Log?.Invoke(this, $"默认配对结果：{simple.Status}");
                DiagnosticsLog.Write("ble.log", $"pair default {simple.Status}");
                if (simple.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)
                    return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Write("ble.log", $"pair default exception {ex.Message}");
            }

            // 默认配对失败后，用自定义配对自动确认 Just Works / 固定 PIN。
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            try
            {
                var custom = info.Pairing.Custom;
                void Handler(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
                {
                    try
                    {
                        Log?.Invoke(this, $"配对请求：{args.PairingKind}");
                        DiagnosticsLog.Write("ble.log", $"pairing requested {args.PairingKind} pin={args.Pin}");
                        if (args.PairingKind == DevicePairingKinds.ProvidePin) args.Accept("0000");
                        else args.Accept();
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Write("ble.log", $"accept failed {ex.Message}");
                    }
                }

                custom.PairingRequested += Handler;
                var kinds = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin |
                            DevicePairingKinds.ProvidePin | DevicePairingKinds.ConfirmPinMatch;
                var result = await WithTimeout(
                        custom.PairAsync(kinds, DevicePairingProtectionLevel.Encryption).AsTask(), 15)
                    .ConfigureAwait(false);
                custom.PairingRequested -= Handler;
                Log?.Invoke(this, $"自定义配对结果：{result.Status}");
                DiagnosticsLog.Write("ble.log", $"pair custom {result.Status}");
                return result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired;
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"自定义配对失败：{ex.Message}");
                DiagnosticsLog.Write("ble.log", $"pair custom exception {ex.Message}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"自动配对失败：{ex.Message}");
            return false;
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            IsConnected = false;
            DiagnosticsLog.Write("ble.log", $"disconnected {_address:X12}");
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var buffer = args.CharacteristicValue;
            var bytes = new byte[(int)buffer.Length];
            using var reader = DataReader.FromBuffer(buffer);
            reader.ReadBytes(bytes);
            _notifyCount++;
            if (bytes.Length >= 7 && bytes[0] == 0x24)
            {
                // 0x24 状态帧：24 01 07 XX 00 XX CHECKSUM，XX 为电量百分比（0xFF 表示充电/未知）。
                var raw = bytes[3];
                var level = raw == 0xFF ? -1 : raw <= 100 ? raw : -1;
                if (level != _lastBattery)
                {
                    _lastBattery = level;
                    BatteryChanged?.Invoke(this, level);
                    DiagnosticsLog.Write("ble.log", $"battery {level}% raw={Convert.ToHexString(bytes)}");
                }
            }
            RawNotify?.Invoke(this, bytes);
            if (_notifyCount <= 200)
                DiagnosticsLog.Write("ble.log", $"notify {_notifyCount} {Convert.ToHexString(bytes)}");
            if (_notifyCount == 1)
                DiagnosticsLog.Write("ble.log", $"first notify {Convert.ToHexString(bytes)}");
            else if (_notifyCount % 200 == 0)
                DiagnosticsLog.Write("ble.log", $"notify count {_notifyCount}");
            foreach (var frame in _reader.Feed(bytes))
            {
                FrameReceived?.Invoke(this, frame);
                if (frame.IsGamepad && BladeReport.TryParse(frame.Payload, out var report) && report != null)
                {
                    var now = Stopwatch.GetTimestamp();
                    if (_lastGamepadTicks != 0)
                    {
                        var ms = (now - _lastGamepadTicks) * 1000.0 / Stopwatch.Frequency;
                        _sumIntervalMs += ms;
                        if (ms < _minIntervalMs) _minIntervalMs = ms;
                        if (ms > _maxIntervalMs) _maxIntervalMs = ms;
                    }
                    _lastGamepadTicks = now;
                    _gamepadCount++;
                    if (_rateWindow.ElapsedMilliseconds >= 2000 && _gamepadCount > 0)
                    {
                        var hz = _gamepadCount * 1000.0 / _rateWindow.ElapsedMilliseconds;
                        var avg = _gamepadCount > 1 ? _sumIntervalMs / (_gamepadCount - 1) : 0;
                        DiagnosticsLog.Write("ble.log",
                            $"gamepad rate {hz:F1}Hz frames={_gamepadCount} interval avg={avg:F1}ms min={(_minIntervalMs == double.MaxValue ? 0 : _minIntervalMs):F1} max={_maxIntervalMs:F1}");
                        _gamepadCount = 0;
                        _sumIntervalMs = 0;
                        _minIntervalMs = double.MaxValue;
                        _maxIntervalMs = 0;
                        _rateWindow.Restart();
                    }
                    ReportReceived?.Invoke(this, report);
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"解析蓝牙报文失败：{ex.Message}");
        }
    }

    private void OnFf11ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var hex = BufferToHex(args.CharacteristicValue);
            _ff11Count++;
            if (_ff11Count <= 100)
                DiagnosticsLog.Write("ble.log", $"ff11 {_ff11Count} {hex}");
        }
        catch { }
    }

    public async Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        if (_writeChar == null) throw new InvalidOperationException("蓝牙未连接");
        using var writer = new DataWriter();
        writer.WriteBytes(frame);
        var buffer = writer.DetachBuffer();

        // Android 端 0x8655 使用 WRITE_TYPE_NO_RESPONSE（setWriteType(1)），优先按无响应写发送。
        var result = await WithTimeout(_writeChar.WriteValueWithResultAsync(buffer, GattWriteOption.WriteWithoutResponse)
            .AsTask(), 5).ConfigureAwait(false);
        if (result.Status != GattCommunicationStatus.Success)
        {
            result = await WithTimeout(_writeChar.WriteValueWithResultAsync(buffer, GattWriteOption.WriteWithResponse)
                .AsTask(), 5).ConfigureAwait(false);
        }
        DiagnosticsLog.Write("ble.log", $"write {frame.Length}B {Convert.ToHexString(frame)} -> {result.Status}");
        if (result.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"蓝牙写入失败：{result.Status}");
    }

    private static string BufferToHex(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return Convert.ToHexString(bytes);
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, int seconds)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))).ConfigureAwait(false);
        if (completed != task) throw new TimeoutException($"操作超时（{seconds} 秒）");
        return await task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        IsConnected = false;
        try { if (_notifyChar != null) _notifyChar.ValueChanged -= OnValueChanged; } catch { }
        try { if (_device != null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
        try { _session?.Dispose(); } catch { }
        try { _service?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _session = null;
        _service = null;
        _device = null;
        _writeChar = null;
        _notifyChar = null;
        await Task.CompletedTask;
    }
}
