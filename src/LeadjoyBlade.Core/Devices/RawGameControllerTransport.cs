using System.Runtime.InteropServices;
using LeadjoyBlade.Core.Protocol;
using Windows.Gaming.Input;

namespace LeadjoyBlade.Core.Devices;

/// <summary>
/// 通过 Windows RawGameController 读取 DS4 / Switch / 其它标准 HID 手柄。
/// 只包含标准按键与摇杆/扳机；背键/Fn 仍需要 _1B 厂商通道。
/// </summary>
public sealed class RawGameControllerTransport : IBladeTransport
{
    private readonly int _index;
    private readonly int _vendorId;
    private readonly int _productId;
    private RawGameController? _pad;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _connected;
    private BladeButtons _lastFlags;
    private bool[] _lastButtons = [];
    private string[] _labelTexts = [];

    public RawGameControllerTransport(int index, int vendorId = 0, int productId = 0)
    {
        _index = index;
        _vendorId = vendorId;
        _productId = productId;
    }

    public BladeTransportKind Kind => BladeTransportKind.RawGameController;
    public string DisplayName => _pad?.DisplayName ?? $"HID 手柄 #{_index + 1}";
    public bool IsConnected => _connected;
    public int Mtu => 64;

    public event EventHandler<BladeReport>? ReportReceived;
    public event EventHandler<Frame>? FrameReceived;
    public event EventHandler<string>? Log;
    public event EventHandler? Disconnected;
    public event EventHandler<int>? BatteryChanged;

    public static int Count => RawGameController.RawGameControllers.Count;

