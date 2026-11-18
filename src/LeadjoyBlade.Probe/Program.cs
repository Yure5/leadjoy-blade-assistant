using System.Text;
using HidSharp;
using LeadjoyBlade.Core.Devices;
using LeadjoyBlade.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Gaming.Input;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LeadjoyBlade.Probe;

internal static class Program
{
    private static StreamWriter? _log;
    private static BladeReport? _latest;
    private static BladeButtons _lastPrintedButtons = BladeButtons.None;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var logPath = Path.Combine(AppContext.BaseDirectory, $"probe-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };

        WriteBanner();
        try
        {
            if (args.Length > 0)
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "scan": await ScanAllAsync(args.Length > 1 ? int.Parse(args[1]) : 12); break;
                    case "ble": await ConnectBleByAddressAsync(args.Length > 1 ? args[1] : null); break;
                    case "hid": await ListenHidAsync(args.Length > 1 ? int.Parse(args[1]) : -1); break;
                    case "hidinfo": HidInfo(args.Length > 1 ? int.Parse(args[1]) : -1); break;
                    case "rawhid": await HidRawAsync(args.Length > 1 ? int.Parse(args[1]) : -1, args.Length > 2 && args[2] == "write"); break;
                    case "buttons": await ButtonWizardAsync(); break;
                    case "services": await DumpServicesAsync(args.Length > 1 ? args[1] : null); break;
                    case "listen": await ListenRawAsync(args.Length > 1 ? args[1] : null); break;
                    case "brute": await BruteAsync(); break;
                    case "winhid": await WinHidAsync(); break;
                    case "rawinput": RawInputMonitor.Run(args.Length > 1 ? int.Parse(args[1]) : 30); break;
                    case "kbdhook": KeyboardHookMonitor.Run(args.Length > 1 ? int.Parse(args[1]) : 30); break;
                    case "rawgamepad": await RawGamepadAsync(); break;
                    case "rawtrace": await RawTraceAsync(args.Length > 1 ? int.Parse(args[1]) : 30); break;
                    case "guitrace": GuiTrace(args.Length > 1 ? int.Parse(args[1]) : 60); break;
                    case "switchkeep": SwitchKeepAlive(args.Length > 1 ? int.Parse(args[1]) : 30); break;
                    case "hidgatt": await HidGattAsync(args.Length > 1 ? args[1] : null); break;
                    case "hidnative": HidNativeInfo.Run(); break;
                    case "hidread": HidNativeInfo.RunRead(args.Length > 1 ? int.Parse(args[1]) : 20); break;
                    case "blescan": await BleScanProbe.RunAsync(args.Length > 1 ? int.Parse(args[1]) : 30); break;
                    case "query": await BatteryQueryProbe.RunAsync(args.Length > 1 ? args[1] : "", args.Length > 2 ? args[2] : "237,1,2,3,4,10,20,30,40,100,200,255"); break;
                    case "radio": await RadioAsync(args.Length > 1 && args[1] == "on"); break;
                    case "menu": await MenuAsync(); break;
                    default: WriteHelp(); break;
                }
            }
            else
            {
                await AutoAsync();
            }
        }
        catch (Exception ex)
        {
            Write($"发生错误：{ex}");
        }
        finally
        {
            Write($"\n日志已保存：{logPath}");
            Write("按任意键退出…");
            try { Console.ReadKey(true); } catch { }
            _log?.Dispose();
        }
        return 0;
    }

    private static void WriteBanner()
    {
        Write("==============================================================");
        Write("  风刃手柄探测工具 (Leadjoy Probe)  v1.1");
        Write("  用于识别手柄连接方式、按键位图与原始报文");
        Write("==============================================================");
        Write($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Write($"系统：{Environment.OSVersion}");
    }

    private static async Task MenuAsync()
    {
        while (true)
        {
            Write("");
            Write("请选择操作：");
            Write("  1. 扫描全部蓝牙设备 + USB HID + PnP 设备（12 秒）");
            Write("  2. 连接蓝牙手柄（列出全部广播，可任选）");
            Write("  3. 列出 USB HID 设备");
            Write("  4. 监听指定 HID 设备的输入报告");
            Write("  5. 按键向导：逐个按键记录位图");
            Write("  6. 检测 XInput 手柄");
            Write("  7. 列出 USB / 蓝牙 PnP 设备");
            Write("  8. 查看 HID 报告描述符（判断厂商通道）");
            Write("  9. 监听原始 HID 输入报告");
            Write("  0. 退出");
            Write("");
            Console.Write("输入序号：");
            var choice = Console.ReadLine()?.Trim();
            switch (choice)
            {
                case "1": await ScanAllAsync(); break;
                case "2": await ConnectBleInteractiveAsync(); break;
                case "3": ListHidDevices(); break;
                case "4": await ListenHidAsync(-1); break;
                case "5": await ButtonWizardAsync(); break;
                case "6": DetectXInput(); break;
                case "7": ListPnpDevices(); break;
                case "8": HidInfo(-1); break;
                case "9": await HidRawAsync(-1, false); break;
                case "0": return;
                default: Write("无效输入"); break;
            }
        }
    }

    private static async Task AutoAsync()
    {
        Write("自动诊断模式：无需任何操作，请保持手柄开机并靠近电脑。");
        Write("重要：风刃手柄需按 Home + Y 进入 G-Touch 模式，电脑才能看到厂商蓝牙服务。");
        Write("（如需手动菜单，请在 5 秒内按 M 键）");
        for (var i = 5; i > 0; i--)
        {
            if (Console.KeyAvailable && char.ToUpperInvariant(Console.ReadKey(true).KeyChar) == 'M')
            {
                await MenuAsync();
                return;
            }
            Console.Write($"\r{i} 秒后开始…");
            await Task.Delay(1000);
        }
        Write("\n");

        var list = await ScanBleAsync(10);
        Write("---- 蓝牙广播设备 ----");
        for (var i = 0; i < list.Count; i++)
        {
            var info = list[i];
            Write($"  [{i,2}] {info.Address:X12}  {info.Rssi,4}dBm  {info.Name,-32} 类型:{info.Type,-24} 地址类型:{info.AddressType,-9} 服务:{(string.IsNullOrEmpty(info.Services) ? "-" : info.Services)}");
        }
        if (list.Count == 0) Write("  （未发现蓝牙广播）");

        // 先按名称/服务寻找，再从信号最强的设备逐一尝试 0x8650 服务
        var preferred = list.FirstOrDefault(x =>
                            x.Name.Contains("blade", StringComparison.OrdinalIgnoreCase) ||
                            x.Name.Contains("wind", StringComparison.OrdinalIgnoreCase) ||
                            x.Name.Contains("风刃", StringComparison.OrdinalIgnoreCase) ||
                            x.Services.Contains("00008650", StringComparison.OrdinalIgnoreCase))
                        ?? list.Where(x => x.Rssi > -85).Take(5).FirstOrDefault();

        if (preferred != null)
        {
            Write($"\n尝试连接：{preferred.Name} [{preferred.Address:X12}]");
            BleBladeTransport? transport = null;
            try
            {
                transport = new BleBladeTransport(preferred.Address,
                    string.IsNullOrWhiteSpace(preferred.Name) ? "Blade" : preferred.Name);
                transport.Log += (_, msg) => Write($"[BLE] {msg}");
                transport.ReportReceived += (_, report) => OnReport(report);
                transport.FrameReceived += (_, frame) => Write($"[帧] type=0x{frame.Type:X2} len={frame.Payload.Length} {frame.ToHex()}");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await transport.ConnectAsync(cts.Token);
                await EnableReportingAsync(transport);
                Write("连接成功！现在请依次按动手柄按键（Fn、M1-M8、背键、ABXY、十字键、摇杆、扳机）。");
                Write("将自动记录 75 秒…");
                await ListenReportsAsync(75);
            }
            catch (Exception ex)
            {
                Write($"连接或监听失败：{ex.Message}");
            }
            finally
            {
                if (transport != null) await transport.DisposeAsync();
            }
        }
        else
        {
            Write("未发现可尝试的蓝牙设备。");
        }

        Write("");
        ListHidDevices();
        Write("");
        ListPnpDevices();
    }

    private static async Task<List<AdvInfo>> ScanBleAsync(int seconds)
    {
        var results = new Dictionary<ulong, AdvInfo>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            var name = e.Advertisement.LocalName;
            var services = string.Join(",", e.Advertisement.ServiceUuids.Select(u => u.ToString("D")));
            var advType = e.AdvertisementType.ToString();
            var addrType = e.BluetoothAddressType.ToString();
            lock (results)
            {
                results[e.BluetoothAddress] = new AdvInfo(e.BluetoothAddress, name, e.RawSignalStrengthInDBm, services, advType, addrType);
            }
        };
        watcher.Start();
        Write($"正在扫描蓝牙广播 {seconds} 秒…");
        for (var i = seconds; i > 0; i--)
        {
            Console.Write($"\r剩余 {i,2} 秒…（已发现 {results.Count} 个设备）");
            await Task.Delay(1000);
        }
        watcher.Stop();
        Write("\n");
        lock (results) return results.Values.OrderByDescending(r => r.Rssi).ToList();
    }

    private static async Task ScanAllAsync(int seconds = 12)
    {
        var list = await ScanBleAsync(seconds);
        Write("---- 蓝牙广播设备 ----");
        for (var i = 0; i < list.Count; i++)
        {
            var info = list[i];
            Write($"  [{i,2}] {info.Address:X12}  {info.Rssi,4}dBm  {info.Name,-32} 类型:{info.Type,-24} 地址类型:{info.AddressType,-9} 服务:{(string.IsNullOrEmpty(info.Services) ? "-" : info.Services)}");
        }
        if (list.Count == 0) Write("  （未发现任何蓝牙广播）");

        Write("");
        ListHidDevices();
        Write("");
        ListPnpDevices();
    }

    private static async Task<AdvInfo?> PickBleAsync()
    {
        var list = await ScanBleAsync(10);
        if (list.Count == 0)
        {
            Write("未发现蓝牙设备。请确认手柄处于蓝牙模式，且未被手机 App 占用。");
            return null;
        }

        Write("---- 可用蓝牙设备 ----");
        for (var i = 0; i < list.Count; i++)
        {
            var info = list[i];
            Write($"  [{i,2}] {info.Address:X12}  {info.Rssi,4}dBm  {info.Name,-32} 类型:{info.Type,-24} 地址类型:{info.AddressType,-9} 服务:{(string.IsNullOrEmpty(info.Services) ? "-" : info.Services)}");
        }
        Console.Write("选择要连接的序号（默认 0）：");
        var input = Console.ReadLine();
        var index = int.TryParse(input, out var n) ? n : 0;
        return list[Math.Clamp(index, 0, list.Count - 1)];
    }

    private static async Task ConnectBleInteractiveAsync()
    {
        var info = await PickBleAsync();
        if (info == null) return;

        await using var transport = new BleBladeTransport(info.Address, string.IsNullOrWhiteSpace(info.Name) ? $"BLE {info.Address:X12}" : info.Name);
        transport.Log += (_, msg) => Write($"[BLE] {msg}");
        transport.ReportReceived += (_, report) => OnReport(report);
        transport.FrameReceived += (_, frame) => Write($"[帧] type=0x{frame.Type:X2} len={frame.Payload.Length} {frame.ToHex()}");

        try
        {
            await transport.ConnectAsync();
        }
        catch (Exception ex)
        {
            Write($"连接失败：{ex.Message}");
            Write("提示：如果提示找不到 0x8650 服务，说明该设备不是风刃手柄，或固件未开放厂商通道。");
            return;
        }

        await EnableReportingAsync(transport);
        Write("连接成功。现在请依次按动手柄按键，实时输出如下（90 秒）：");
        await ListenReportsAsync(90);
    }

    private static async Task RadioAsync(bool turnOn)
    {
        var radios = await Radio.GetRadiosAsync();
        foreach (var radio in radios)
        {
            Write($"无线电：{radio.Name} 类型={radio.Kind} 状态={radio.State}");
            if (radio.Kind == RadioKind.Bluetooth && turnOn && radio.State != RadioState.On)
            {
                var access = await Radio.RequestAccessAsync();
                Write($"请求访问：{access}");
                if (access == RadioAccessStatus.Allowed)
                {
                    await radio.SetStateAsync(RadioState.On);
                    Write($"已尝试开启，当前状态：{radio.State}");
                }
            }
        }
    }

    private static string BufferHex(Windows.Storage.Streams.IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return Convert.ToHexString(bytes);
    }

    private static async Task HidGattAsync(string? addressText)
    {
        ulong address = 0;
        string name = "Blade";
        if (!string.IsNullOrWhiteSpace(addressText) &&
            ulong.TryParse(addressText, System.Globalization.NumberStyles.HexNumber, null, out var parsed))
        {
            address = parsed;
        }
        else
        {
            var list = await ScanBleAsync(8);
            var devInfo = list.FirstOrDefault(x => x.Name.Contains("Blade", StringComparison.OrdinalIgnoreCase));
            if (devInfo == null) { Write("未发现 Blade Plus 广播"); return; }
            address = devInfo.Address;
            name = devInfo.Name;
        }
        Write($"连接 {name} [{address:X12}]…");

        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Public);
        if (device == null) { Write("无法创建设备"); return; }

        var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        Write($"services status={services.Status}");
        var hid = services.Services.FirstOrDefault(s => s.Uuid == new Guid("00001812-0000-1000-8000-00805f9b34fb"));
        if (hid == null) { Write("未找到 0x1812"); return; }

        var chars = await hid.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        foreach (var c in chars.Characteristics)
            Write($"  char {c.Uuid} props={c.CharacteristicProperties}");

        var map = chars.Characteristics.FirstOrDefault(c => c.Uuid == new Guid("00002a4b-0000-1000-8000-00805f9b34fb"));
        if (map != null)
        {
            var mapValue = await map.ReadValueAsync(BluetoothCacheMode.Uncached);
            Write($"Report Map status={mapValue.Status} len={mapValue.Value?.Length}");
            if (mapValue.Value != null) Write(BufferHex(mapValue.Value));
        }
        else Write("未找到 Report Map 0x2A4B");

        var report = chars.Characteristics.FirstOrDefault(c => c.Uuid == new Guid("00002a4d-0000-1000-8000-00805f9b34fb"));
        if (report == null) { Write("未找到 Report 0x2A4D"); return; }
        report.ValueChanged += (_, e) => Write($"[report] {BufferHex(e.CharacteristicValue)}");
        var cccd = await report.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
        Write($"订阅 Report: {cccd}");
        Write("监听 30 秒，请按键…");
        await Task.Delay(30000);
    }

    /// <summary>逐秒打印每个 HID 手柄的原始读数与“时间戳变化次数”，用来判断是否真的收到新报文。</summary>
    private static async Task RawTraceAsync(int seconds)
    {
        var pads = RawGameController.RawGameControllers;
        Write($"RawGameController 数量：{pads.Count}");
        if (pads.Count == 0) { Write("没有 HID 手柄。"); return; }
        for (var i = 0; i < pads.Count; i++)
        {
            var pad = pads[i];
            Write($"  [{i}] {pad.DisplayName} VID={pad.HardwareVendorId:X4} PID={pad.HardwareProductId:X4} " +
                  $"buttons={pad.ButtonCount} switches={pad.SwitchCount} axes={pad.AxisCount}");
        }

        Write($"连续采样 {seconds} 秒（每 200ms 读一次，每秒打印一行），请持续按键…");
        var buttons = pads.Select(p => new bool[p.ButtonCount]).ToArray();
        var switches = pads.Select(p => new GameControllerSwitchPosition[p.SwitchCount]).ToArray();
        var axes = pads.Select(p => new double[p.AxisCount]).ToArray();
        var lastTs = new ulong[pads.Count];
        var changes = new int[pads.Count];

        var end = DateTime.UtcNow.AddSeconds(seconds);
        var nextPrint = DateTime.UtcNow;
        while (DateTime.UtcNow < end)
        {
            for (var i = 0; i < pads.Count; i++)
            {
                var ts = pads[i].GetCurrentReading(buttons[i], switches[i], axes[i]);
                if (ts != lastTs[i])
                {
                    lastTs[i] = ts;
                    changes[i]++;
                }
            }

            if (DateTime.UtcNow >= nextPrint)
            {
                nextPrint = DateTime.UtcNow.AddSeconds(1);
                for (var i = 0; i < pads.Count; i++)
                {
                    var pressed = string.Join(",", buttons[i].Select((b, k) => (b, k)).Where(x => x.b).Select(x => x.k));
                    Write($"[{i}] ts={lastTs[i]} 变化={changes[i]} 按下=[{pressed}] " +
                          $"十字=[{string.Join(",", switches[i].Select(s => s.ToString()))}] " +
                          $"轴=[{string.Join(",", axes[i].Select(a => a.ToString("F3")))}]");
                }
            }

            await Task.Delay(100);
        }
        Write("采样结束。");
    }

    /// <summary>
    /// 带窗口消息循环地读取手柄：同时打印 RawGameController 与标准 Gamepad 两套 API 的读数。
    /// RawGameController 拿不到数据时，用 Gamepad 结果判断是不是整个 Windows 侧都没数据。
    /// </summary>
    private static void GuiTrace(int seconds)
    {
        Write($"带消息循环采样 {seconds} 秒，请持续按键…");
        var form = new System.Windows.Forms.Form
        {
            Text = "LeadjoyProbe",
            Width = 260,
            Height = 80,
            ShowInTaskbar = false,
            WindowState = System.Windows.Forms.FormWindowState.Minimized,
        };
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        var end = DateTime.UtcNow.AddSeconds(seconds);
        var lastRaw = "";
        var lastPad = "";

        timer.Tick += (_, _) =>
        {
            try
            {
                var pads = RawGameController.RawGameControllers;
                var padLines = new List<string>();
                for (var i = 0; i < pads.Count; i++)
                {
                    var pad = pads[i];
                    var b = new bool[pad.ButtonCount];
                    var s = new GameControllerSwitchPosition[pad.SwitchCount];
                    var a = new double[pad.AxisCount];
                    var ts = pad.GetCurrentReading(b, s, a);
                    var pressed = string.Join(",", b.Select((v, k) => (v, k)).Where(x => x.v).Select(x => x.k));
                    padLines.Add($"raw[{i}] {pad.HardwareVendorId:X4}:{pad.HardwareProductId:X4} ts={ts} " +
                                 $"按下=[{pressed}] 十字=[{string.Join(",", s.Select(x => x.ToString()))}] " +
                                 $"轴=[{string.Join(",", a.Select(x => x.ToString("F2")))}]");
                }
                var padText = $"RawGameController={pads.Count} " + string.Join(" | ", padLines);
                if (padText != lastRaw) { lastRaw = padText; Write(padText); }

                var gamepads = Gamepad.Gamepads;
                var gpLines = new List<string>();
                for (var i = 0; i < gamepads.Count; i++)
                {
                    var g = gamepads[i];
                    var r = g.GetCurrentReading();
                    gpLines.Add($"gamepad[{i}] ts={r.Timestamp} " +
                                $"buttons=0x{(ushort)r.Buttons:X4} LT={r.LeftTrigger} RT={r.RightTrigger} " +
                                $"LX={r.LeftThumbstickX:F2} LY={r.LeftThumbstickY:F2}");
                }
                var gpText = $"Gamepad={gamepads.Count} " + string.Join(" | ", gpLines);
                if (gpText != lastPad) { lastPad = gpText; Write(gpText); }
            }
            catch (Exception ex)
            {
                Write($"采样异常：{ex.Message}");
            }

            if (DateTime.UtcNow <= end) return;
            timer.Stop();
            form.Close();
        };

        form.Load += (_, _) => timer.Start();
        System.Windows.Forms.Application.Run(form);
        Write("采样结束。");
    }

    private static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x0F : crc << 1);
        }
        return crc;
    }

    /// <summary>
    /// 向 Switch 模式手柄的 HID 接口发一次握手 + 周期性空振动报文。
    /// 很多克隆固件只在收到输出报文后才进入“全量上报模式”/持续上报，
    /// 表现就是连上后只有第一帧、之后按键全无。
    /// </summary>
    private static void SwitchKeepAlive(int seconds)
    {
        var devices = DeviceList.Local.GetHidDevices()
            .Where(d => d.VendorID == 0x057E && d.ProductID == 0x2009)
            .ToList();
        Write($"057E:2009 HID 设备数：{devices.Count}");
        foreach (var d in devices)
        {
            Write($"  {d.DevicePath}");
            Write($"    in={d.GetMaxInputReportLength()} out={d.GetMaxOutputReportLength()}");
        }

        var target = devices.FirstOrDefault();
        if (target == null) { Write("没有可用的 HID 接口。"); return; }

        if (!target.TryOpen(out var stream))
        {
            Write("打开失败（可能被占用）。");
            return;
        }

        using (stream)
        {
            stream.ReadTimeout = 200;
            Write($"已打开：in={stream.Device.GetMaxInputReportLength()} out={stream.Device.GetMaxOutputReportLength()}");

            void Send(byte[] report)
            {
                try
                {
                    stream.Write(report, 0, report.Length);
                    Write($"  [out] {Convert.ToHexString(report)}");
                }
                catch (Exception ex) { Write($"  写失败：{ex.Message}"); }
            }

            // 0x01 = 振动 + 子命令（蓝牙模式，最后一字节 CRC8）
            byte[] Command(byte sub, byte arg)
            {
                var buf = new byte[49];
                buf[0] = 0x01;
                buf[1] = 0x00; buf[2] = 0x01; buf[3] = 0x40; buf[4] = 0x40;
                buf[5] = 0x00; buf[6] = 0x01; buf[7] = 0x40; buf[8] = 0x40;
                buf[9] = sub;
                buf[10] = arg;
                buf[48] = Crc8(buf.AsSpan(1, 47));
                return buf;
            }

            Send(Command(0x03, 0x30)); // 设置输入报文模式 = 全量 0x30
            Send(Command(0x40, 0x01)); // 打开 IMU
            Send(Command(0x48, 0x01)); // 打开振动

            var end = DateTime.UtcNow.AddSeconds(seconds);
            var nextRumble = DateTime.UtcNow.AddSeconds(1);
            var lastTs = 0UL;
            var reads = 0;
            var nextReport = DateTime.UtcNow;

            while (DateTime.UtcNow < end)
            {
                // 1) 原始读取，看看 HID 层到底有没有报文
                var buffer = new byte[Math.Max(64, target.GetMaxInputReportLength())];
                try
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        reads++;
                        Write($"  [in] {Convert.ToHexString(buffer.AsSpan(0, read))}");
                    }
                }
                catch (TimeoutException) { }
                catch (Exception ex) { Write($"  读失败：{ex.Message}"); break; }

                // 2) 每秒发一次空振动保活（0x10 = 仅振动，9 字节）
                if (DateTime.UtcNow >= nextRumble)
                {
                    nextRumble = DateTime.UtcNow.AddSeconds(1);
                    var rumble = new byte[9] { 0x10, 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 };
                    Send(rumble);
                }

                // 3) 每秒打印 WinRT 侧时间戳
                if (DateTime.UtcNow >= nextReport)
                {
                    nextReport = DateTime.UtcNow.AddSeconds(1);
                    var pads = RawGameController.RawGameControllers;
                    var ts = 0UL;
                    var pressed = "";
                    for (var i = 0; i < pads.Count; i++)
                    {
                        var pad = pads[i];
                        if (pad.HardwareVendorId != 0x057E) continue;
                        var b = new bool[pad.ButtonCount];
                        var s = new GameControllerSwitchPosition[pad.SwitchCount];
                        var a = new double[pad.AxisCount];
                        ts = pad.GetCurrentReading(b, s, a);
                        pressed = string.Join(",", b.Select((v, k) => (v, k)).Where(x => x.v).Select(x => x.k));
                        break;
                    }
                    Write($"  WinRT ts={ts}(上次{lastTs}) 原始读取={reads} 按下=[{pressed}]");
                    lastTs = ts;
                }
            }
            Write("结束。");
        }
    }

    private static async Task RawGamepadAsync()
    {
        var pads = RawGameController.RawGameControllers;
        Write($"RawGameController 数量：{pads.Count}");
        foreach (var pad in pads)
        {
            Write($"  {pad.DisplayName}  VID={pad.HardwareVendorId:X4} PID={pad.HardwareProductId:X4} buttons={pad.ButtonCount} switches={pad.SwitchCount} axes={pad.AxisCount}");
            for (var i = 0; i < pad.ButtonCount; i++)
                Write($"    button[{i}] = {pad.GetButtonLabel(i)}");

            var buttons = new bool[pad.ButtonCount];
            var switches = new GameControllerSwitchPosition[pad.SwitchCount];
            var axes = new double[pad.AxisCount];
            var last = "";
            Write("    监听 15 秒，请按键…");
            var end = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < end)
            {
                var timestamp = pad.GetCurrentReading(buttons, switches, axes);
                if (timestamp != 0)
                {
                    var pressed = string.Join(",", buttons.Select((b, i) => (b, i)).Where(x => x.b).Select(x => x.i));
                    var state = pressed + " | " + string.Join(",", switches.Select(s => s.ToString())) + " | " + string.Join(",", axes.Select(a => a.ToString("F2")));
                    if (state != last)
                    {
                        last = state;
                        Write($"    [pad] {state}");
                    }
                }
                await Task.Delay(50);
            }
        }
    }

    private static async Task WinHidAsync()
    {
        var selector = Windows.Devices.HumanInterfaceDevice.HidDevice.GetDeviceSelector(0x01, 0x05, 0x054C, 0x09CC);
        var infos = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
        Write($"WinRT HID 设备数：{infos.Count}");
        foreach (var info in infos)
        {
            Write($"  {info.Name}  {info.Id}");
            try
            {
                using var device = await Windows.Devices.HumanInterfaceDevice.HidDevice.FromIdAsync(info.Id, FileAccessMode.ReadWrite);
                if (device == null) { Write("    (无法打开)"); continue; }
                Write($"    VID={device.VendorId:X4} PID={device.ProductId:X4} usagePage=0x{device.UsagePage:X4} usage=0x{device.UsageId:X4}");
                if (device.VendorId != 0x054C && !info.Name.Contains("Wireless", StringComparison.OrdinalIgnoreCase)) continue;

                device.InputReportReceived += (_, e) =>
                {
                    var bytes = new byte[e.Report.Data.Length];
                    using var reader = DataReader.FromBuffer(e.Report.Data);
                    reader.ReadBytes(bytes);
                    Write($"    [report] id=0x{e.Report.Id:X2} {Convert.ToHexString(bytes)}");
                };
                Write("    监听 30 秒，请按键…");
                await Task.Delay(30000);
            }
            catch (Exception ex) { Write($"    异常：{ex.Message}"); }
        }
    }

    private static async Task BruteAsync()
    {
        var list = await ScanBleAsync(8);
        var dev = list.FirstOrDefault(x => x.Name.Contains("Blade", StringComparison.OrdinalIgnoreCase));
        if (dev == null) { Write("未发现 Blade Plus 广播"); return; }
        Write($"连接 {dev.Name} [{dev.Address:X12}]…");

        await using var transport = new BleBladeTransport(dev.Address, dev.Name, Windows.Devices.Bluetooth.BluetoothAddressType.Public);
        var count = new int[1];
        var big = new List<string>();
        transport.Log += (_, msg) => Write($"[BLE] {msg}");
        transport.RawNotify += (_, bytes) =>
        {
            Interlocked.Increment(ref count[0]);
            if (bytes.Length > 7) lock (big) big.Add(Convert.ToHexString(bytes));
        };
        try { await transport.ConnectAsync(); }
        catch (Exception ex) { Write($"连接失败：{ex.Message}"); return; }

        var candidates = Enumerable.Range(1, 80).Concat(new[] { 100, 200, 201, 300, 400, 1000, 1001, 1002, 1003, 1005, 1006, 1010, 1011, 1012, 1013, 1014, 1015, 1020, 1100, 1200 }).Distinct();
        foreach (var n in candidates)
        {
            count[0] = 0;
            lock (big) big.Clear();
            var payload = MsgPack.PackMap(("c", n), ("s", 1));
            try
            {
                foreach (var frame in BladeProtocol.BuildCommandFrames(payload, transport.Mtu))
                    await transport.SendAsync(frame);
            }
            catch { }
            await Task.Delay(450);
            var c = count[0];
            List<string> b;
            lock (big) b = big.ToList();
            if (c > 3 || b.Count > 0)
                Write($"c={n} -> {c} 条通知 {(b.Count > 0 ? string.Join(";", b.Take(3).Select(x => x.Length + ":" + x)) : "")}");
        }

        Write("扫描完成。");
    }

    private static async Task ListenRawAsync(string? addressText)
    {
        if (string.IsNullOrWhiteSpace(addressText) ||
            !ulong.TryParse(addressText, System.Globalization.NumberStyles.HexNumber, null, out var address))
        {
            Write("用法：LeadjoyProbe.exe listen <12位十六进制蓝牙地址>");
            return;
        }

        await using var transport = new BleBladeTransport(address, "Blade", Windows.Devices.Bluetooth.BluetoothAddressType.Public);
        transport.Log += (_, msg) => Write($"[BLE] {msg}");
        transport.RawNotify += (_, bytes) => Write($"[raw] {Convert.ToHexString(bytes)}");
        transport.Disconnected += (_, _) => Write("[BLE] 已断开");
        try
        {
            await transport.ConnectAsync();
        }
        catch (Exception ex)
        {
            Write($"连接失败：{ex.Message}");
            return;
        }

        Write("不发送任何命令，只监听 30 秒。请按键，观察是否有 [raw] 输出…");
        await Task.Delay(30000);
    }

    private static async Task DumpServicesAsync(string? addressText)
    {
        if (string.IsNullOrWhiteSpace(addressText) ||
            !ulong.TryParse(addressText, System.Globalization.NumberStyles.HexNumber, null, out var address))
        {
            Write("用法：LeadjoyProbe.exe services <12位十六进制蓝牙地址>");
            return;
        }

        using var device = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device == null) { Write("无法创建设备对象"); return; }
        Write($"设备：{device.Name}  地址：{address:X12}  状态：{device.ConnectionStatus}");

        GattDeviceServicesResult result;
        try
        {
            result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        }
        catch (Exception ex)
        {
            Write($"枚举服务异常：{ex.Message}");
            return;
        }
        Write($"服务枚举状态：{result.Status}");
        foreach (var service in result.Services)
        {
            Write($"  服务 {service.Uuid}");
            try
            {
                var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                foreach (var ch in chars.Characteristics)
                {
                    Write($"    特征 {ch.Uuid}  属性 {ch.CharacteristicProperties}");
                    if ((ch.CharacteristicProperties & GattCharacteristicProperties.Read) != 0)
                    {
                        try
                        {
                            var value = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                            if (value.Status == GattCommunicationStatus.Success && value.Value != null)
                                Write($"      值 {BufferHex(value.Value)}");
                        }
                        catch (Exception ex) { Write($"      读取失败：{ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) { Write($"    特征枚举失败：{ex.Message}"); }
        }

        if (result.Services.Any(s => s.Uuid == BleBladeTransport.ServiceUuid))
        {
            Write("发现 0x8650，尝试协议连接并监听 10 秒…");
            await using var transport = new BleBladeTransport(address, device.Name);
            transport.Log += (_, msg) => Write($"[BLE] {msg}");
            transport.ReportReceived += (_, report) => OnReport(report);
            transport.FrameReceived += (_, frame) => Write($"[帧] type=0x{frame.Type:X2} len={frame.Payload.Length} {frame.ToHex()}");
            try
            {
                await transport.ConnectAsync();
                await EnableReportingAsync(transport);
                await ListenReportsAsync(10);
            }
            catch (Exception ex) { Write($"协议连接失败：{ex.Message}"); }
        }
    }

    private static async Task ConnectBleByAddressAsync(string? addressText)
    {
        if (string.IsNullOrWhiteSpace(addressText) ||
            !ulong.TryParse(addressText, System.Globalization.NumberStyles.HexNumber, null, out var address))
        {
            Write("用法：LeadjoyProbe.exe ble <12位十六进制蓝牙地址>");
            return;
        }

        await using var transport = new BleBladeTransport(address, "Blade", Windows.Devices.Bluetooth.BluetoothAddressType.Random);
        transport.Log += (_, msg) => Write($"[BLE] {msg}");
        transport.ReportReceived += (_, report) => OnReport(report);
        transport.FrameReceived += (_, frame) => Write($"[帧] type=0x{frame.Type:X2} len={frame.Payload.Length} {frame.ToHex()}");
        await transport.ConnectAsync();
        await EnableReportingAsync(transport);
        Write("监听 90 秒…");
        await ListenReportsAsync(90);
    }

    private static async Task EnableReportingAsync(IBladeTransport transport)
    {
        try
        {
            foreach (var frame in BladeProtocol.BuildCommandFrames(BladeProtocol.EnableGamepadReport(true), transport.Mtu))
                await transport.SendAsync(frame);
            Write("已发送“开启手柄上报”命令");
        }
        catch (Exception ex)
        {
            Write($"发送开启命令失败：{ex.Message}");
        }
    }

    private static async Task ListenReportsAsync(int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        _lastPrintedButtons = BladeButtons.None;
        while (DateTime.UtcNow < end)
        {
            var report = _latest;
            if (report != null && report.Buttons != _lastPrintedButtons)
            {
                _lastPrintedButtons = report.Buttons;
                Write($"[{DateTime.Now:HH:mm:ss.fff}] 0x{(uint)report.Buttons:X8}  {report.DescribePressed()}");
                Write($"    L=({report.LeftX},{report.LeftY}) R=({report.RightX},{report.RightY}) LT={report.LeftTrigger} RT={report.RightTrigger}");
            }
            await Task.Delay(20);
        }
    }

    private static void OnReport(BladeReport report) => _latest = report;

    private static void ListHidDevices()
    {
        Write("---- USB HID 设备 ----");
        var index = 0;
        foreach (var device in DeviceList.Local.GetHidDevices().OrderBy(d => d.VendorID).ThenBy(d => d.ProductID))
        {
            string product, manufacturer;
            try { product = device.GetProductName() ?? ""; } catch { product = ""; }
            try { manufacturer = device.GetManufacturer() ?? ""; } catch { manufacturer = ""; }

            Write($"  [{index}] VID={device.VendorID:X4} PID={device.ProductID:X4}  报告长度: IN={device.GetMaxInputReportLength()} OUT={device.GetMaxOutputReportLength()} FEATURE={device.GetMaxFeatureReportLength()}");
            Write($"       {manufacturer} / {product}");
            Write($"       {device.DevicePath}");
            index++;
        }
        if (index == 0) Write("  （未发现 HID 设备）");
    }

    private static async Task ListenHidAsync(int requestedIndex)
    {
        var devices = DeviceList.Local.GetHidDevices().OrderBy(d => d.VendorID).ThenBy(d => d.ProductID).ToList();
        if (devices.Count == 0) { Write("没有 HID 设备"); return; }
        if (requestedIndex < 0)
        {
            ListHidDevices();
            Console.Write("选择 HID 序号：");
            if (!int.TryParse(Console.ReadLine(), out var idx)) return;
            requestedIndex = idx;
        }
        if (requestedIndex < 0 || requestedIndex >= devices.Count) { Write("序号无效"); return; }

        await using var transport = new HidBladeTransport(devices[requestedIndex]);
        transport.Log += (_, msg) => Write($"[HID] {msg}");
        transport.ReportReceived += (_, report) => OnReport(report);
        transport.FrameReceived += (_, frame) => Write($"[帧] type=0x{frame.Type:X2} len={frame.Payload.Length} {frame.ToHex()}");
        await transport.ConnectAsync();
        await EnableReportingAsync(transport);

        Write("监听 45 秒，请按动手柄按键…");
        await ListenReportsAsync(45);
    }

    private static void HidInfo(int requestedIndex)
    {
        var devices = DeviceList.Local.GetHidDevices().OrderBy(d => d.VendorID).ThenBy(d => d.ProductID).ToList();
        if (requestedIndex < 0)
        {
            ListHidDevices();
            Console.Write("选择 HID 序号：");
            if (!int.TryParse(Console.ReadLine(), out var idx)) return;
            requestedIndex = idx;
        }
        if (requestedIndex < 0 || requestedIndex >= devices.Count) { Write("序号无效"); return; }

        var device = devices[requestedIndex];
        Write($"设备：{device.VendorID:X4}:{device.ProductID:X4}  {device.DevicePath}");
        Write($"报告长度：IN={device.GetMaxInputReportLength()} OUT={device.GetMaxOutputReportLength()} FEATURE={device.GetMaxFeatureReportLength()}");
        try
        {
            var raw = device.GetRawReportDescriptor();
            Write($"报告描述符（{raw.Length} 字节）：");
            Write(Convert.ToHexString(raw));
        }
        catch (Exception ex) { Write($"读取描述符失败：{ex.Message}"); }
    }

    private static async Task HidRawAsync(int requestedIndex, bool sendEnable)
    {
        var devices = DeviceList.Local.GetHidDevices().OrderBy(d => d.VendorID).ThenBy(d => d.ProductID).ToList();
        if (requestedIndex < 0)
        {
            ListHidDevices();
            Console.Write("选择 HID 序号：");
            if (!int.TryParse(Console.ReadLine(), out var idx)) return;
            requestedIndex = idx;
        }
        if (requestedIndex < 0 || requestedIndex >= devices.Count) { Write("序号无效"); return; }

        var device = devices[requestedIndex];
        Write($"打开 {device.VendorID:X4}:{device.ProductID:X4} {device.DevicePath}");
        using var stream = device.Open();
        stream.ReadTimeout = 200;

        if (sendEnable)
        {
            var frame = BladeProtocol.BuildCommandFrames(BladeProtocol.EnableGamepadReport(true), Math.Max(64, device.GetMaxOutputReportLength())).First();
            var output = new byte[Math.Max(frame.Length + 1, device.GetMaxOutputReportLength())];
            output[0] = 0;
            frame.CopyTo(output, 1);
            try
            {
                stream.Write(output, 0, output.Length);
                Write($"已写入输出报告（{output.Length} 字节）：{Convert.ToHexString(output.AsSpan(0, Math.Min(output.Length, 32)))}…");
            }
            catch (Exception ex) { Write($"写入失败：{ex.Message}"); }
        }

        Write("监听 15 秒原始输入报告，请按动手柄按键…");
        var buffer = new byte[Math.Max(64, device.GetMaxInputReportLength())];
        var end = DateTime.UtcNow.AddSeconds(15);
        var last = string.Empty;
        while (DateTime.UtcNow < end)
        {
            int read;
            try { read = stream.Read(buffer, 0, buffer.Length); }
            catch (TimeoutException) { continue; }
            catch (Exception ex) { Write($"读取异常：{ex.Message}"); break; }
            if (read <= 0) continue;
            var hex = Convert.ToHexString(buffer.AsSpan(0, Math.Min(read, 64)));
            if (hex == last) continue;
            last = hex;
            Write($"[{DateTime.Now:HH:mm:ss.fff}] len={read}  {hex}");
        }
        await Task.CompletedTask;
    }

    private static async Task ButtonWizardAsync()
    {
        var info = await PickBleAsync();
        if (info == null) return;

        await using var transport = new BleBladeTransport(info.Address, string.IsNullOrWhiteSpace(info.Name) ? "Blade" : info.Name);
        transport.Log += (_, msg) => Write($"[BLE] {msg}");
        transport.ReportReceived += (_, report) => _latest = report;
        try
        {
            await transport.ConnectAsync();
        }
        catch (Exception ex)
        {
            Write($"连接失败：{ex.Message}");
            return;
        }
        await EnableReportingAsync(transport);

        Write("\n按键向导开始。请按提示按住对应按键约 1 秒。超时会跳过。\n");
        var found = new Dictionary<BladeButtons, uint>();
        foreach (var button in BladeButtonsExtensions.All)
        {
            Write($"请按住：{button.ToDisplayName()} …");
            var deadline = DateTime.UtcNow.AddSeconds(8);
            var detected = false;
            while (DateTime.UtcNow < deadline)
            {
                var report = _latest;
                if (report != null && report.IsPressed(button))
                {
                    detected = true;
                    found[button] = (uint)report.Buttons;
                    Write($"    ✓ 位图 0x{(uint)report.Buttons:X8}  ({report.DescribePressed()})");
                    while (DateTime.UtcNow < deadline && _latest?.IsPressed(button) == true) await Task.Delay(30);
                    break;
                }
                await Task.Delay(30);
            }
            if (!detected) Write("    × 未检测到");
        }

        Write("\n---- 按键位图汇总 ----");
        foreach (var pair in found.OrderBy(p => p.Key))
            Write($"  {pair.Key.ToDisplayName(),-24} bit{(int)Math.Log2((uint)pair.Key),2}  0x{(uint)pair.Key:X8}");
        var all = found.Values.Aggregate(0u, (a, b) => a | b);
        Write($"  合并位图：0x{all:X8}");
        var known = (uint)BladeButtonsExtensions.All.Aggregate(BladeButtons.None, (a, b) => a | b);
        Write($"  未识别位：0x{all & ~known:X8}");
    }

    private static void DetectXInput()
    {
        Write("XInput 只能看到标准 Xbox 按键，Fn / M1-M8 / 背键不会出现。");
        Write("如果手柄通过 2.4G 接收器连接，设备管理器中通常会显示为 Xbox 控制器。");
    }

    private static void ListPnpDevices()
    {
        Write("---- USB PnP 设备 ----");
        QueryPnp("SELECT Name,PNPDeviceID,Status,Manufacturer FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB%'");
        Write("---- 蓝牙 PnP 设备 ----");
        QueryPnp("SELECT Name,PNPDeviceID,Status,Manufacturer FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'BTHENUM%' OR PNPDeviceID LIKE 'BTHLE%'");
    }

    private static void QueryPnp(string query)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(query);
            var any = false;
            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                any = true;
                var id = obj["PNPDeviceID"]?.ToString() ?? "";
                var name = obj["Name"]?.ToString() ?? "";
                var status = obj["Status"]?.ToString() ?? "";
                var manufacturer = obj["Manufacturer"]?.ToString() ?? "";
                Write($"  {status,-8} {name,-40} {manufacturer,-18} {id}");
            }
            if (!any) Write("  （无）");
        }
        catch (Exception ex) { Write($"  查询失败：{ex.Message}"); }
    }

    private static void WriteHelp()
    {
        Write("用法：");
        Write("  LeadjoyProbe.exe               自动诊断（推荐，双击即可）");
        Write("  LeadjoyProbe.exe menu          交互菜单");
        Write("  LeadjoyProbe.exe scan          扫描蓝牙 + 列出 HID + PnP");
        Write("  LeadjoyProbe.exe ble <地址>    连接指定蓝牙地址并监听");
        Write("  LeadjoyProbe.exe hid [序号]    监听 HID 设备");
        Write("  LeadjoyProbe.exe hidinfo [序号] 查看 HID 报告描述符");
        Write("  LeadjoyProbe.exe rawhid [序号] [write] 原始 HID 监听");
        Write("  LeadjoyProbe.exe services <地址> 转储 GATT 服务");
        Write("  LeadjoyProbe.exe buttons       按键位图向导");
    }

    private static void Write(string message)
    {
        Console.WriteLine(message);
        _log?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    private sealed record AdvInfo(ulong Address, string Name, short Rssi, string Services, string Type, string AddressType);
}
