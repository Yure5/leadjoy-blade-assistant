using System.Runtime.InteropServices;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.Core.Devices;

#pragma warning disable CS0067
/// <summary>
/// 通过 XInput 读取标准 Xbox 手柄（风刃切到 Xbox 模式后同样适用）。
/// 只包含标准按键；M1-M8 / Fn / 背键需要手柄端板载映射。
/// </summary>
public sealed class XInputTransport : IBladeTransport
{
    private const uint DpadUp = 0x0001;
    private const uint DpadDown = 0x0002;
    private const uint DpadLeft = 0x0004;
    private const uint DpadRight = 0x0008;
    private const uint Start = 0x0010;
    private const uint Back = 0x0020;
    private const uint LeftThumb = 0x0040;
    private const uint RightThumb = 0x0080;
    private const uint LeftShoulder = 0x0100;
    private const uint RightShoulder = 0x0200;
    private const uint Guide = 0x0400;
    private const uint A = 0x1000;
    private const uint B = 0x2000;
    private const uint X = 0x4000;
    private const uint Y = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftThumbX;
        public short LeftThumbY;
        public short RightThumbX;
        public short RightThumbY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint index, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputBatteryInformation
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputSetState(uint index, ref XInputVibration vibration);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetBatteryInformation(uint index, byte deviceType,
        out XInputBatteryInformation battery);

    /// <summary>通过 Xbox 链路震动指定槽位；返回是否成功。</summary>
    public static bool TryVibrate(uint index, double left, double right, int milliseconds)
    {
        var vibration = new XInputVibration
        {
            LeftMotorSpeed = (ushort)(Math.Clamp(left, 0, 1) * 65535),
            RightMotorSpeed = (ushort)(Math.Clamp(right, 0, 1) * 65535),
        };
        if (XInputSetState(index, ref vibration) != 0) return false;
        if (milliseconds > 0) Thread.Sleep(milliseconds);
        vibration = default;
        XInputSetState(index, ref vibration);
        return true;
    }

    /// <summary>XInput 只能给出空/低/中/满；转换为近似百分比。</summary>
    public static int? TryGetBatteryPercent()
    {
        for (uint slot = 0; slot < 4; slot++)
        {
            if (XInputGetState(slot, out _) != 0) continue;
            if (XInputGetBatteryInformation(slot, 0, out var battery) != 0) continue;
            if (battery.BatteryType == 0) return null; // disconnected
            return battery.BatteryLevel switch
            {
                0 => 5,
                1 => 25,
                2 => 60,
                3 => 100,
                _ => null,
            };
        }
        return null;
    }

    private readonly uint _index;
    private readonly string _name;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _connected;

    public XInputTransport(uint index, string? name = null)
    {
        _index = index;
        _name = string.IsNullOrWhiteSpace(name) ? $"XInput 手柄 #{index + 1}" : name;
    }

    public BladeTransportKind Kind => BladeTransportKind.XInput;
    public string DisplayName => _name;
    public bool IsConnected => _connected;
    public int Mtu => 64;

    public event EventHandler<BladeReport>? ReportReceived;
    public event EventHandler<Frame>? FrameReceived;
    public event EventHandler<string>? Log;
    public event EventHandler? Disconnected;
    public event EventHandler<int>? BatteryChanged;

    public static bool IsAvailable(uint index = 0)
    {
        if (XInputGetState(index, out _) != 0) return false;
        // 某些驱动/虚拟设备会残留一个“可读”的 XInput 槽位，但电池状态仍是
        // DISCONNECTED。必须两者都通过，才把它当成真实手柄。
        if (XInputGetBatteryInformation(index, 0, out var battery) != 0) return false;
        return battery.BatteryType != 0;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (XInputGetState(_index, out _) != 0)
            throw new InvalidOperationException("未检测到 XInput 手柄。请把手柄切换到 Xbox 模式，或连接 Xbox 手柄。");
        _connected = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => PollAsync(_cts.Token));
        Log?.Invoke(this, "XInput 手柄已连接");
        return Task.CompletedTask;
    }

    private async Task PollAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (XInputGetState(_index, out var state) != 0)
                {
                    _connected = false;
                    Disconnected?.Invoke(this, EventArgs.Empty);
                    return;
                }

                var pad = state.Gamepad;
                var buttons = BladeButtons.None;
                if ((pad.Buttons & DpadUp) != 0) buttons |= BladeButtons.Up;
                if ((pad.Buttons & DpadDown) != 0) buttons |= BladeButtons.Down;
                if ((pad.Buttons & DpadLeft) != 0) buttons |= BladeButtons.Left;
                if ((pad.Buttons & DpadRight) != 0) buttons |= BladeButtons.Right;
                if ((pad.Buttons & Start) != 0) buttons |= BladeButtons.Start;
                if ((pad.Buttons & Back) != 0) buttons |= BladeButtons.Select;
                if ((pad.Buttons & LeftThumb) != 0) buttons |= BladeButtons.L3;
                if ((pad.Buttons & RightThumb) != 0) buttons |= BladeButtons.R3;
                if ((pad.Buttons & LeftShoulder) != 0) buttons |= BladeButtons.L1;
                if ((pad.Buttons & RightShoulder) != 0) buttons |= BladeButtons.R1;
                if ((pad.Buttons & Guide) != 0) buttons |= BladeButtons.Home;
                if ((pad.Buttons & A) != 0) buttons |= BladeButtons.A;
                if ((pad.Buttons & B) != 0) buttons |= BladeButtons.B;
                if ((pad.Buttons & X) != 0) buttons |= BladeButtons.X;
                if ((pad.Buttons & Y) != 0) buttons |= BladeButtons.Y;

                ReportReceived?.Invoke(this, new BladeReport
                {
                    Buttons = buttons,
                    LeftX = pad.LeftThumbX,
                    LeftY = pad.LeftThumbY,
                    RightX = pad.RightThumbX,
                    RightY = pad.RightThumbY,
                    LeftTrigger = pad.LeftTrigger,
                    RightTrigger = pad.RightTrigger,
                    RawContent = [],
                });

                await Task.Delay(8, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _connected = false;
        }
    }

    public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
        => Task.CompletedTask; // XInput 只读，不支持震动/配置写入

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
#pragma warning restore CS0067
