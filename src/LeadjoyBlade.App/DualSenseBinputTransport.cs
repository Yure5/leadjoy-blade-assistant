using LeadjoyBlade.Binput;
using LeadjoyBlade.Core.Devices;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.App;

/// <summary>
/// DS5（DualSense）传输适配器：复用 Binput 已经调好的 HidPad 解析。
/// 这样不用再写一套 DualSense 描述符/报文解析，输入法开着时也不会和它抢 HID 句柄。
/// </summary>
public sealed class DualSenseBinputTransport : IBladeTransport
{
    private readonly BladeDeviceCandidate _candidate;
    private CancellationTokenSource? _cts;
    private Task? _poll;
    private BladeReport? _last;
    private long _lastEmit;
    private bool _reportedDisconnect;

    public DualSenseBinputTransport(BladeDeviceCandidate candidate) => _candidate = candidate;

    public BladeTransportKind Kind => BladeTransportKind.DualSenseHid;
    public string DisplayName => _candidate.Name;
    public bool IsConnected { get; private set; }
    public int Mtu => 64;

    public event EventHandler<BladeReport>? ReportReceived;
    public event EventHandler<Frame>? FrameReceived;
    public event EventHandler<string>? Log;
    public event EventHandler? Disconnected;
    public event EventHandler<int>? BatteryChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        HidPad.Start();
        for (var i = 0; i < 60; i++)
        {
            if (HidPad.Connected) break;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        if (!HidPad.Connected)
            throw new InvalidOperationException("没找到 DualSense HID（可能被其他程序占用）");

        IsConnected = true;
        _reportedDisconnect = false;
        HidPad.KeepRunning = true;
        _cts = new CancellationTokenSource();
        _poll = Task.Run(() => PollLoop(_cts.Token));
        Log?.Invoke(this, $"DS5 已连接：{DisplayName}");
    }

    private void PollLoop(CancellationToken token)
    {
        var lastRestart = Environment.TickCount64;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var st = HidPad.GetState();
                if (st.Connected)
                {
                    IsConnected = true;
                    _reportedDisconnect = false;
                    var report = ToReport(st);
                    var now = Environment.TickCount64;
                    if (_last == null || !SameState(_last, report) || now - _lastEmit >= 16)
                    {
                        _last = report;
                        _lastEmit = now;
                        ReportReceived?.Invoke(this, report);
                    }
                }
                else
                {
                    // Binput 停用/被游戏短暂独占时，HidPad 的管理线程可能已退出，拉起来。
                    if (Environment.TickCount64 - lastRestart >= 1000)
                    {
                        lastRestart = Environment.TickCount64;
                        HidPad.Start();
                    }
                    if (IsConnected && !_reportedDisconnect)
                    {
                        _reportedDisconnect = true;
                        IsConnected = false;
                        Disconnected?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"DS5 读取异常：{ex.Message}");
            }
            Thread.Sleep(8);
        }
    }

    private BladeReport ToReport(PadState st)
    {
        var b = BladeButtons.None;
        if ((st.Buttons & GamepadInput.A) != 0) b |= BladeButtons.A;
        if ((st.Buttons & GamepadInput.B) != 0) b |= BladeButtons.B;
        if ((st.Buttons & GamepadInput.X) != 0) b |= BladeButtons.X;
        if ((st.Buttons & GamepadInput.Y) != 0) b |= BladeButtons.Y;
        if ((st.Buttons & GamepadInput.LB) != 0) b |= BladeButtons.L1;
        if ((st.Buttons & GamepadInput.RB) != 0) b |= BladeButtons.R1;
        if ((st.Buttons & GamepadInput.L3) != 0) b |= BladeButtons.L3;
        if ((st.Buttons & GamepadInput.R3) != 0) b |= BladeButtons.R3;
        if ((st.Buttons & GamepadInput.BACK) != 0) b |= BladeButtons.Select;
        if ((st.Buttons & GamepadInput.START) != 0) b |= BladeButtons.Start;
        if ((st.Buttons & GamepadInput.DPAD_UP) != 0) b |= BladeButtons.Up;
        if ((st.Buttons & GamepadInput.DPAD_DOWN) != 0) b |= BladeButtons.Down;
        if ((st.Buttons & GamepadInput.DPAD_LEFT) != 0) b |= BladeButtons.Left;
        if ((st.Buttons & GamepadInput.DPAD_RIGHT) != 0) b |= BladeButtons.Right;

        if (st.LeftTrigger > 127) b |= BladeButtons.L2;
        if (st.RightTrigger > 127) b |= BladeButtons.R2;

        // PS / 触摸板 / 静音在 Binput 的 XInput 位图里没有，从最近原始报告补。
        var raw = ParseRaw(HidPad.LastRaw);
        if (raw.Length > 10)
        {
            if ((raw[10] & 0x01) != 0) b |= BladeButtons.Home;
            if ((raw[10] & 0x02) != 0 || (raw[10] & 0x04) != 0) b |= BladeButtons.Capture;
        }

        return new BladeReport
        {
            Buttons = b,
            LeftX = st.LX,
            LeftY = st.LY,
            RightX = st.RX,
            RightY = st.RY,
            LeftTrigger = st.LeftTrigger,
            RightTrigger = st.RightTrigger,
            SubType = 1,
            RawContent = raw,
        };
    }

    private static byte[] ParseRaw(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                return [];
        }
        return bytes;
    }

    private static bool SameState(BladeReport a, BladeReport b) =>
        a.Buttons == b.Buttons && a.LeftX == b.LeftX && a.LeftY == b.LeftY &&
        a.RightX == b.RightX && a.RightY == b.RightY &&
        a.LeftTrigger == b.LeftTrigger && a.RightTrigger == b.RightTrigger;

    public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        IsConnected = false;
        HidPad.KeepRunning = false;
        try { _cts?.Cancel(); } catch { }
        if (_poll != null)
        {
            try { await _poll.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
        _cts = null;
        try
        {
            if (App.Current?.Ime?.Running != true) HidPad.Stop();
        }
        catch { }
    }
}
