#pragma warning disable CS0067
using HidSharp;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.Core.Devices;

/// <summary>
/// Switch 模式的唯一通路：直接读 HID 报文，完全不走 Windows.Gaming.Input。
///
/// 为什么不再用系统接口：同一只手柄在不同 Windows 上会发不同类型的报文 ——
/// Win10 上停在标准集合 0x3F（系统接口能读），Win11 上会被驱动/握手切到 0x30，
/// 而 0x30 位于厂商页面 0xFF01，系统接口永远读不到按键（设备在列表里但时间戳恒 0）。
/// 所以「系统接口能不能用」取决于机器，不能作为方案；HID 层则两台机器都稳定
/// （实测 700+ 帧/秒，in=362 out=49）。
///
/// 解析按报文 ID 分派（见 <see cref="SwitchReportParser"/>）：
/// 0x30 / 0x21 用任天堂全量布局，0x3F 用标准 HID 布局。
/// </summary>
public sealed class SwitchHidTransport : IBladeTransport
{
    private readonly int _vendorId;
    private readonly int _productId;
    private readonly string? _devicePath;
    private HidStream? _stream;
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private volatile bool _connected;
    private string _name = "Switch 模式";

    public SwitchHidTransport(int vendorId, int productId, string? name = null, string? devicePath = null)
    {
        _vendorId = vendorId == 0 ? 0x057E : vendorId;
        _productId = productId;
        _devicePath = devicePath;
        if (!string.IsNullOrWhiteSpace(name)) _name = name;
    }

    public sealed record SwitchHidDevice(int VendorId, int ProductId, string Path, int InputLength, int OutputLength);

