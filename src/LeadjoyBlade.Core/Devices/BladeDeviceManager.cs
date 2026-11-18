using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using HidSharp;
using LeadjoyBlade.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace LeadjoyBlade.Core.Devices;

/// <summary>
/// 负责发现、连接、保活与重连风刃手柄。优先 BLE（可拿到全部 M1-M8/Fn 键），
/// 其次尝试 USB HID 厂商接口。
/// </summary>
public sealed class BladeDeviceManager : IAsyncDisposable
{
    private static readonly string[] NameFilters = ["blade", "风刃", "windblade", "leadjoy"];
    private readonly ConcurrentDictionary<string, BladeDeviceCandidate> _candidates = new();
    private readonly ConcurrentDictionary<ulong, DateTime> _seen = new();
    private readonly ConcurrentDictionary<ulong, string> _advertisements = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly object _sync = new();

    private BluetoothLEAdvertisementWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private IBladeTransport? _current;
    private BladeDeviceCandidate? _currentCandidate;
    private Timer? _keepAlive;
    private Timer? _hidScan;
    private Timer? _autoConnect;
    private DateTime _lastReport = DateTime.MinValue;
    private int _reconnectAttempt;

    public event EventHandler<BladeReport>? ReportReceived;
    public event EventHandler<IReadOnlyList<BladeDeviceCandidate>>? CandidatesChanged;
    public event EventHandler<string>? Log;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<string>? DeviceNameChanged;
    public event EventHandler<int>? BatteryChanged;
    public int? BatteryPercent { get; private set; }

    public IBladeTransport? Current => _current;
    public bool IsConnected => _current?.IsConnected == true;
    public BladeTransportKind? ConnectedKind => _current?.Kind;
    public string? ConnectedName { get; private set; }
    public IReadOnlyList<BladeDeviceCandidate> Candidates => _candidates.Values.OrderBy(c => c.Name).ToList();
    public int AdvertisementCount => _advertisements.Count;
    public IReadOnlyList<string> AdvertisementNames => _advertisements.Values.OrderBy(n => n).ToList();
    public bool AutoReconnect { get; set; } = true;
    public bool AutoConnect { get; set; } = true;
    public bool IncludeAllBle { get; set; }

    /// <summary>
    /// 外部传输工厂（App 层注入）。目前只用于 DS5：由 App 复用 Binput 的 HidPad
    /// 解析 DualSense，避免 Core 再写一套 HID 描述符解析，也避免和输入法抢设备。
    /// </summary>
    public Func<BladeDeviceCandidate, IBladeTransport?>? TransportFactory { get; set; }
    private DateTime _lastAutoConnectAttempt = DateTime.MinValue;
    private LastBleDevice? _lastBle;
    private readonly ConcurrentDictionary<string, int> _bleFailures = new();

    public bool HasLastBle => _lastBle != null;

    private async Task ClearBleBindingAsync(BladeDeviceCandidate candidate)
    {
        try
        {
            BluetoothLEDevice? device = null;
            if (!string.IsNullOrWhiteSpace(candidate.Path) &&
                candidate.Path.StartsWith("BluetoothLE#", StringComparison.OrdinalIgnoreCase))
                device = await BluetoothLEDevice.FromIdAsync(candidate.Path).AsTask().ConfigureAwait(false);
            device ??= await BluetoothLEDevice.FromBluetoothAddressAsync(
                candidate.BluetoothAddress, candidate.AddressType).AsTask().ConfigureAwait(false);
            if (device == null) return;

            try
            {
                var info = await DeviceInformation.CreateFromIdAsync(device.DeviceId).AsTask().ConfigureAwait(false);
                if (info.Pairing.IsPaired)
                {
                    var result = await info.Pairing.UnpairAsync().AsTask().ConfigureAwait(false);
                    DiagnosticsLog.Write("ble.log",
                        $"unpair {candidate.Name} [{candidate.BluetoothAddress:X12}] -> {result.Status}");
                }
                else
                {
                    DiagnosticsLog.Write("ble.log",
                        $"unpair skipped (not paired) {candidate.Name} [{candidate.BluetoothAddress:X12}]");
                }
            }
            finally
            {
                device.Dispose();
            }

            _candidates.TryRemove(candidate.Id, out _);
            NotifyCandidates();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("ble.log", $"unpair failed: {ex.Message}");
        }
    }

