#pragma warning disable CS0067
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.Core.Devices;

/// <summary>无手柄时用于演示界面与测试映射的模拟设备。</summary>
public sealed class SimulatorTransport : IBladeTransport
{
    private readonly Timer _timer;
    private BladeButtons _buttons;
    private readonly object _sync = new();
    private bool _connected;

    public SimulatorTransport()
    {
        _timer = new Timer(_ => Tick(), null, 0, 16);
    }

    public BladeTransportKind Kind => BladeTransportKind.Simulator;
    public string DisplayName => "模拟手柄";
    public bool IsConnected => _connected;
    public int Mtu => 185;

    public event EventHandler<BladeReport>? ReportReceived;
    public event EventHandler<Frame>? FrameReceived;
    public event EventHandler<string>? Log;
    public event EventHandler? Disconnected;
    public event EventHandler<int>? BatteryChanged;

    public void SetButton(BladeButtons button, bool pressed)
    {
        lock (_sync)
        {
            if (pressed) _buttons |= button;
            else _buttons &= ~button;
        }
    }

    public void SetButtons(BladeButtons buttons)
    {
        lock (_sync) _buttons = buttons;
    }

    private void Tick()
    {
        if (!_connected) return;
        BladeButtons buttons;
        lock (_sync) buttons = _buttons;

        var t = Environment.TickCount64 / 1000.0;
        var report = new BladeReport
        {
            Buttons = buttons,
            LeftX = (short)(Math.Sin(t) * 16000),
            LeftY = (short)(Math.Cos(t) * 16000),
            RightX = (short)(Math.Sin(t * 1.7) * 12000),
            RightY = (short)(Math.Cos(t * 1.3) * 12000),
            LeftTrigger = 0,
            RightTrigger = 0,
            SubType = 1,
        };
        ReportReceived?.Invoke(this, report);
        FrameReceived?.Invoke(this, new Frame(FrameReader.TypeGamepad, report.RawContent, 0, false));
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        Log?.Invoke(this, "模拟手柄已启用");
        return Task.CompletedTask;
    }

    public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _connected = false;
        _timer.Dispose();
        Disconnected?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }
}

#pragma warning restore CS0067

