#pragma warning disable CS0067
using HidSharp;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.Core.Devices;

/// <summary>
/// 通用 USB HID 厂商接口传输。部分风刃/接收器会把 0x20 状态帧直接放进 HID 输入报告，
/// 这里会自动尝试“原始字节”和“去掉 Report ID 首字节”两种解析方式。
/// </summary>
public sealed class HidBladeTransport : IBladeTransport
{
    private readonly HidDevice _device;
    private readonly FrameReader _reader = new();
    private HidStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readTask;

    public HidBladeTransport(HidDevice device)
    {
        _device = device;
    }

    public BladeTransportKind Kind => BladeTransportKind.Hid;
    public string DisplayName => SafeName();
    public bool IsConnected { get; private set; }
    public int Mtu => Math.Max(64, _device.GetMaxOutputReportLength());

    public event EventHandler<BladeReport>? ReportReceived;
    public event EventHandler<Frame>? FrameReceived;
    public event EventHandler<string>? Log;
    public event EventHandler? Disconnected;
    public event EventHandler<int>? BatteryChanged;

    private string SafeName()
    {
        try
        {
            var name = _device.GetProductName();
            return string.IsNullOrWhiteSpace(name) ? $"HID {_device.VendorID:X4}:{_device.ProductID:X4}" : name;
        }
        catch
        {
            return $"HID {_device.VendorID:X4}:{_device.ProductID:X4}";
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_device.TryOpen(out var stream))
            throw new InvalidOperationException($"无法打开 HID 设备 {DisplayName}（可能被其他程序占用）");

        _stream = stream;
        _stream.ReadTimeout = 100;
        IsConnected = true;
        _cts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(_cts.Token));
        Log?.Invoke(this, $"HID 已连接：{DisplayName}，输入报告 {_device.GetMaxInputReportLength()} 字节");
        return Task.CompletedTask;
    }

#pragma warning disable CS1998
    private async Task ReadLoop(CancellationToken token)
    {
        var buffer = new byte[Math.Max(64, _device.GetMaxInputReportLength())];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = _stream!.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Log?.Invoke(this, $"HID 读取异常：{ex.Message}");
                        IsConnected = false;
                        Disconnected?.Invoke(this, EventArgs.Empty);
                    }
                    return;
                }

                if (read <= 0) continue;
                var chunk = buffer.AsSpan(0, read).ToArray();
                var frames = _reader.Feed(chunk);
                if (frames.Count == 0 && read > 4 && chunk[0] == 0)
                {
                    // 某些 HID 栈会在输入报告前附带 Report ID 0
                    frames = _reader.Feed(chunk.AsSpan(1));
                }

                foreach (var frame in frames)
                {
                    FrameReceived?.Invoke(this, frame);
                    if (frame.IsGamepad && BladeReport.TryParse(frame.Payload, out var report) && report != null)
                        ReportReceived?.Invoke(this, report);
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"HID 读取线程退出：{ex.Message}");
        }
    }
#pragma warning restore CS1998

    public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        if (_stream == null) throw new InvalidOperationException("HID 未连接");

        // Windows HID 输出报告通常需要首字节 Report ID；厂商接口常见 ID 0。
        var withId = new byte[frame.Length + 1];
        withId[0] = 0;
        Buffer.BlockCopy(frame, 0, withId, 1, frame.Length);
        try
        {
            _stream.Write(withId, 0, withId.Length);
        }
        catch
        {
            _stream.Write(frame, 0, frame.Length);
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        IsConnected = false;
        try { _cts?.Cancel(); } catch { }
        if (_readTask != null)
        {
            try { await _readTask.ConfigureAwait(false); } catch { }
        }
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        _cts?.Dispose();
        _cts = null;
    }
}

#pragma warning restore CS0067