    public async Task<string> ReadOnboardConfigAsync(CancellationToken cancellationToken = default)
    {
        var transport = _current ?? throw new InvalidOperationException("请先连接 Blade Plus_1B");
        var completion = new TaskCompletionSource<Dictionary<string, object?>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnFrame(object? sender, Frame frame)
        {
            if (frame.Type != FrameReader.TypeMsgPack) return;
            try
            {
                var text = Convert.ToHexString(frame.Payload);
                DiagnosticsLog.Write("ble.log", $"onboard cmd frame {text}");
                if (MsgPack.Unpack(frame.Payload) is not Dictionary<string, object?> map) return;
                DiagnosticsLog.Write("ble.log",
                    $"onboard cmd c={(map.TryGetValue("c", out var cc) ? cc : "?")} keys={string.Join(",", map.Keys)}");
                var error = map.TryGetValue("e", out var ev) ? Convert.ToInt32(ev) : 0;
                if (error == 15)
                {
                    completion.TrySetException(new InvalidOperationException("手柄固件不支持板载配置读取（e=15 NOT SUPPORTED）"));
                    return;
                }
                if (!map.TryGetValue("c", out var command) || Convert.ToInt32(command) != 10) return;
                completion.TrySetResult(map);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Write("ble.log", $"onboard frame parse failed: {ex.Message}");
            }
        }

        transport.FrameReceived += OnFrame;
        try
        {
            foreach (var frame in BladeProtocol.BuildCommandFrames(
                         MsgPack.PackMap(("c", 10)), transport.Mtu))
            {
                DiagnosticsLog.Write("ble.log", $"send onboard read {Convert.ToHexString(frame)}");
                await transport.SendAsync(frame, cancellationToken).ConfigureAwait(false);
            }

            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            var section = result.TryGetValue("r", out var r) ? r
                : result.TryGetValue("s", out var s) ? s : null;
            if (section is Dictionary<string, object?> map &&
                map.TryGetValue("d1", out var entries))
                return JsonSerializer.Serialize(entries,
                    new JsonSerializerOptions { WriteIndented = true });
            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        finally
        {
            transport.FrameReceived -= OnFrame;
        }
    }

    public async Task<string> ScanOnboardCommandsAsync(CancellationToken cancellationToken = default)
    {
        var transport = _current ?? throw new InvalidOperationException("请先连接 Blade Plus_1B");
        var lines = new List<string>();
        var current = new List<string>();

        void OnFrame(object? sender, Frame frame)
        {
            if (frame.Type != FrameReader.TypeMsgPack) return;
            try
            {
                if (MsgPack.Unpack(frame.Payload) is not Dictionary<string, object?> map) return;
                var c = map.TryGetValue("c", out var cv) ? cv : "?";
                var e = map.TryGetValue("e", out var ev) ? ev : "?";
                current.Add($"c={c},e={e}");
            }
            catch { }
        }

        transport.FrameReceived += OnFrame;
        try
        {
            var ranges = new[] { (0, 50), (200, 260), (1000, 1100) };
            foreach (var (from, to) in ranges)
            {
                for (var command = from; command <= to; command++)
                {
                    current.Clear();
                    foreach (var frame in BladeProtocol.BuildCommandFrames(
                                 MsgPack.PackMap(("c", command)), transport.Mtu))
                        await transport.SendAsync(frame, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                    lines.Add($"{command}: {(current.Count == 0 ? "无响应" : string.Join(" | ", current))}");
                }
            }
        }
        finally
        {
            transport.FrameReceived -= OnFrame;
        }
        return string.Join(Environment.NewLine, lines);
    }

    public async Task SaveOnboardConfigAsync(string json, CancellationToken cancellationToken = default)
    {
        var transport = _current ?? throw new InvalidOperationException("请先连接 Blade Plus_1B");
        var entries = JsonSerializer.Deserialize<List<object>>(json)
                      ?? throw new InvalidOperationException("配置 JSON 必须是数组");
        var payload = MsgPack.PackMap(("c", 1003), ("s", new Dictionary<string, object?>
        {
            ["d1"] = entries,
        }));
        foreach (var frame in BladeProtocol.BuildCommandFrames(payload, transport.Mtu))
            await transport.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        foreach (var frame in BladeProtocol.BuildCommandFrames(
                     MsgPack.PackMap(("c", 1004)), transport.Mtu))
            await transport.SendAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> VibrateAsync(double left, double right, int milliseconds = 400)
    {
        for (uint slot = 0; slot < 4; slot++)
        {
            if (!XInputTransport.IsAvailable(slot)) continue;
            return await Task.Run(() => XInputTransport.TryVibrate(slot, left, right, milliseconds))
                .ConfigureAwait(false);
        }

        return await Task.Run(() => HidRumble.TryVibrateDs4(left, right, milliseconds))
            .ConfigureAwait(false);
    }

    public async Task<bool> ConnectLastAsync(CancellationToken cancellationToken = default)
    {
        var last = _lastBle;
        if (last == null) return false;
        var candidate = new BladeDeviceCandidate($"last-ble:{last.Address:X12}", last.Name,
            BladeTransportKind.Ble, last.Address, Path: last.DeviceId);
        return await ConnectAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

    public void ClearCandidates()
    {
        _candidates.Clear();
        _advertisements.Clear();
        _seen.Clear();
        NotifyCandidates();
    }

    private string _lastCandidateSignature = "<none>";

    /// <summary>候选列表变化时写一行日志，现场排查"找不到手柄"时一眼能看到 app 到底认出了什么。</summary>
    private void NotifyCandidates()
    {
        var list = Candidates;
        var signature = string.Join("|", list.Select(c => c.Id + "=" + c.Name));
        if (signature != _lastCandidateSignature)
        {
            _lastCandidateSignature = signature;
            DiagnosticsLog.Write("ble.log",
                "候选列表(" + list.Count + "): " + (list.Count == 0
                    ? "(空)"
                    : string.Join(" ; ", list.Select(c => c.Name + " [" + c.Kind + "] " + c.Id))));
        }
        CandidatesChanged?.Invoke(this, list);
    }

    private void TriggerAutoConnect(BladeDeviceCandidate candidate)
    {
        if (!AutoConnect || IsConnected || _current != null) return;
        if ((DateTime.UtcNow - _lastAutoConnectAttempt).TotalSeconds < 2) return;
        _lastAutoConnectAttempt = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try { await ConnectAsync(candidate).ConfigureAwait(false); } catch { }
        });
    }

    private bool IsRecentlySeen(BladeDeviceCandidate candidate, DateTime now, int seconds) =>
        _seen.TryGetValue(candidate.BluetoothAddress, out var seen) &&
        (now - seen).TotalSeconds <= seconds;

    private void AutoConnectTick()
    {
        try
        {
            var now = DateTime.UtcNow;

            // 清理超过 15 秒没有再次广播的 _1B（包括 Windows 缓存里的离线地址），避免虚空重试。
            var removed = false;
            foreach (var stale in _candidates.Values
                         .Where(c => c.Kind == BladeTransportKind.Ble &&
                                     c.Name.Contains("_1B", StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                if (IsRecentlySeen(stale, now, 15)) continue;
                removed |= _candidates.TryRemove(stale.Id, out _);
            }
            if (removed) NotifyCandidates();

            if (IsConnected || _current != null) return;
            var candidate = _candidates.Values
                .Where(c => c.Kind == BladeTransportKind.Ble &&
                            c.Name.Contains("_1B", StringComparison.OrdinalIgnoreCase) &&
                            IsRecentlySeen(c, now, 10))
                .OrderByDescending(c => _seen.TryGetValue(c.BluetoothAddress, out var seen) ? seen : DateTime.MinValue)
                .FirstOrDefault();
            if (candidate == null) return;
            TriggerAutoConnect(candidate);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("ble.log", $"auto-connect tick fail {ex.Message}");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = new CancellationTokenSource();
        _lastBle ??= LastDeviceStore.Load();
        await ClearStaleBleBindingsAsync().ConfigureAwait(false);
        StartBleWatcher();
        ScanHid();
        // Switch HID / USB HID 和 XInput / RawGameController 都随这个定时器周期发现，
        // 不再只有「重新扫描」才枚举 DS4 / Xbox / Switch 模式。
        _hidScan = new Timer(_ => { ScanHid(); ScanGamepads(); },
            null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        _keepAlive = new Timer(_ => KeepAlive(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        // 未连接时每 5 秒对最近的 _1B 持续重试，不依赖经典链路/XInput。
        _autoConnect = new Timer(_ => AutoConnectTick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        await ScanPairedAsync().ConfigureAwait(false);
        await Task.CompletedTask;
    }

    /// <summary>启动时清理所有未连接的 _1B 缓存/绑定，避免上次异常退出留下的失效条目导致连接失败。</summary>
    private async Task ClearStaleBleBindingsAsync()
    {
        try
        {
            var infos = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector());
            foreach (var info in infos)
            {
                try
                {
                    using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
                    if (device == null) continue;
                    var name = device.Name ?? string.Empty;
                    if (!name.Contains("_1B", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!info.Pairing.IsPaired) continue;

                    var result = await info.Pairing.UnpairAsync();
                    DiagnosticsLog.Write("ble.log", $"startup unpair {name} -> {result.Status}");
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Write("ble.log", $"startup unpair failed: {ex.Message}");
                }
            }

            // 已知上次地址（即使设备名暂时为空）也尝试解除绑定。
            if (_lastBle != null)
            {
                try
                {
                    using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(
                        _lastBle.Address, BluetoothAddressType.Public);
                    if (device != null)
                    {
                        var info = await DeviceInformation.CreateFromIdAsync(device.DeviceId);
                        if (info.Pairing.IsPaired)
                        {
                            var result = await info.Pairing.UnpairAsync();
                            DiagnosticsLog.Write("ble.log",
                                $"startup unpair(last) {_lastBle.Address:X12} -> {result.Status}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Write("ble.log", $"startup unpair(last) failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("ble.log", $"startup cache cleanup failed: {ex.Message}");
        }
    }

    /// <summary>列出 Windows 中已配对的蓝牙设备（BLE + 经典蓝牙）。已连接的手柄不会再广播，必须从配对列表里找。</summary>
    public async Task ScanPairedAsync()
    {
        try
        {
            var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var infos = await DeviceInformation.FindAllAsync(selector);
            foreach (var info in infos)
            {
                try
                {
                    using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
                    if (device == null) continue;
                    if (device.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;
                    AddPairedCandidate(device.BluetoothAddress, device.Name, "BLE", info.Id);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"枚举已配对 BLE 设备失败：{ex.Message}");
        }

        // 已连接的 BLE 设备（即使系统里尚未标记为“已配对”）。
        try
        {
            var selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var infos = await DeviceInformation.FindAllAsync(selector);
            foreach (var info in infos)
            {
                try
                {
                    using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
                    if (device == null) continue;
                    AddPairedCandidate(device.BluetoothAddress, device.Name, "已连接", info.Id);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"枚举已连接 BLE 设备失败：{ex.Message}");
        }

        // 经典蓝牙列表：有些型号在经典模式下不广播 BLE，但同一地址仍可尝试 GATT 连接。
        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var infos = await DeviceInformation.FindAllAsync(selector);
            foreach (var info in infos)
            {
                try
                {
                    using var device = await BluetoothDevice.FromIdAsync(info.Id);
                    if (device == null) continue;
                    if (device.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;
                    AddPairedCandidate(device.BluetoothAddress, device.Name, "经典蓝牙", info.Id);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"枚举已配对经典蓝牙设备失败：{ex.Message}");
        }

        // Windows 缓存过的全部 BLE 设备：即使当前没有广播，也能按设备 ID 直接连接。
        try
        {
            var selector = BluetoothLEDevice.GetDeviceSelector();
            var infos = await DeviceInformation.FindAllAsync(selector);
            foreach (var info in infos)
            {
                try
                {
                    using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
                    if (device == null || string.IsNullOrWhiteSpace(device.Name)) continue;
                    AddPairedCandidate(device.BluetoothAddress, device.Name, "BLE缓存", info.Id);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"枚举 BLE 缓存设备失败：{ex.Message}");
        }

        ScanGamepads();
    }

    private void ScanGamepads()
    {
        try
        {
            for (uint slot = 0; slot < 4; slot++)
            {
                if (!XInputTransport.IsAvailable(slot)) continue;
                var id = $"xinput:{slot}";
                if (_candidates.ContainsKey(id)) continue;
                _candidates[id] = new BladeDeviceCandidate(id, "XBOX 模式", BladeTransportKind.XInput);
                Log?.Invoke(this, $"发现 XInput 手柄 #{slot + 1}，可用于标准按键映射");
                NotifyCandidates();
            }

            // 不经过 Windows.Gaming.Input 的 Switch 手柄发现：即使系统手柄列表是空的，
            // 只要 HID 接口在，就能直读报文（本机就是这种情况）。
            var switchHidDevices = SwitchHidTransport.Discover();
            for (var i = 0; i < RawGameControllerTransport.Count; i++)
            {
                var pad = RawGameControllerTransport.Get(i);
                if (pad == null) continue;
                // Xbox 手柄由 XInput 统一处理，避免同一只手柄出现两条“XBOX 模式”。
                if (pad.HardwareVendorId == 0x045E) continue;
                var id = $"raw:{i}";
                if (_candidates.ContainsKey(id)) continue;
                var modeName = pad.HardwareVendorId switch
                {
                    0x054C => "DS4 模式",
                    0x057E => "SWITCH 模式",
                    0x045E => "XBOX 模式",
                    _ => "HID 模式",
                };

                // Switch 模式：部分电脑的蓝牙 HID 栈不把报文交给 Windows.Gaming.Input
                // （RawGameController 时间戳恒为 0，甚至列表里根本没有这只手柄），
                // 直接用 HID 接口读取才是可靠路径，见下方 DiscoverSwitchHidCandidates。

                // Switch 模式固定走 HID 直读（见 SwitchHidTransport 的说明）：
                // 不再列出“系统接口”候选，避免同一只手柄出现两条通路、行为不一致。
                if (pad.HardwareVendorId == 0x057E) continue;

                // 同时连着多只手柄时，名称带上 VID:PID 和按键数，便于区分具体是哪一只。
                var display = $"{modeName}（{pad.HardwareVendorId:X4}:{pad.HardwareProductId:X4} " +
                              $"{pad.ButtonCount}键）";
                _candidates[id] = new BladeDeviceCandidate(id, display,
                    BladeTransportKind.RawGameController, VendorId: pad.HardwareVendorId,
                    ProductId: pad.HardwareProductId);
                Log?.Invoke(this, $"发现 HID 手柄：{pad.DisplayName}（{pad.HardwareVendorId:X4}:{pad.HardwareProductId:X4}，" +
                                  $"{pad.ButtonCount} 键 / {pad.SwitchCount} 十字键 / {pad.AxisCount} 轴）");
                NotifyCandidates();
            }

            // 不经过 Windows.Gaming.Input 的 Switch 手柄发现：即使系统手柄列表是空的，
            // 只要 HID 接口在，就能直读报文（本机就是这种情况）。
            foreach (var device in switchHidDevices)
            {
                var id = $"swhid:{device.Path}";
                if (_candidates.ContainsKey(id)) continue;
                if (!SwitchHidTransport.IsLive(device)) continue;
                _candidates[id] = new BladeDeviceCandidate(id,
                    $"SWITCH 模式（HID 直读 {device.VendorId:X4}:{device.ProductId:X4}）",
                    BladeTransportKind.SwitchHid, VendorId: device.VendorId,
                    ProductId: device.ProductId, Path: device.Path);
                Log?.Invoke(this, $"发现 Switch 手柄（HID 直读）：{device.VendorId:X4}:{device.ProductId:X4}");
                NotifyCandidates();
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("ble.log", $"xinput probe fail {ex.Message}");
        }
    }

    private static bool IsSwitchHidIdentity(string name) =>
        name.Contains("Blade Plus", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("_1B", StringComparison.OrdinalIgnoreCase);

    private void AddPairedCandidate(ulong address, string name, string source, string? deviceId)
    {
        // _1B 只由真实广播添加；Windows 的缓存/离线条目不再显示，避免点到失效地址。
        if (name.Contains("_1B", StringComparison.OrdinalIgnoreCase)) return;

        var nameMatch = !string.IsNullOrWhiteSpace(name) &&
                        NameFilters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));
        if (!nameMatch && !IncludeAllBle) return;

        var id = $"ble:{address:X12}";
        if (_candidates.ContainsKey(id)) return;
        var display = string.IsNullOrWhiteSpace(name) ? $"已配对设备 {address:X12}" : name;
        _candidates[id] = new BladeDeviceCandidate(id, display, BladeTransportKind.Ble, address, Path: deviceId);
        DiagnosticsLog.Write("ble.log", $"paired candidate {source} {display} [{address:X12}] id={deviceId}");
        Log?.Invoke(this, $"发现已配对设备（{source}）：{display} [{address:X12}]");
        NotifyCandidates();

        // 缓存候选不自动连接：随机地址可能已失效，自动连会反复失败并拖慢发现；
        // 只对当前广播到的地址自动连接。
    }

    private void StartBleWatcher()
    {
        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active,
                // 风刃的厂商通道使用扩展广播；Windows 默认不接收，必须显式打开。
                AllowExtendedAdvertisements = true,
            };
            _watcher.Received += OnAdvertisementReceived;
            _watcher.Stopped += (_, e) =>
            {
                Log?.Invoke(this, $"蓝牙扫描停止：{e.Error}");
                if (IsConnected) return;
                Task.Run(async () =>
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    if (!IsConnected)
                    {
                        try { _watcher?.Start(); } catch { }
                    }
                });
            };
            _watcher.Start();
            Log?.Invoke(this, "蓝牙扫描已启动，请将手柄切换到蓝牙模式并靠近电脑");
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"无法启动蓝牙扫描：{ex.Message}");
        }
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (IsConnected) return;
        try
        {
            var name = args.Advertisement.LocalName;
            var hasService = args.Advertisement.ServiceUuids.Any(u => u == BleBladeTransport.ServiceUuid);
            // 同名 “Blade Plus” 有 G-Touch（0x8650）和 Switch HID（0x1812）两种身份；
            // 只有带厂商服务的才是可连接的 G-Touch，纯 HID 身份跳过。
            if (IsSwitchHidIdentity(name) && !hasService) return;
            var nameMatch = !string.IsNullOrWhiteSpace(name) &&
                            NameFilters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));

            _advertisements[args.BluetoothAddress] = string.IsNullOrWhiteSpace(name)
                ? $"(无名) {args.BluetoothAddress:X12}"
                : name;

            if (!hasService && !nameMatch && !IncludeAllBle) return;
            if (nameMatch || hasService)
            {
                DiagnosticsLog.Write("ble.log",
                    $"{args.BluetoothAddress:X12} name='{name}' type={args.AdvertisementType} addrType={args.BluetoothAddressType} rssi={args.RawSignalStrengthInDBm} services=[{string.Join(",", args.Advertisement.ServiceUuids)}]");
            }
            if (IncludeAllBle && !hasService && !nameMatch && args.RawSignalStrengthInDBm < -80) return;

            var id = $"ble:{args.BluetoothAddress:X12}";
            var display = string.IsNullOrWhiteSpace(name)
                ? (hasService ? "风刃手柄（G-Touch）" : $"蓝牙设备 {args.BluetoothAddress:X12}")
                : name;
            if (hasService && IsSwitchHidIdentity(name)) display = "G-Touch 模式";
            // G-Touch（0x8650）和 _1B 都是厂商通道，可自动连接。
            var isVendorIdentity = display.Contains("_1B", StringComparison.OrdinalIgnoreCase) ||
                                   (hasService && display.Contains("G-Touch", StringComparison.OrdinalIgnoreCase));

            // 同一手柄的 BLE 随机地址会变；只保留当前这次广播的 _1B 候选，避免连到旧地址。
            if (isVendorIdentity)
            {
                foreach (var stale in _candidates.Values
                             .Where(c => c.Id != id && c.Name.Contains("_1B", StringComparison.OrdinalIgnoreCase))
                             .ToList())
                    _candidates.TryRemove(stale.Id, out _);
            }

            if (_candidates.TryGetValue(id, out var existing))
            {
                _seen[args.BluetoothAddress] = DateTime.UtcNow;
                if (isVendorIdentity)
                    TriggerAutoConnect(_candidates[id]); // 同一条广播重复出现时也要能再次触发
                if (existing.Name == display && existing.Rssi == args.RawSignalStrengthInDBm) return;
                _candidates[id] = existing with { Name = display, Rssi = args.RawSignalStrengthInDBm };
                NotifyCandidates();
                return;
            }

            var candidate = new BladeDeviceCandidate(id, display, BladeTransportKind.Ble, args.BluetoothAddress,
                Rssi: args.RawSignalStrengthInDBm, AddressType: args.BluetoothAddressType);
            _candidates[id] = candidate;
            _seen[args.BluetoothAddress] = DateTime.UtcNow;
            Log?.Invoke(this, $"发现蓝牙设备：{display} [{args.BluetoothAddress:X12}] 信号 {args.RawSignalStrengthInDBm}dBm" +
                              (nameMatch || hasService ? "（疑似风刃）" : ""));
            NotifyCandidates();

            if (isVendorIdentity)
                TriggerAutoConnect(candidate);
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"解析蓝牙广播失败：{ex.Message}");
        }
    }

    private static bool HasVendorUsagePage(HidDevice device)
    {
        try
        {
            var raw = device.GetRawReportDescriptor();
            var i = 0;
            while (i < raw.Length)
            {
                var prefix = raw[i];
                var size = prefix & 0x03;
                if (size == 3) size = 4;
                if ((prefix & 0xFC) == 0x04 && size == 2 && i + 2 < raw.Length)
                {
                    var page = raw[i + 1] | (raw[i + 2] << 8);
                    if (page >= 0xFF00) return true;
                }
                i += 1 + size;
            }
        }
        catch { }
        return false;
    }

    private void ScanHid()
    {
        try
        {
            // Switch 模式（HID 直读）：每 5 秒随扫描自动发现，
            // 这样手柄从休眠唤醒后不需要用户手动点「重新扫描」。
            foreach (var switchDevice in SwitchHidTransport.Discover())
            {
                var switchId = $"swhid:{switchDevice.Path}";
                if (_candidates.ContainsKey(switchId)) continue;
                if (!SwitchHidTransport.IsLive(switchDevice)) continue;
                _candidates[switchId] = new BladeDeviceCandidate(switchId,
                    $"SWITCH 模式（HID 直读 {switchDevice.VendorId:X4}:{switchDevice.ProductId:X4}）",
                    BladeTransportKind.SwitchHid, VendorId: switchDevice.VendorId,
                    ProductId: switchDevice.ProductId, Path: switchDevice.Path);
                Log?.Invoke(this, $"发现 Switch 手柄（HID 直读）：{switchDevice.VendorId:X4}:{switchDevice.ProductId:X4}");
                NotifyCandidates();
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("ble.log", $"scan switch hid fail: {ex.Message}");
        }

        try
        {
            foreach (var device in DeviceList.Local.GetHidDevices())
            {
                bool match;
                string? product = null;

                // DualSense（PS5 / DS5）：Windows.Gaming.Input 往往枚举不到，必须 HID 直读。
                // 只新增这一条，DS4 / Xbox / Switch / 风刃的 HID 匹配逻辑保持原样。
                if (device.VendorID == 0x054C &&
                    (device.ProductID == 0x0CE6 || device.ProductID == 0x0DF2))
                {
                    var ds5Id = $"ds5hid:{device.DevicePath}";
                    if (!_candidates.ContainsKey(ds5Id))
                    {
                        var ds5Name = device.ProductID == 0x0DF2
                            ? "DS5 模式（DualSense Edge）"
                            : "DS5 模式（DualSense）";
                        _candidates[ds5Id] = new BladeDeviceCandidate(ds5Id, ds5Name,
                            BladeTransportKind.DualSenseHid,
                            VendorId: device.VendorID, ProductId: device.ProductID,
                            Path: device.DevicePath);
                        Log?.Invoke(this, $"发现 DualSense：{ds5Name} ({device.VendorID:X4}:{device.ProductID:X4})");
                        NotifyCandidates();
                    }
                    continue;
                }

                try
                {
                    product = device.GetProductName();
                    var vendorCandidate = HasVendorUsagePage(device) &&
                        (string.IsNullOrWhiteSpace(product) ||
                         NameFilters.Any(f => product.Contains(f, StringComparison.OrdinalIgnoreCase)) ||
                         product.Contains("xbox", StringComparison.OrdinalIgnoreCase));
                    match = device.VendorID is 0x4131 or 0x4C4A ||
                            (!string.IsNullOrWhiteSpace(product) &&
                             NameFilters.Any(f => product.Contains(f, StringComparison.OrdinalIgnoreCase))) ||
                            vendorCandidate;
                }
                catch
                {
                    match = device.VendorID is 0x4131 or 0x4C4A;
                }

                if (!match) continue;

                var id = $"hid:{device.DevicePath}";
                if (_candidates.ContainsKey(id)) continue;
                var name = string.IsNullOrWhiteSpace(product)
                    ? $"USB HID {device.VendorID:X4}:{device.ProductID:X4}"
                    : product;
                _candidates[id] = new BladeDeviceCandidate(id, name, BladeTransportKind.Hid,
                    VendorId: device.VendorID, ProductId: device.ProductID, Path: device.DevicePath);
                Log?.Invoke(this, $"发现 USB HID：{name} ({device.VendorID:X4}:{device.ProductID:X4})");
                NotifyCandidates();
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"扫描 HID 失败：{ex.Message}");
        }

        // 每轮扫描结束都落一行日志（内部按签名去重），便于现场确认 app 认出了哪些手柄。
        NotifyCandidates();
    }

    public async Task<bool> ConnectAsync(BladeDeviceCandidate candidate, CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected && _current != null)
            {
                if (_currentCandidate?.Id == candidate.Id) return true;
                await DisconnectAsync().ConfigureAwait(false);
            }
            return await ConnectCoreAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<bool> ConnectCoreAsync(BladeDeviceCandidate candidate, CancellationToken cancellationToken)
    {
        await DisconnectAsync().ConfigureAwait(false);
        // 连接期间保持广播扫描：BLE 地址是随机地址，停扫描会错过新地址，导致反复连旧地址失败。
        // 不再等待 XInput/经典链路：未连接时由 AutoConnectTick 持续重试 _1B。
        const int maxAttempts = 1;
        IBladeTransport? transport = null;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var attemptSw = Stopwatch.StartNew();
            try
            {
                transport = CreateTransport(candidate);
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"创建传输失败：{ex.Message}");
                return false;
            }

            transport.Log += (_, msg) => Log?.Invoke(this, msg);
            transport.ReportReceived += OnReport;
            transport.Disconnected += OnTransportDisconnected;
            transport.BatteryChanged += OnBatteryChanged;

            try
            {
                await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                DiagnosticsLog.Write("ble.log",
                    $"connect attempt {attempt}/{maxAttempts} success +{attemptSw.ElapsedMilliseconds}ms");
                _bleFailures.TryRemove(candidate.Id, out _);
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                DiagnosticsLog.Write("ble.log",
                    $"connect attempt {attempt}/{maxAttempts} fail +{attemptSw.ElapsedMilliseconds}ms {candidate.Name} [{candidate.Id}]: {ex.Message}");
                transport.ReportReceived -= OnReport;
                transport.Disconnected -= OnTransportDisconnected;
                transport.BatteryChanged -= OnBatteryChanged;
                await transport.DisposeAsync().ConfigureAwait(false);
                transport = null;
                if (candidate.Kind == BladeTransportKind.Ble &&
                    candidate.Name.Contains("_1B", StringComparison.OrdinalIgnoreCase))
                {
                    var fails = _bleFailures.AddOrUpdate(candidate.Id, 1, (_, v) => v + 1);
                    if (fails >= 2)
                    {
                        // Windows 会留下失效的 BLE 绑定，导致再次连接报错；解除后重新配对。
                        await ClearBleBindingAsync(candidate).ConfigureAwait(false);
                        _bleFailures[candidate.Id] = 0;
                    }
                }
                if (attempt < maxAttempts)
                {
                    Log?.Invoke(this, $"等待手柄开机 / 进入可连接状态…（{attempt + 1}/{maxAttempts}）");
                    await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                }
                else if (candidate.Kind == BladeTransportKind.SwitchHid)
                {
                    // HID 直读打不开（例如被别的程序独占）时，自动退回系统接口再试一次。
                    transport = await TryConnectRawFallbackAsync(candidate, cancellationToken).ConfigureAwait(false);
                    if (transport != null) break;
                }
            }
        }

        if (transport == null)
        {
            Log?.Invoke(this, $"连接失败：{lastError?.Message}");
            DiagnosticsLog.Write("ble.log", $"connect fail {candidate.Name} [{candidate.Id}]: {lastError}");
            // _1B 保留在列表里，交给 AutoConnectTick 持续重试；其它设备失败即移除。
            if (!candidate.Name.Contains("_1B", StringComparison.OrdinalIgnoreCase) &&
                _candidates.TryRemove(candidate.Id, out _))
                NotifyCandidates();
            try { _watcher?.Start(); } catch { }
            return false;
        }

        lock (_sync) _current = transport;
        _currentCandidate = candidate;
        ConnectedName = transport.DisplayName;
        BatteryPercent = null;
        if (candidate.Kind == BladeTransportKind.Ble && candidate.BluetoothAddress != 0)
        {
            _lastBle = new LastBleDevice(candidate.BluetoothAddress, candidate.Path, transport.DisplayName);
            LastDeviceStore.Save(_lastBle);
        }
        _lastReport = DateTime.MinValue;
        _reconnectAttempt = 0;
        ConnectionChanged?.Invoke(this, true);
        DeviceNameChanged?.Invoke(this, transport.DisplayName);
        Log?.Invoke(this, $"已连接：{transport.DisplayName}（{transport.Kind}）");

        // 请求手柄持续上报完整状态（与 Android MapperManager 相同：屏幕初始化 → 开启映射 → 开启上报）
        await EnableReportingAsync(transport, fullSequence: true).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// HID 直读打不开（被独占等）时的兜底：改用 Windows.Gaming.Input 的 RawGameController。
    /// 成功了就返回已连接的传输，失败返回 null。
    /// </summary>
    private async Task<IBladeTransport?> TryConnectRawFallbackAsync(
        BladeDeviceCandidate candidate, CancellationToken token)
    {
        RawGameControllerTransport? fallback = null;
        try
        {
            for (var i = 0; i < RawGameControllerTransport.Count; i++)
            {
                var pad = RawGameControllerTransport.Get(i);
                if (pad == null) continue;
                if (pad.HardwareVendorId != candidate.VendorId) continue;
                if (candidate.ProductId != 0 && pad.HardwareProductId != candidate.ProductId) continue;

                fallback = new RawGameControllerTransport(i, pad.HardwareVendorId, pad.HardwareProductId);
                fallback.Log += (_, msg) => Log?.Invoke(this, msg);
                fallback.ReportReceived += OnReport;
                fallback.Disconnected += OnTransportDisconnected;
                fallback.BatteryChanged += OnBatteryChanged;
                await fallback.ConnectAsync(token).ConfigureAwait(false);
                Log?.Invoke(this, "HID 直读不可用，已改用系统接口连接（可能收不到数据）");
                DiagnosticsLog.Write("ble.log", $"switchhid fallback -> rawgamepad index={i}");
                return fallback;
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("ble.log", $"switchhid fallback fail: {ex.Message}");
            if (fallback != null)
            {
                fallback.ReportReceived -= OnReport;
                fallback.Disconnected -= OnTransportDisconnected;
                fallback.BatteryChanged -= OnBatteryChanged;
                await fallback.DisposeAsync().ConfigureAwait(false);
            }
        }
        return null;
    }

    private static int RawIndex(string id) =>        id.StartsWith("raw:", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(id.AsSpan(4), out var index)
            ? index
            : 0;

    private static uint XInputSlot(string id) =>
        id.StartsWith("xinput:", StringComparison.OrdinalIgnoreCase) &&
        uint.TryParse(id.AsSpan(7), out var slot)
            ? slot
            : 0;

    private IBladeTransport CreateTransport(BladeDeviceCandidate candidate)
    {
        if (candidate.Kind == BladeTransportKind.DualSenseHid)
        {
            var custom = TransportFactory?.Invoke(candidate);
            if (custom != null) return custom;
            throw new NotSupportedException("DS5 传输未初始化");
        }
        return candidate.Kind switch
        {        BladeTransportKind.Ble => new BleBladeTransport(candidate.BluetoothAddress, candidate.Name,
            candidate.AddressType, candidate.Path),
        BladeTransportKind.Hid => OpenHid(candidate),
        BladeTransportKind.XInput => new XInputTransport(XInputSlot(candidate.Id)),
        BladeTransportKind.RawGameController => new RawGameControllerTransport(
            RawIndex(candidate.Id), candidate.VendorId, candidate.ProductId),
        BladeTransportKind.SwitchHid => new SwitchHidTransport(
            candidate.VendorId, candidate.ProductId, candidate.Name, candidate.Path),
        BladeTransportKind.Simulator => new SimulatorTransport(),
        _ => throw new NotSupportedException($"暂不支持的连接方式：{candidate.Kind}"),
        };
    }

    private static IBladeTransport OpenHid(BladeDeviceCandidate candidate)
    {
        var device = DeviceList.Local.GetHidDevices()
            .FirstOrDefault(d => string.Equals(d.DevicePath, candidate.Path, StringComparison.OrdinalIgnoreCase));
        if (device == null) throw new InvalidOperationException("HID 设备已拔出");
        return new HidBladeTransport(device);
    }

    private async Task EnableReportingAsync(IBladeTransport transport, bool fullSequence = false)
    {
        // Switch 模式是标准 HID，没有厂商命令；DualSense 也是标准 HID，不能发风刃 0x20 帧。
        if (transport.Kind is BladeTransportKind.SwitchHid or BladeTransportKind.DualSenseHid) return;
        try
        {
            if (fullSequence && transport.Kind == BladeTransportKind.Ble)
            {
                if (transport.DisplayName.Contains("G-Touch", StringComparison.OrdinalIgnoreCase))
                {
                    // G-Touch 身份只需要 c=5 启动；继续发 c=1001/1005/40 会切回别的状态。
                    await SendPayloadAsync(transport, MsgPack.PackMap(("c", 5), ("s", 1))).ConfigureAwait(false);
                    Log?.Invoke(this, "已发送 G-Touch 启动命令");
                    return;
                }
                await SendPayloadAsync(transport, BladeProtocol.ScreenInit(1920, 1080, 0)).ConfigureAwait(false);
                await Task.Delay(120).ConfigureAwait(false);
                await SendPayloadAsync(transport, BladeProtocol.ToggleMapping(true)).ConfigureAwait(false);
                await Task.Delay(120).ConfigureAwait(false);
            }

            await SendPayloadAsync(transport, BladeProtocol.EnableGamepadReport(true)).ConfigureAwait(false);
            Log?.Invoke(this, fullSequence
                ? "已发送屏幕初始化 / 开启映射 / 开启上报命令"
                : "已发送“开启手柄上报”命令");
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"发送开启上报命令失败：{ex.Message}");
        }
    }

    private async Task SendPayloadAsync(IBladeTransport transport, byte[] payload)
    {
        foreach (var frame in BladeProtocol.BuildCommandFrames(payload, transport.Mtu))
            await transport.SendAsync(frame, _cts?.Token ?? default).ConfigureAwait(false);
    }

    private void KeepAlive()
    {
        if (BatteryPercent == null)
        {
            var approx = XInputTransport.TryGetBatteryPercent();
            if (approx.HasValue) OnBatteryChanged(this, approx.Value);
        }

        var transport = _current;
        if (transport == null || !transport.IsConnected) return;

        var idle = DateTime.UtcNow - _lastReport;
        if (idle > TimeSpan.FromSeconds(10))
        {
            _ = EnableReportingAsync(transport);
        }
    }

    private void OnReport(object? sender, BladeReport report)
    {
        _lastReport = DateTime.UtcNow;
        ReportReceived?.Invoke(this, report);
    }

    private void OnBatteryChanged(object? sender, int level)
    {
        BatteryPercent = level;
        BatteryChanged?.Invoke(this, level);
    }

    private void OnTransportDisconnected(object? sender, EventArgs e)
    {
        var name = ConnectedName;
        var candidate = _currentCandidate;
        lock (_sync) _current = null;
        _currentCandidate = null;
        ConnectedName = null;
        try { _watcher?.Start(); } catch { }
        ConnectionChanged?.Invoke(this, false);
        Log?.Invoke(this, $"{name} 已断开");

        // 断开后立即解除 _1B 的 Windows 绑定，避免“其他设备”里留下失效条目导致下次连接报错。
        if (candidate is { Kind: BladeTransportKind.Ble } &&
            candidate.Name.Contains("_1B", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500).ConfigureAwait(false);
                await ClearBleBindingAsync(candidate).ConfigureAwait(false);
            });
        }

        if (AutoReconnect && _cts is { IsCancellationRequested: false })
        {
            _ = Task.Run(async () =>
            {
                var delay = TimeSpan.FromSeconds(Math.Min(10, 1 + _reconnectAttempt++));
                await Task.Delay(delay).ConfigureAwait(false);
                if (_current != null || _cts.IsCancellationRequested) return;
                var next = Candidates.FirstOrDefault(c =>
                               c.Name.Contains("_1B", StringComparison.OrdinalIgnoreCase))
                           ?? Candidates.FirstOrDefault();
                if (next != null)
                {
                    Log?.Invoke(this, $"尝试自动重连 {next.Name} …");
                    await ConnectAsync(next).ConfigureAwait(false);
                }
            });
        }
    }

    public async Task DisconnectAsync()
    {
        IBladeTransport? transport;
        lock (_sync)
        {
            transport = _current;
            _current = null;
        }
        if (transport == null) return;
        transport.ReportReceived -= OnReport;
        transport.Disconnected -= OnTransportDisconnected;
        transport.BatteryChanged -= OnBatteryChanged;
        await transport.DisposeAsync().ConfigureAwait(false);
        ConnectedName = null;
        ConnectionChanged?.Invoke(this, false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _keepAlive?.Dispose();
        _hidScan?.Dispose();
        _autoConnect?.Dispose();
        try
        {
            if (_watcher != null)
            {
                _watcher.Received -= OnAdvertisementReceived;
                _watcher.Stop();
            }
        }
        catch { }
        await DisconnectAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
