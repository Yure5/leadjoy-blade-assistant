using System.Runtime.InteropServices;

namespace LeadjoyBlade.Core.Input;

public sealed class MouseInjector
{
    private readonly object _sync = new();
    private readonly HashSet<MouseButtonKind> _pressed = new();

    public void ButtonDown(MouseButtonKind button) => SendButton(button, true);
    public void ButtonUp(MouseButtonKind button) => SendButton(button, false);

    private void SendButton(MouseButtonKind button, bool down)
    {
        lock (_sync)
        {
            if (down) _pressed.Add(button);
            else if (!_pressed.Remove(button)) return;

            var (flag, data) = button switch
            {
                MouseButtonKind.Left => (down ? NativeInput.MOUSEEVENTF_LEFTDOWN : NativeInput.MOUSEEVENTF_LEFTUP, 0u),
                MouseButtonKind.Right => (down ? NativeInput.MOUSEEVENTF_RIGHTDOWN : NativeInput.MOUSEEVENTF_RIGHTUP, 0u),
                MouseButtonKind.Middle => (down ? NativeInput.MOUSEEVENTF_MIDDLEDOWN : NativeInput.MOUSEEVENTF_MIDDLEUP, 0u),
                MouseButtonKind.X1 => (down ? NativeInput.MOUSEEVENTF_XDOWN : NativeInput.MOUSEEVENTF_XUP, 1u),
                MouseButtonKind.X2 => (down ? NativeInput.MOUSEEVENTF_XDOWN : NativeInput.MOUSEEVENTF_XUP, 2u),
                _ => (0u, 0u),
            };

            var input = new NativeInput.INPUT
            {
                type = NativeInput.INPUT_MOUSE,
                U = new NativeInput.InputUnion
                {
                    mi = new NativeInput.MOUSEINPUT
                    {
                        dwFlags = flag,
                        mouseData = data,
                    },
                },
            };
            NativeInput.SendInput(1, [input], Marshal.SizeOf<NativeInput.INPUT>());
        }
    }

    /// <summary>
    /// 把光标移动到屏幕坐标后点击，再把光标移回原处。
    /// 系统菜单的“固定位置点击”使用游戏窗口客户区的相对坐标，最终换算成屏幕坐标。
    /// </summary>
    public void ClickAt(int screenX, int screenY, MouseButtonKind button, int holdMilliseconds = 45)
    {
        var hasOriginal = GetCursorPos(out var original);
        if (!SetCursorPos(screenX, screenY)) return;
        Thread.Sleep(12);
        ButtonDown(button);
        Thread.Sleep(Math.Max(10, holdMilliseconds));
        ButtonUp(button);
        Thread.Sleep(12);
        if (hasOriginal) SetCursorPos(original.X, original.Y);
    }

    /// <summary>把光标移动到屏幕坐标（不点击）。</summary>
    public bool MoveTo(int screenX, int screenY) => SetCursorPos(screenX, screenY);

    public void Wheel(int delta) => SendMouse(0, 0, (uint)delta, NativeInput.MOUSEEVENTF_WHEEL);
    public void HorizontalWheel(int delta) => SendMouse(0, 0, (uint)delta, NativeInput.MOUSEEVENTF_HWHEEL);

    public void MoveBy(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        SendMouse(dx, dy, 0, NativeInput.MOUSEEVENTF_MOVE);
    }

    private static void SendMouse(int dx, int dy, uint data, uint flags)
    {
        var input = new NativeInput.INPUT
        {
            type = NativeInput.INPUT_MOUSE,
            U = new NativeInput.InputUnion
            {
                mi = new NativeInput.MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = data,
                    dwFlags = flags,
                },
            },
        };
        NativeInput.SendInput(1, [input], Marshal.SizeOf<NativeInput.INPUT>());
    }

    public void ReleaseAll()
    {
        lock (_sync)
        {
            foreach (var button in _pressed.ToArray()) SendButton(button, false);
            _pressed.Clear();
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
}

public enum MouseButtonKind
{
    Left,
    Right,
    Middle,
    X1,
    X2,
}
