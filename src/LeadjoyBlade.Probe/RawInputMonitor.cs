using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace LeadjoyBlade.Probe;

/// <summary>用 Raw Input 监听 HID 游戏手柄的原始输入报告（包括 BLE HID）。</summary>
internal static class RawInputMonitor
{
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RIM_TYPEKEYBOARD = 1;
    private const uint RIM_TYPEHID = 2;
    private const uint RIDEV_INPUTSINK = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWHID
    {
        public int dwSizeHid;
        public int dwCount;
        // bRawData follows
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    public static void Run(int seconds)
    {
        var form = new MonitorForm();
        var timer = new Forms.Timer { Interval = seconds * 1000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            form.Close();
        };
        form.Shown += (_, _) =>
        {
            var devices = new[]
            {
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = form.Handle }, // gamepad
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = form.Handle }, // joystick
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_INPUTSINK, hwndTarget = form.Handle }, // keyboard
                new RAWINPUTDEVICE { usUsagePage = 0x0C, usUsage = 0x01, dwFlags = RIDEV_INPUTSINK, hwndTarget = form.Handle }, // consumer
                new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = form.Handle }, // digitizer
            };
            var ok = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            Console.WriteLine(ok ? "Raw Input 注册成功，请按键…" : $"Raw Input 注册失败：{Marshal.GetLastWin32Error()}");
            timer.Start();
        };
        Forms.Application.Run(form);
    }

    private sealed class MonitorForm : Forms.Form
    {
        private string _last = string.Empty;

        public MonitorForm()
        {
            Text = "RawInputMonitor";
            ShowInTaskbar = false;
            FormBorderStyle = Forms.FormBorderStyle.None;
            Opacity = 0;
            Width = 1;
            Height = 1;
        }

        protected override void WndProc(ref Forms.Message m)
        {
            if (m.Msg == WmInput)
            {
                try { Dump(m.LParam); } catch (Exception ex) { Console.WriteLine($"解析失败：{ex.Message}"); }
            }
            base.WndProc(ref m);
        }

        private void Dump(IntPtr lParam)
        {
            var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            uint size = 0;
            GetRawInputData(lParam, RidInput, IntPtr.Zero, ref size, headerSize);
            if (size == 0) return;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lParam, RidInput, buffer, ref size, headerSize) != size) return;
                var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
                if (header.dwType == RIM_TYPEKEYBOARD)
                {
                    var kb = Marshal.PtrToStructure<RAWKEYBOARD>(IntPtr.Add(buffer, (int)headerSize));
                    Console.WriteLine($"[KEY] vk=0x{kb.VKey:X2} scan=0x{kb.MakeCode:X2} flags=0x{kb.Flags:X2} msg=0x{kb.Message:X4}");
                    return;
                }
                if (header.dwType != RIM_TYPEHID) return;

                var hid = Marshal.PtrToStructure<RAWHID>(IntPtr.Add(buffer, (int)headerSize));
                if (hid.dwSizeHid <= 0 || hid.dwCount <= 0) return;

                var length = hid.dwSizeHid * hid.dwCount;
                var data = new byte[length];
                Marshal.Copy(IntPtr.Add(buffer, (int)headerSize) + 8, data, 0, length);

                var hex = Convert.ToHexString(data);
                if (hex == _last) return;
                _last = hex;
                Console.WriteLine($"[HID] size={hid.dwSizeHid} count={hid.dwCount}  {hex}");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
