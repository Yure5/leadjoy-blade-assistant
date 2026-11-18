using System.Runtime.InteropServices;

namespace LeadjoyBlade.Core.Input;

/// <summary>基于 SendInput 的键盘注入。使用扫描码，兼容 DirectInput 游戏。</summary>
public sealed class KeyboardInjector
{
    private readonly object _sync = new();
    private readonly HashSet<int> _pressed = new();

    public bool IsKeyDown(int vk)
    {
        lock (_sync) return _pressed.Contains(vk);
    }

    public void KeyDown(int vk)
    {
        lock (_sync)
        {
            if (!_pressed.Add(vk)) return;
            SendKey(vk, down: true);
        }
    }

    public void KeyUp(int vk)
    {
        lock (_sync)
        {
            if (!_pressed.Remove(vk)) return;
            SendKey(vk, down: false);
        }
    }

    public void Tap(int vk, int holdMilliseconds = 30)
    {
        KeyDown(vk);
        Thread.Sleep(Math.Max(10, holdMilliseconds));
        KeyUp(vk);
    }

    /// <summary>按下修饰键 + 主键，再按相反顺序释放。</summary>
    public void Chord(IReadOnlyList<int> modifiers, int vk, int holdMilliseconds = 40)
    {
        lock (_sync)
        {
            foreach (var modifier in modifiers) KeyDown(modifier);
            KeyDown(vk);
            Thread.Sleep(Math.Max(10, holdMilliseconds));
            KeyUp(vk);
            for (var i = modifiers.Count - 1; i >= 0; i--) KeyUp(modifiers[i]);
        }
    }

    /// <summary>
    /// 输入文本。ASCII 直接走 Unicode SendInput；中文等非 ASCII 使用剪贴板 + Ctrl+V，
    /// 因为很多游戏的聊天框只接受真实 IME/粘贴，不接受 KEYEVENTF_UNICODE。
    /// </summary>
    public void TypeText(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var ascii = text[index] <= 0x7E;
            var start = index;
            while (index < text.Length && (text[index] <= 0x7E) == ascii) index++;
            var run = text[start..index];
            if (ascii) SendUnicodeText(run);
            else PasteText(run);
        }
    }

    private static void SendUnicodeText(string text)
    {
        foreach (var ch in text)
        {
            var down = new NativeInput.INPUT
            {
                type = NativeInput.INPUT_KEYBOARD,
                U = new NativeInput.InputUnion
                {
                    ki = new NativeInput.KEYBDINPUT
                    {
                        wScan = ch,
                        dwFlags = NativeInput.KEYEVENTF_UNICODE,
                    },
                },
            };
            var up = down;
            up.U.ki.dwFlags = NativeInput.KEYEVENTF_UNICODE | NativeInput.KEYEVENTF_KEYUP;
            NativeInput.SendInput(2, [down, up], Marshal.SizeOf<NativeInput.INPUT>());
            Thread.Sleep(8);
        }
    }

    private void PasteText(string text)
    {
        var original = GetClipboardText();
        if (!SetClipboardText(text))
        {
            SendUnicodeText(text);
            return;
        }

        Thread.Sleep(20);
        KeyDown(0x11); // Ctrl
        KeyDown(0x56); // V
        Thread.Sleep(30);
        KeyUp(0x56);
        KeyUp(0x11);
        Thread.Sleep(120);
        if (original != null) SetClipboardText(original);
    }

    private static string? GetClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            if (!IsClipboardFormatAvailable(CfUnicodeText)) return null;
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero) return null;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool SetClipboardText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
            if (handle == IntPtr.Zero) return false;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
            try
            {
                var chars = text.ToCharArray();
                Marshal.Copy(chars, 0, pointer, chars.Length);
                Marshal.WriteInt16(pointer, chars.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    public void ReleaseAll()
    {
        lock (_sync)
        {
            foreach (var vk in _pressed.ToArray()) SendKey(vk, down: false);
            _pressed.Clear();
        }
    }

    private static void SendKey(int vk, bool down)
    {
        var scan = (ushort)(NativeInput.MapVirtualKey((uint)vk, NativeInput.MAPVK_VK_TO_VSC_EX) & 0xFF);
        var extended = (NativeInput.MapVirtualKey((uint)vk, NativeInput.MAPVK_VK_TO_VSC_EX) & 0xFF00) == 0xE000;
        var flags = NativeInput.KEYEVENTF_SCANCODE;
        if (extended) flags |= NativeInput.KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= NativeInput.KEYEVENTF_KEYUP;

        var input = new NativeInput.INPUT
        {
            type = NativeInput.INPUT_KEYBOARD,
            U = new NativeInput.InputUnion
            {
                ki = new NativeInput.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scan,
                    dwFlags = flags,
                },
            },
        };

        // 少数按键没有扫描码（如媒体键），退回虚拟键方式
        if (scan == 0)
        {
            input.U.ki.wVk = (ushort)vk;
            input.U.ki.wScan = 0;
            input.U.ki.dwFlags = down ? 0 : NativeInput.KEYEVENTF_KEYUP;
        }

        var sent = NativeInput.SendInput(1, [input], Marshal.SizeOf<NativeInput.INPUT>());
        if (sent == 0)
        {
            // 某些环境下扫描码被拒绝，使用 VK 兜底
            input.U.ki.wVk = (ushort)vk;
            input.U.ki.wScan = 0;
            input.U.ki.dwFlags = down ? 0 : NativeInput.KEYEVENTF_KEYUP;
            NativeInput.SendInput(1, [input], Marshal.SizeOf<NativeInput.INPUT>());
        }
    }
}