    /// <summary>
    /// 枚举可以用「直接读 HID」方式打开的 Switch 手柄接口。
    /// 不依赖 Windows.Gaming.Input（那套接口在部分电脑上根本看不到该手柄）。
    /// </summary>
    public static IReadOnlyList<SwitchHidDevice> Discover(int vendorId = 0x057E)
    {
        var list = new List<SwitchHidDevice>();
        try
        {
            foreach (var device in DeviceList.Local.GetHidDevices())
            {
                if (device.VendorID != vendorId) continue;
                try
                {
                    // 手柄集合才有较长的输出报文；键盘/触摸板集合会被过滤掉。
                    if (device.GetMaxOutputReportLength() < 40) continue;
                    list.Add(new SwitchHidDevice(device.VendorID, device.ProductID, device.DevicePath,
                        device.GetMaxInputReportLength(), device.GetMaxOutputReportLength()));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("hiddle.log", "switchhid 枚举失败：" + ex.Message);
        }
        return list;
    }

    /// <summary>
    /// 枚举到的 HID 节点不一定代表手柄真的在线：Windows 可能保留已关机/休眠
    /// 设备的幽灵节点。尝试短读一次；读不到任何报文就视为未连接。
    /// </summary>
    public static bool IsLive(SwitchHidDevice candidate)
    {
        try
        {
            var device = DeviceList.Local.GetHidDevices()
                .FirstOrDefault(d => string.Equals(d.DevicePath, candidate.Path,
                    StringComparison.OrdinalIgnoreCase));
            if (device == null || !device.TryOpen(out var stream)) return false;
            using (stream)
            {
                stream.ReadTimeout = 180;
                var buffer = new byte[Math.Max(1, device.GetMaxInputReportLength())];
                return stream.Read(buffer, 0, buffer.Length) > 0;
            }
        }
        catch
        {
            return false;
        }
    }

    public BladeTransportKind Kind => BladeTransportKind.SwitchHid;
    public string DisplayName => _name;
    public bool IsConnected => _connected;
    public int Mtu => 64;

    /// <summary>当前正在解析的报文类型（Full=0x30/0x21，Simple=0x3F），供 UI 显示。</summary>
    public SwitchReportKind CurrentReportKind { get; private set; }

    public event EventHandler<BladeReport>? ReportReceived;
    public event EventHandler<Frame>? FrameReceived;
    public event EventHandler<string>? Log;
    public event EventHandler? Disconnected;
    public event EventHandler<int>? BatteryChanged;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var devices = DeviceList.Local.GetHidDevices()
            .Where(d => d.VendorID == _vendorId && (_productId == 0 || d.ProductID == _productId))
            .OrderByDescending(d => string.Equals(d.DevicePath, _devicePath, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(d => d.GetMaxOutputReportLength())
            .ToList();
        if (devices.Count == 0)
            throw new InvalidOperationException("未找到该手柄的 HID 接口（可能不是 Switch 模式）");

        HidStream? stream = null;
        HidDevice? opened = null;
        foreach (var device in devices)
        {
            if (!device.TryOpen(out var candidate)) continue;
            stream = candidate;
            opened = device;
            break;
        }

        if (stream == null || opened == null)
            throw new InvalidOperationException("HID 接口被其他程序占用，无法打开");

        _stream = stream;
        _stream.ReadTimeout = 100;
        _name = "Switch 模式（HID 直读 " + opened.VendorID.ToString("X4") + ":" +
                opened.ProductID.ToString("X4") + "）";
        _connected = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 独占一个高于普通优先级的线程连续读取：手柄约 700 报文/秒，
        // 任何 sleep/Delay 都会让驱动里的报文排队，表现就是「按键有明显延迟」。
        var thread = new Thread(() => ReadLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "SwitchHidRead",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread = thread;
        thread.Start();

        if (HandshakeEnabled()) TrySendHandshake();

        Log?.Invoke(this, "已连接 " + _name + "（输入报文 " + opened.GetMaxInputReportLength() + " 字节）");
        DiagnosticsLog.Write("hiddle.log",
            "switchhid 已打开 " + opened.DevicePath + " in=" + opened.GetMaxInputReportLength() +
            " out=" + opened.GetMaxOutputReportLength() +
            " handshake=" + (HandshakeEnabled() ? "on" : "off"));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 默认**不发握手**：让控制器留在 0x3F 标准集合，Windows 自己也能读，
    /// 对我们和系统都最友好；只有需要 IMU/振动时才用环境变量打开。
    /// </summary>
    private static bool HandshakeEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("LEADJOY_SWITCH_HANDSHAKE"), "1", StringComparison.Ordinal);

    private void TrySendHandshake()
    {
        try
        {
            Send(BuildCommand(0x03, 0x30)); // 输入报文模式 = 0x30
            Send(BuildCommand(0x40, 0x01)); // IMU
            Send(BuildCommand(0x48, 0x01)); // 振动
            DiagnosticsLog.Write("hiddle.log", "switchhid 已发送 0x03/0x40/0x48 握手（LEADJOY_SWITCH_HANDSHAKE=1）");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("hiddle.log", "switchhid 握手失败：" + ex.Message);
        }
    }

    private static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var value in data)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++)
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x0F : crc << 1);
        }
        return crc;
    }

    /// <summary>蓝牙输出报文 0x01：振动 8 字节 + 子命令 + 参数 + CRC8。</summary>
    private static byte[] BuildCommand(byte subCommand, byte argument)
    {
        var buffer = new byte[49];
        buffer[0] = 0x01;
        buffer[1] = 0x00; buffer[2] = 0x01; buffer[3] = 0x40; buffer[4] = 0x40;
        buffer[5] = 0x00; buffer[6] = 0x01; buffer[7] = 0x40; buffer[8] = 0x40;
        buffer[9] = subCommand;
        buffer[10] = argument;
        buffer[48] = Crc8(buffer.AsSpan(1, 47));
        return buffer;
    }

    private void Send(byte[] report)
    {
        var stream = _stream;
        if (stream == null) return;
        stream.Write(report, 0, report.Length);
    }

    /// <summary>
    /// 连续读取循环：读一帧立刻分发，不做任何 sleep。
    /// 一旦消费速度慢于报文速度，驱动缓冲区就会排队，表现就是「按键延迟越来越大」。
    /// </summary>
    private void ReadLoop(CancellationToken token)
    {
        var stream = _stream!;
        var buffer = new byte[Math.Max(64, stream.Device.GetMaxInputReportLength())];
        var lastFlags = BladeButtons.None;
        var reports = 0;
        var unknown = 0;
        var lastHeartbeat = Environment.TickCount64;
        var lastKeepAlive = Environment.TickCount64;
        var decodeLogged = false;
        var idleErrors = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = stream.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) return;

                    // HidSharp 在 Windows 上读超时会抛 IOException("Operation failed after some time")，
                    // 那是「暂时没有报文」，不是断开；只要设备还在 HID 列表里就继续读。
                    if (DeviceStillPresent())
                    {
                        if (++idleErrors < 200) continue;
                    }

                    DiagnosticsLog.Write("hiddle.log", "switchhid 读取失败：" + ex.Message);
                    Log?.Invoke(this, _name + " 读取中断：" + ex.Message);
                    _connected = false;
                    Disconnected?.Invoke(this, EventArgs.Empty);
                    return;
                }

                if (read >= SwitchReportParser.MinLength)
                {
                    var state = SwitchReportParser.Parse(buffer, read);
                    if (state.Kind == SwitchReportKind.Unknown)
                    {
                        unknown++;
                        if (unknown <= 3)
                            DiagnosticsLog.Write("hiddle.log",
                                "switchhid 未知报文 id=0x" + buffer[0].ToString("X2") + "，已忽略");
                    }
                    else
                    {
                        reports++;
                        idleErrors = 0;
                        if (CurrentReportKind != state.Kind)
                        {
                            CurrentReportKind = state.Kind;
                            var kindText = state.Kind == SwitchReportKind.Full
                                ? "0x30/0x21 全量（厂商页面，系统接口读不到）"
                                : "0x3F 标准 HID 集合（系统接口也能读）";
                            Log?.Invoke(this, "报文类型：" + kindText);
                            DiagnosticsLog.Write("hiddle.log", "switchhid 报文类型切换为 " + kindText);
                        }

                        if (!decodeLogged)
                        {
                            decodeLogged = true;
                            DiagnosticsLog.Write("hiddle.log",
                                "switchhid 首帧 " + SwitchReportParser.Describe(state) +
                                " 原始=" + Convert.ToHexString(buffer.AsSpan(0, Math.Min(16, read))));
                        }

                        var flags = ToBladeButtons(state);
                        if (flags != lastFlags)
                        {
                            lastFlags = flags;
                            DiagnosticsLog.Write("hiddle.log",
                                "switchhid 按键=" + flags + "  (" + SwitchReportParser.Describe(state) + ")");
                        }

                        ReportReceived?.Invoke(this, new BladeReport
                        {
                            Buttons = flags,
                            LeftX = (short)state.LeftX,
                            LeftY = (short)state.LeftY,
                            RightX = (short)state.RightX,
                            RightY = (short)state.RightY,
                            LeftTrigger = state.L2 ? (byte)255 : (byte)0,
                            RightTrigger = state.R2 ? (byte)255 : (byte)0,
                            Timestamp = DateTime.UtcNow,
                            RawContent = buffer.AsSpan(0, Math.Min(read, 32)).ToArray(),
                        });
                    }
                }

                var now = Environment.TickCount64;
                if (now - lastKeepAlive >= 1000)
                {
                    lastKeepAlive = now;
                    // 只在需要时才发输出报文：默认不发握手，保活也只在 Full 模式下有意义。
                    if (HandshakeEnabled())
                    {
                        try { Send(new byte[] { 0x10, 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 }); }
                        catch { }
                    }
                }

                if (now - lastHeartbeat >= 10000)
                {
                    lastHeartbeat = now;
                    DiagnosticsLog.Write("hiddle.log",
                        "switchhid 心跳 报文数=" + reports + " 未知=" + unknown +
                        " 类型=" + CurrentReportKind + " 按键=" + lastFlags);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("hiddle.log", "switchhid 线程退出：" + ex.Message);
        }
        finally
        {
            _connected = false;
        }
    }

    /// <summary>把解析结果翻译成软件内部的风刃按键位。</summary>
    private static BladeButtons ToBladeButtons(in SwitchPadState state)
    {
        var flags = BladeButtons.None;
        if (state.A) flags |= BladeButtons.A;
        if (state.B) flags |= BladeButtons.B;
        if (state.X) flags |= BladeButtons.X;
        if (state.Y) flags |= BladeButtons.Y;
        if (state.L1) flags |= BladeButtons.L1;
        if (state.R1) flags |= BladeButtons.R1;
        if (state.L2) flags |= BladeButtons.L2;
        if (state.R2) flags |= BladeButtons.R2;
        if (state.L3) flags |= BladeButtons.L3;
        if (state.R3) flags |= BladeButtons.R3;
        if (state.Up) flags |= BladeButtons.Up;
        if (state.Down) flags |= BladeButtons.Down;
        if (state.Left) flags |= BladeButtons.Left;
        if (state.Right) flags |= BladeButtons.Right;
        if (state.Home) flags |= BladeButtons.Home;
        if (state.Capture) flags |= BladeButtons.Capture;
        if (state.Minus) flags |= BladeButtons.Select;
        if (state.Plus) flags |= BladeButtons.Start;
        return flags;
    }

    /// <summary>设备是否仍在 HID 列表里（用来区分「读超时」和「真的被拔掉/关机」）。</summary>
    private bool DeviceStillPresent()
    {
        try
        {
            foreach (var device in DeviceList.Local.GetHidDevices())
            {
                if (device.VendorID != _vendorId) continue;
                if (_productId != 0 && device.ProductID != _productId) continue;
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 厂商 0xA5 帧对 Switch 模式没有意义（那是 BLE 厂商通道的协议），
    /// 这里只接受 Switch 自己的输出报文，其它一律忽略。
    /// </summary>
    public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        if (frame.Length < 2) return Task.CompletedTask;
        if (frame[0] != 0x01 && frame[0] != 0x10 && frame[0] != 0x11) return Task.CompletedTask;
        try { Send(frame); } catch { }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _connected = false;
        try { _cts?.Cancel(); } catch { }
        var thread = _thread;
        if (thread != null)
        {
            try { await Task.Run(() => thread.Join(1500)).ConfigureAwait(false); } catch { }
            _thread = null;
        }
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        _cts?.Dispose();
        _cts = null;
    }
}
#pragma warning restore CS0067
