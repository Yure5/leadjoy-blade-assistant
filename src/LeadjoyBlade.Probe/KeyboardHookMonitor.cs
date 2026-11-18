using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace LeadjoyBlade.Probe;

/// <summary>全局低级键盘钩子，用于捕获 BLE HID 键盘集合发出的按键。</summary>
internal static class KeyboardHookMonitor
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? name);

    public static void Run(int seconds)
    {
        HookProc proc = HookCallback;
        var hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            Console.WriteLine($"键盘钩子安装失败：{Marshal.GetLastWin32Error()}");
            return;
        }

        Console.WriteLine("键盘钩子已安装，请按键…");
        var form = new Forms.Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = Forms.FormBorderStyle.None,
            Opacity = 0,
            Width = 1,
            Height = 1,
        };
        var timer = new Forms.Timer { Interval = seconds * 1000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            form.Close();
        };
        form.Shown += (_, _) => timer.Start();
        Forms.Application.Run(form);
        UnhookWindowsHookEx(hook);
        GC.KeepAlive(proc);
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var msg = wParam.ToInt32();
            if (msg is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)
                Console.WriteLine($"[KEY] vk=0x{info.vkCode:X2} scan=0x{info.scanCode:X2} flags=0x{info.flags:X2} msg=0x{msg:X4}");
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