    /// <summary>
    /// 系统接口（Windows.Gaming.Input）是否真的在出数据。
    /// 有的电脑会先给一两个陈旧读数之后就不再更新，所以要求在一小段时间内看到多个不同时间戳。
    /// </summary>
    public static bool HasLiveData(int vendorId, int productId, int timeoutMs = 900, int requiredTimestamps = 3)
    {
        var pads = RawGameController.RawGameControllers;
        for (var i = 0; i < pads.Count; i++)
        {
            var pad = pads[i];
            if (pad.HardwareVendorId != vendorId) continue;
            if (productId != 0 && pad.HardwareProductId != productId) continue;

            var buttons = new bool[pad.ButtonCount];
            var switches = new GameControllerSwitchPosition[pad.SwitchCount];
            var axes = new double[pad.AxisCount];
            var seen = new HashSet<ulong>();
            var end = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < end)
            {
                var timestamp = pad.GetCurrentReading(buttons, switches, axes);
                if (timestamp != 0) seen.Add(timestamp);
                if (seen.Count >= requiredTimestamps) return true;
                Thread.Sleep(20);
            }
            return false;
        }
        return false;
    }

    public static RawGameController? Get(int index)
    {
        var pads = RawGameController.RawGameControllers;
        return index >= 0 && index < pads.Count ? pads[index] : null;
    }

    /// <summary>
    /// 按 VID/PID 重新定位手柄：系统里的 HID 手柄顺序会随插拔/重连变化，
    /// 只按序号取可能连到另一只手柄，甚至取到已经失效的对象。
    /// </summary>
    private RawGameController? Resolve()
    {
        var pads = RawGameController.RawGameControllers;
        if (_vendorId != 0 || _productId != 0)
        {
            if (_index >= 0 && _index < pads.Count)
            {
                var preferred = pads[_index];
                if (preferred.HardwareVendorId == _vendorId && preferred.HardwareProductId == _productId)
                    return preferred;
            }
            for (var i = 0; i < pads.Count; i++)
            {
                var pad = pads[i];
                if (pad.HardwareVendorId == _vendorId && pad.HardwareProductId == _productId) return pad;
            }
        }
        return _index >= 0 && _index < pads.Count ? pads[_index] : null;
    }

    /// <summary>当前系统里还剩哪些 HID 手柄，写进日志便于排查“连上了但没数据”。</summary>
    private static string Inventory()
    {
        var pads = RawGameController.RawGameControllers;
        var parts = new List<string>();
        for (var i = 0; i < pads.Count; i++)
        {
            var pad = pads[i];
            parts.Add($"{i}:{pad.HardwareVendorId:X4}:{pad.HardwareProductId:X4}" +
                      $"「{pad.DisplayName}」b{pad.ButtonCount}s{pad.SwitchCount}a{pad.AxisCount}");
        }
        return pads.Count == 0 ? "(无)" : string.Join(" ", parts);
    }

    /// <summary>该对象是否仍在系统的手柄列表里（蓝牙休眠/断开后对象会失效但读数不再更新）。</summary>
    private static bool StillPresent(RawGameController pad)
    {
        var pads = RawGameController.RawGameControllers;
        for (var i = 0; i < pads.Count; i++)
        {
            var candidate = pads[i];
            if (candidate.HardwareVendorId == pad.HardwareVendorId &&
                candidate.HardwareProductId == pad.HardwareProductId &&
                candidate.ButtonCount == pad.ButtonCount &&
                candidate.SwitchCount == pad.SwitchCount &&
                candidate.AxisCount == pad.AxisCount &&
                string.Equals(candidate.DisplayName, pad.DisplayName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _pad = Resolve() ?? throw new InvalidOperationException("HID 手柄已断开");
        _connected = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => PollAsync(_cts.Token));
        Log?.Invoke(this, $"已连接 {_pad.DisplayName}（{_pad.ButtonCount} 键 / {_pad.AxisCount} 轴）");
        return Task.CompletedTask;
    }

    private async Task PollAsync(CancellationToken token)
    {
        var pad = _pad!;
        // 按键语义：优先用系统标签；标签整体缺失时由 GamepadButtonLayout 按固定顺序回退，
        // 避免在部分 Windows/驱动组合上所有按键都被映射成 None（只有 LT/RT 有反应）。
        _labelTexts = new string[pad.ButtonCount];
        for (var i = 0; i < _labelTexts.Length; i++)
            _labelTexts[i] = pad.GetButtonLabel(i).ToString();

        // 轴顺序不能只看 VID：不少第三方 DS4 兼容手柄的 VID 不是 0x054C，
        // 但轴布局仍是 DS4 的 [LX, LY, RX, L2, R2, RY]。
        // 若按 Xbox 的 [LX, LY, RX, RY, LT, RT] 去读，静息时 axes[3]=0 会被当成
        // “右摇杆推到底”，输入法就会自动翻到标点层并持续输出标点。
        // 原始标签里的 Square / Cross / Triangle / Left1 是可靠的 DS 系列判据。
        var isDs4 = pad.HardwareVendorId == 0x054C || LooksLikePlayStation(_labelTexts);
        var leftTriggerAxis = isDs4 ? 3 : 4;
        var rightTriggerAxis = isDs4 ? 4 : 5;
        var rightYAxis = isDs4 ? 5 : 3;
        Log?.Invoke(this, $"轴布局：{(isDs4 ? "DS4/PlayStation（L2/R2 = 3/4，右摇杆 Y = 5）" : "Xbox（右摇杆 Y = 3，LT/RT = 4/5）")}");

        var layout = GamepadButtonLayout.Build(pad.HardwareVendorId, pad.HardwareProductId,
            pad.DisplayName, _labelTexts, pad.SwitchCount);
        var labels = layout.Mapped;
        var mappingText = layout.Describe();

        Log?.Invoke(this, $"{pad.DisplayName}（VID {pad.HardwareVendorId:X4}:{pad.HardwareProductId:X4}，" +
                           $"{pad.ButtonCount} 键 / {pad.SwitchCount} 十字键 / {pad.AxisCount} 轴）");
        Log?.Invoke(this, $"按键映射：{mappingText}");
        if (layout.AllLabelsMissing)
            Log?.Invoke(this,
                $"系统没有为该手柄提供按键标签，已按固定物理顺序映射 {layout.PositionalMappedCount} 个按键（标签缺失机型）。");
        if (!layout.AnyMapped)
            Log?.Invoke(this, "警告：该手柄没有解析出任何按键，请复制「诊断日志」内容反馈以便定位。");

        DiagnosticsLog.Write("hiddle.log",
            $"rawgamepad {pad.DisplayName} vid={pad.HardwareVendorId:X4} pid={pad.HardwareProductId:X4} " +
            $"buttons={pad.ButtonCount} switches={pad.SwitchCount} axes={pad.AxisCount} " +
            $"labels=[{string.Join("|", _labelTexts)}]");
        DiagnosticsLog.Write("hiddle.log",
            $"  os={RuntimeInformation.OSDescription} ver={Environment.OSVersion.VersionString} " +
            $"arch={RuntimeInformation.OSArchitecture} clr={Environment.Version}");
        DiagnosticsLog.Write("hiddle.log",
            $"  labelMapped={layout.LabelMappedCount} positional={layout.PositionalMappedCount} mapping={mappingText}");
        DiagnosticsLog.Write("hiddle.log", $"  手柄列表：{Inventory()}");

        var buttons = new bool[pad.ButtonCount];
        var switches = new GameControllerSwitchPosition[pad.SwitchCount];
        var axes = new double[pad.AxisCount];

        var lastPresenceCheck = Environment.TickCount64;
        var lastHeartbeat = Environment.TickCount64;
        var lastTimestamp = 0UL;
        var previousTimestamp = 0UL;
        var timestampChanges = 0;
        var lastDataTick = Environment.TickCount64;
        var lastZeroWarning = 0L;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var nowTicks0 = Environment.TickCount64;
                var timestamp = pad.GetCurrentReading(buttons, switches, axes);
                if (timestamp != 0)
                {
                    lastDataTick = nowTicks0;
                    lastTimestamp = timestamp;
                    if (timestamp != previousTimestamp)
                    {
                        previousTimestamp = timestamp;
                        timestampChanges++;
                    }
                    var flags = BladeButtons.None;
                    for (var i = 0; i < buttons.Length; i++)
                        if (buttons[i]) flags |= labels[i];
                    if (switches.Length > 0) flags |= MapSwitch(switches[0]);
                    if (axes.Length >= 6)
                    {
                        if (axes[leftTriggerAxis] > 0.5) flags |= BladeButtons.L2;
                        if (axes[rightTriggerAxis] > 0.5) flags |= BladeButtons.R2;
                    }

                    if (flags != _lastFlags)
                    {
                        _lastFlags = flags;
                        DiagnosticsLog.Write("hiddle.log",
                            $"rawgamepad press flags={flags} axes={string.Join(",", axes.Select(v => v.ToString("F2")))}");
                    }

                    if (!buttons.SequenceEqual(_lastButtons))
                    {
                        _lastButtons = buttons.ToArray();
                        var pressed = string.Join(",",
                            buttons.Select((value, index) => (value, index))
                                .Where(pair => pair.value)
                                .Select(pair => pair.index));
                        var pressedNames = string.Join("|", MappedPressedNames(buttons, labels));
                        DiagnosticsLog.Write("hiddle.log",
                            $"rawgamepad pressed indexes=[{pressed}] mapped=[{pressedNames}]");
                        // 系统没给标签、靠位置回退出来的按键：提醒现场可以用 hid-layout.json 改正。
                        for (var i = 0; i < buttons.Length && i < _labelTexts.Length; i++)
                        {
                            if (!buttons[i] || GamepadButtonLayout.ParseLabel(_labelTexts[i]) != BladeButtons.None) continue;
                            if (labels[i] == BladeButtons.None) continue;
                            DiagnosticsLog.Write("hiddle.log",
                                $"  提示：序号 {i} 没有系统标签，按固定顺序当成 {labels[i].ToShortName()}；" +
                                "若与实际按键不符，请在 hid-layout.json 中更正。");
                        }
                    }

                    // 不同手柄的轴范围不同：DS4 常见为 0..1（中心 0.5），Xbox 为 -1..1。
                    var signedAxes = axes.Any(value => value < -0.01);
                    short Axis(int i, bool invert = false)
                    {
                        if (i >= axes.Length) return 0;
                        var raw = axes[i];
                        var normalized = signedAxes ? raw : (raw - 0.5) * 2.0;
                        var value = (short)Math.Clamp(normalized * 32767.0, short.MinValue, short.MaxValue);
                        return invert ? (short)-value : value;
                    }

                    byte Trigger(int i) => i < axes.Length
                        ? (byte)Math.Clamp(axes[i] * 255.0, 0, 255)
                        : (byte)0;

                    ReportReceived?.Invoke(this, new BladeReport
                    {
                        Buttons = flags,
                        LeftX = Axis(0),
                        LeftY = Axis(1, invert: true),
                        RightX = Axis(2),
                        RightY = Axis(rightYAxis, invert: true),
                        LeftTrigger = Trigger(leftTriggerAxis),
                        RightTrigger = Trigger(rightTriggerAxis),
                        RawContent = [],
                    });
                }

                var nowTicks = Environment.TickCount64;

                // 手柄在系统列表里，但 Windows 一直不给读数：蓝牙链路没建立/没进入上报模式。
                // 这种情况旧版本会显示“已连接、60Hz”却按键全无反应，这里明确提示出来。
                if (nowTicks - lastDataTick > 5000 && nowTicks - lastZeroWarning > 30000)
                {
                    lastZeroWarning = nowTicks;
                    DiagnosticsLog.Write("hiddle.log",
                        $"rawgamepad 5 秒内收不到任何读数（时间戳=0），系统手柄数={RawGameController.RawGameControllers.Count}");
                    Log?.Invoke(this,
                        $"{pad.DisplayName} 已连接，但 Windows 没有给出任何读数：请检查蓝牙连接，" +
                        "或换 USB 线试试（可在「诊断日志」查看详情）。");
                }

                if (nowTicks - lastPresenceCheck >= 1000)
                {
                    lastPresenceCheck = nowTicks;
                    if (!StillPresent(pad))
                    {
                        DiagnosticsLog.Write("hiddle.log",
                            $"rawgamepad 已从系统移除（{pad.DisplayName}），按断开处理；剩余手柄：{Inventory()}");
                        Log?.Invoke(this, $"{pad.DisplayName} 已断开（系统已移除该手柄，常见原因是蓝牙休眠/关机）");
                        _connected = false;
                        Disconnected?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                }

                if (nowTicks - lastHeartbeat >= 10000)
                {
                    lastHeartbeat = nowTicks;
                    DiagnosticsLog.Write("hiddle.log",
                        $"心跳 时间戳={lastTimestamp}(变化{timestampChanges}次) " +
                        $"按下=[{string.Join(",", buttons.Select((b, k) => (b, k)).Where(x => x.b).Select(x => x.k))}] " +
                        $"十字=[{string.Join(",", switches.Select(s => s.ToString()))}] " +
                        $"轴=[{string.Join(",", axes.Select(a => a.ToString("F2")))}] " +
                        $"系统手柄数={RawGameController.RawGameControllers.Count}");
                }

                await Task.Delay(8, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _connected = false;
        }
    }

    /// <summary>
    /// 系统标签是不是 DS4/PlayStation 那一套（Square / Cross / Triangle / Circle、Left1 / Right1）。
    /// 第三方 DS4 兼容手柄的 VID 往往不是 0x054C，只能靠标签认轴布局。
    /// </summary>
    private static bool LooksLikePlayStation(string[] labels)
    {
        foreach (var t in labels)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            if (t.Contains("Square", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Triangle", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Circle", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Cross", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Left1", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Right1", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Left2", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Right2", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static BladeButtons MapSwitch(GameControllerSwitchPosition position) => position switch
    {
        GameControllerSwitchPosition.Up => BladeButtons.Up,
        GameControllerSwitchPosition.Down => BladeButtons.Down,
        GameControllerSwitchPosition.Left => BladeButtons.Left,
        GameControllerSwitchPosition.Right => BladeButtons.Right,
        GameControllerSwitchPosition.UpLeft => BladeButtons.Up | BladeButtons.Left,
        GameControllerSwitchPosition.UpRight => BladeButtons.Up | BladeButtons.Right,
        GameControllerSwitchPosition.DownLeft => BladeButtons.Down | BladeButtons.Left,
        GameControllerSwitchPosition.DownRight => BladeButtons.Down | BladeButtons.Right,
        _ => BladeButtons.None,
    };

    /// <summary>把当前按下的按键序号翻译成“序号:标签→按键”，用于诊断日志。</summary>
    private IEnumerable<string> MappedPressedNames(bool[] buttons, BladeButtons[] labels)
    {
        for (var i = 0; i < buttons.Length && i < labels.Length; i++)
        {
            if (!buttons[i]) continue;
            var label = i < _labelTexts.Length && !string.IsNullOrWhiteSpace(_labelTexts[i])
                ? _labelTexts[i]
                : "?";
            var button = labels[i] == BladeButtons.None ? "-" : labels[i].ToShortName();
            yield return $"{i}:{label}→{button}";
        }
    }

    public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
        _connected = false;
    }
}
