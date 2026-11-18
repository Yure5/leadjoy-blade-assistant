using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LeadjoyBlade.Core.Devices;
using LeadjoyBlade.Core.Input;
using LeadjoyBlade.Core.Mapping;
using LeadjoyBlade.Core.Protocol;
using Forms = System.Windows.Forms;

namespace LeadjoyBlade.SelfTest;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    private static int _failures;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== 风刃手柄助手 自检 ===");

        using var form = new Forms.Form { Width = 360, Height = 120, TopMost = true, Text = "SelfTest" };
        var box = new Forms.TextBox { Dock = Forms.DockStyle.Fill };
        form.Controls.Add(box);
        form.Show();
        ForceForeground(form.Handle);
        box.Focus();
        Pump();

        var keyboard = new KeyboardInjector();
        var mouse = new MouseInjector();

        // 1. Unicode 文本注入
        keyboard.TypeText("风刃Test123");
        Pump(300);
        Check(box.Text == "风刃Test123", $"文本注入（实际：{box.Text}）");

        // 2. 普通按键按下/抬起
        box.Clear();
        box.Focus();
        keyboard.KeyDown(0x41); // A
        Pump(120);
        Check(IsDown(0x41), "A 键按下状态");
        keyboard.KeyUp(0x41);
        Pump(120);
        Check(!IsDown(0x41), "A 键抬起状态");

        // 3. 映射引擎：A → B
        var profile = new MappingProfile { Name = "selftest" };
        profile.Bindings.Add(new Binding
        {
            Source = "A",
            Action = new BindingAction { Type = ActionType.Key, Key = 0x42 },
        });
        var engine = new MappingEngine(keyboard, mouse);
        engine.SetProfile(profile);
        engine.Enabled = true;

        engine.Update(new BladeReport { Buttons = BladeButtons.A, Timestamp = DateTime.UtcNow });
        Pump(150);
        Check(IsDown(0x42), "映射 A→B：B 应按下");
        engine.Update(new BladeReport { Buttons = BladeButtons.None, Timestamp = DateTime.UtcNow });
        Pump(150);
        Check(!IsDown(0x42), "映射 A→B：B 应抬起");

        // 4. 协议帧解析：构造一帧 B 键按下的 0x20 报文
        var payload = new byte[15];
        payload[0] = 0x01;
        payload[1] = 0x02; // bit1 = B
        for (var axis = 5; axis <= 11; axis += 2) payload[axis + 1] = 0x80; // 0x8000 = 中点
        var raw = new byte[3 + payload.Length + 1];
        raw[0] = FrameReader.HeaderShort;
        raw[1] = (byte)raw.Length;
        raw[2] = FrameReader.TypeGamepad;
        payload.CopyTo(raw, 3);
        raw[^1] = FrameReader.Checksum(raw, 0, raw.Length - 1);
        var frames = new FrameReader().Feed(raw);
        Check(frames.Count == 1 && frames[0].IsGamepad, "0x20 帧解析");
        Check(BladeReport.TryParse(frames[0].Payload, out var parsed) && parsed!.IsPressed(BladeButtons.B),
            "位图 bit1 → B");
        Check(parsed!.LeftX == 0 && parsed.LeftY == 0 && parsed.RightX == 0 && parsed.RightY == 0,
            "摇杆 0x8000 → 居中 0");
        var topPayload = (byte[])frames[0].Payload.Clone();
        topPayload[7] = 0x00;
        topPayload[8] = 0x00;
        Check(BladeReport.Parse(topPayload).LeftY == short.MaxValue, "Y 到顶不溢出（32767）");

        // 4.1 开启上报命令的字节级校验
        var cmd = BladeProtocol.BuildCommandFrames(BladeProtocol.EnableGamepadReport(true), 185);
        var expected = new byte[] { 0xA5, 0x0B, 0xCC, 0x82, 0xA1, 0x63, 0x28, 0xA1, 0x73, 0x01, 0x3F };
        Check(cmd.Count == 1 && cmd[0].SequenceEqual(expected), "c=40 命令帧字节");

        // 5. Fn 层覆盖
        profile.Bindings.Clear();
        profile.Bindings.Add(new Binding { Source = "M1", Action = new BindingAction { Type = ActionType.Key, Key = 0x70 } });
        profile.Bindings.Add(new Binding
        {
            Source = "M1",
            Layer = BindingLayer.Fn,
            Action = new BindingAction { Type = ActionType.Key, Key = 0x71 },
        });
        engine.SetProfile(profile);
        engine.Update(new BladeReport { Buttons = BladeButtons.Fn, Timestamp = DateTime.UtcNow });
        Pump(80);
        engine.Update(new BladeReport { Buttons = BladeButtons.Fn | BladeButtons.M1, Timestamp = DateTime.UtcNow });
        Pump(180);
        Check(IsDown(0x71), "Fn 层：Fn+M1 应触发 F2");
        engine.Update(new BladeReport { Buttons = BladeButtons.None, Timestamp = DateTime.UtcNow });
        Pump(150);
        Check(!IsDown(0x71), "Fn 层释放");

        // 5. 组合键 Fn+Start → Ctrl+Enter
        profile.Bindings.Clear();
        profile.Chords.Clear();
        profile.Chords.Add(new ChordBinding
        {
            Name = "Fn+Start",
            Sources = ["Fn", "Start"],
            Action = new BindingAction
            {
                Type = ActionType.Key,
                Key = 0x0D,
                Modifiers = [KeyCatalog.VkControl],
            },
        });
        engine.SetProfile(profile);
        engine.Update(new BladeReport { Buttons = BladeButtons.Fn, Timestamp = DateTime.UtcNow });
        Pump(60);
        engine.Update(new BladeReport { Buttons = BladeButtons.Fn | BladeButtons.Start, Timestamp = DateTime.UtcNow });
        Pump(200);
        Check(IsDown(0x11), "组合键：Ctrl 应按下");
        Check(IsDown(0x0D), "组合键：Enter 应按下");
        engine.Update(new BladeReport { Buttons = BladeButtons.None, Timestamp = DateTime.UtcNow });
        Pump(200);
        Check(!IsDown(0x0D), "组合键释放：Enter");

        // 6. 鼠标按键
        profile.Chords.Clear();
        profile.Bindings.Clear();
        profile.Bindings.Add(new Binding
        {
            Source = "R3",
            Action = new BindingAction { Type = ActionType.MouseButton, MouseButton = MouseButtonKind.Left },
        });
        engine.SetProfile(profile);
        engine.Update(new BladeReport { Buttons = BladeButtons.R3, Timestamp = DateTime.UtcNow });
        Pump(120);
        Check(IsDown(0x01), "鼠标左键按下");
        engine.Update(new BladeReport { Buttons = BladeButtons.None, Timestamp = DateTime.UtcNow });
        Pump(120);
        Check(!IsDown(0x01), "鼠标左键抬起");

        engine.ReleaseAll();
        keyboard.ReleaseAll();
        mouse.ReleaseAll();

        // 7. 手柄按键标签解析与“标签缺失”回退（不同电脑/系统版本上 DS4 按键无反应的核心逻辑）
        Check(GamepadButtonLayout.ParseLabel("Cross") == BladeButtons.A, "标签 Cross → A");
        Check(GamepadButtonLayout.ParseLabel("Left1") == BladeButtons.L1, "标签 Left1 → LB");
        Check(GamepadButtonLayout.ParseLabel("Right2") == BladeButtons.R2, "标签 Right2 → RT");
        Check(GamepadButtonLayout.ParseLabel("Left3") == BladeButtons.L3, "标签 Left3 → L3");
        Check(GamepadButtonLayout.ParseLabel("None") == BladeButtons.None, "标签 None → 无");

        // 标签全部缺失时按 DS4 固定物理顺序回退（Square/Cross/Circle/Triangle/L1/R1/L2/R2/…）
        var deadLabels = Enumerable.Repeat("None", 14).ToArray();
        var dead = GamepadButtonLayout.Build(0x054C, 0x09CC, "Wireless Controller", deadLabels);
        Check(dead.AllLabelsMissing, "识别“标签全部缺失”");
        Check(dead.Mapped[1] == BladeButtons.A && dead.Mapped[2] == BladeButtons.B &&
              dead.Mapped[0] == BladeButtons.X && dead.Mapped[3] == BladeButtons.Y,
            "标签缺失回退：ABXY");
        Check(dead.Mapped[4] == BladeButtons.L1 && dead.Mapped[5] == BladeButtons.R1,
            "标签缺失回退：LB/RB");
        Check(dead.Mapped[6] == BladeButtons.None && dead.Mapped[7] == BladeButtons.None,
            "标签缺失回退：DS4 扳机数字位不映射（仍走模拟量）");
        Check(dead.Mapped[8] == BladeButtons.Select && dead.Mapped[9] == BladeButtons.Start &&
              dead.Mapped[10] == BladeButtons.L3 && dead.Mapped[11] == BladeButtons.R3 &&
              dead.Mapped[12] == BladeButtons.Home,
            "标签缺失回退：Select/Start/L3/R3/Home");

        // 标签正常时保持原有语义，只用位置补空缺
        var goodLabels = new[]
        {
            "Square", "Cross", "Circle", "Triangle", "Left1", "Right1", "None", "None",
            "View", "Menu", "Left3", "Right3", "None", "None",
        };
        var good = GamepadButtonLayout.Build(0x054C, 0x09CC, "HID-compliant game controller", goodLabels);
        Check(!good.AllLabelsMissing && good.Mapped[1] == BladeButtons.A && good.Mapped[10] == BladeButtons.L3,
            "标签正常：语义不变");
        Check(good.Mapped[4] == BladeButtons.L1 && good.Mapped[5] == BladeButtons.R1 &&
              good.Mapped[8] == BladeButtons.Select && good.Mapped[9] == BladeButtons.Start &&
              good.Mapped[11] == BladeButtons.R3,
            "本机真实标签集合解析正确");
        Check(good.Mapped[6] == BladeButtons.None && good.Mapped[7] == BladeButtons.None &&
              good.Mapped[12] == BladeButtons.Home,
            "标签正常：扳机数字位保持不映射、PS 补 Home");

        // Switch 模式只有 4 个轴，扳机必须靠按键位（实测 buttons=16 / axes=4）
        var switchDead = GamepadButtonLayout.Build(0x057E, 0x2009, "Pro Controller",
            Enumerable.Repeat("None", 16).ToArray(), switchCount: 1);
        Check(switchDead.Mapped[6] == BladeButtons.L2 && switchDead.Mapped[7] == BladeButtons.R2 &&
              switchDead.Mapped[8] == BladeButtons.Select && switchDead.Mapped[12] == BladeButtons.Home,
            "Switch 标签缺失：扳机与功能键按位置回退");

        // 十字键由 Hat 上报时，不做“方向键按键”兜底，避免误判
        var noHat = GamepadButtonLayout.Build(0x054C, 0x09CC, "x",
            Enumerable.Repeat("None", 18).ToArray(), switchCount: 0);
        Check(noHat.Mapped[14] == BladeButtons.Up && noHat.Mapped[15] == BladeButtons.Down &&
              noHat.Mapped[16] == BladeButtons.Left && noHat.Mapped[17] == BladeButtons.Right,
            "无 Hat 时十字键按 4 键兜底");
        var withHat = GamepadButtonLayout.Build(0x054C, 0x09CC, "x",
            Enumerable.Repeat("None", 18).ToArray(), switchCount: 1);
        Check(withHat.Mapped[14] == BladeButtons.None, "有 Hat 时不做方向键兜底");

        Check(GamepadButtonLayout.ParseName("LB") == BladeButtons.L1 &&
              GamepadButtonLayout.ParseName("Up") == BladeButtons.Up &&
              GamepadButtonLayout.ParseName("-") == BladeButtons.None,
            "hid-layout.json 按键名解析");

        // 8. Switch 报文解析（0x3F 标准集合 / 0x30 任天堂全量，两套位序必须分开）
        SwitchParserTests();

        // 9. 系统菜单数据模型与持久化
        SystemMenuTests();

        Console.WriteLine(_failures == 0 ? "全部通过。" : $"失败 {_failures} 项。");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Switch 报文解析：0x3F（标准集合）与 0x30（任天堂全量）必须分派正确、互不串键。</summary>
    private static void SwitchParserTests()
    {
        static byte[] Frame(byte id, byte b1, byte b2, byte b3)
        {
            var f = new byte[16];
            f[0] = id;
            f[1] = b1;
            f[2] = b2;
            f[3] = b3;
            f[4] = 0x00; f[5] = 0x80;
            f[6] = 0x00; f[7] = 0x80;
            f[8] = 0x00; f[9] = 0x80;
            f[10] = 0x00; f[11] = 0x80;
            return f;
        }

        static SwitchPadState P(byte[] f) => SwitchReportParser.Parse(f, f.Length);

        // 0x3F：B 机实测位序
        Check(P(Frame(0x3F, 0x02, 0x00, 0x08)).A, "0x3F A 位");
        Check(P(Frame(0x3F, 0x01, 0x00, 0x08)).B, "0x3F B 位");
        Check(P(Frame(0x3F, 0x08, 0x00, 0x08)).X, "0x3F X 位");
        Check(P(Frame(0x3F, 0x04, 0x00, 0x08)).Y, "0x3F Y 位");
        var lb = P(Frame(0x3F, 0x10, 0x00, 0x08));
        Check(lb.L1 && !lb.A && !lb.X, "0x3F LB 不被误读成 ABXY");
        var rb = P(Frame(0x3F, 0x20, 0x00, 0x08));
        Check(rb.R1 && !rb.AnyButton == false, "0x3F RB 位");
        Check(P(Frame(0x3F, 0x40, 0x00, 0x08)).L2, "0x3F LT 位");
        Check(P(Frame(0x3F, 0x80, 0x00, 0x08)).R2, "0x3F RT 位");
        var home = P(Frame(0x3F, 0x00, 0x10, 0x08));
        Check(home.Home && !home.A, "0x3F Home 位");
        Check(P(Frame(0x3F, 0x00, 0x04, 0x08)).L3 && !P(Frame(0x3F, 0x00, 0x04, 0x08)).B, "0x3F L3 位");
        Check(P(Frame(0x3F, 0x00, 0x00, 0x00)).Up, "0x3F hat 0 = 上");
        Check(P(Frame(0x3F, 0x00, 0x00, 0x04)).Down, "0x3F hat 4 = 下");
        Check(P(Frame(0x3F, 0x00, 0x00, 0x02)).Right, "0x3F hat 2 = 右");
        var neutral3F = P(Frame(0x3F, 0x00, 0x00, 0x08));
        Check(!neutral3F.AnyButton && neutral3F.LeftX == 0 && neutral3F.LeftY == 0, "0x3F 中性帧无按键、摇杆居中");

        // 0x30：A 机实测位序
        var frame30 = new byte[16];
        frame30[0] = 0x30; frame30[1] = 0x6A; frame30[2] = 0x41;
        frame30[3] = 0x08; frame30[4] = 0x80; frame30[5] = 0x00;
        frame30[6] = 0x00; frame30[7] = 0x08; frame30[8] = 0x7F;
        frame30[9] = 0x00; frame30[10] = 0x08; frame30[11] = 0x7F;
        var a30 = SwitchReportParser.Parse(frame30, frame30.Length);
        Check(a30.Kind == SwitchReportKind.Full && a30.A && !a30.X, "0x30 A 位（b3 0x08）");
        frame30[3] = 0x40;
        Check(SwitchReportParser.Parse(frame30, frame30.Length).R1, "0x30 RB 位（b3 0x40）");
        frame30[3] = 0x00; frame30[5] = 0x40;
        Check(SwitchReportParser.Parse(frame30, frame30.Length).L1, "0x30 LB 位（b5 0x40）");
        frame30[5] = 0x00; frame30[4] = 0x90;
        Check(SwitchReportParser.Parse(frame30, frame30.Length).Home, "0x30 Home 位（b4 0x10）");

        // 未知报文 & 短报文
        var unknown = P(Frame(0x99, 0x0F, 0x0F, 0x00));
        Check(unknown.Kind == SwitchReportKind.Unknown && !unknown.AnyButton, "未知报文不误报按键");
        Check(SwitchReportParser.Parse(new byte[8], 8).Kind == SwitchReportKind.Unknown, "短报文安全返回");
    }

    /// <summary>系统菜单：默认项、确认/取消不能相同、JSON 往返、独立宏不引用全局宏库。</summary>
    private static void SystemMenuTests()
    {
        var fantasy = new FantasyProfile
        {
            SystemMenuEnabled = true,
            SystemMenuHotkey = "Start",
            SystemMenuConfirmButton = "Y",
            SystemMenuCancelButton = "Y",
        };
        SystemMenuDefaults.Normalize(fantasy);
        Check(fantasy.SystemMenuHotkey == "Start", "系统菜单：Start 呼出键保留");
        Check(fantasy.SystemMenuConfirmButton == "Y" && fantasy.SystemMenuCancelButton != "Y",
            "系统菜单：确认/取消自动错开");
        Check(fantasy.SystemMenuCategories.Count > 1 && fantasy.SystemMenuCategories[0].Items.Count > 0,
            "系统菜单：默认菜单栏与项目");

        var action = SystemMenuAction.CreateKey(0x43, KeyCatalog.VkControl);
        Check(action.Describe() == "键盘 Ctrl+C", "系统菜单：键盘组合键描述");
        var clone = action.Clone();
        clone.Key = 0x56;
        Check(action.Key == 0x43 && clone.Key == 0x56, "系统菜单：动作深拷贝");

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };
        var json = JsonSerializer.Serialize(fantasy, options);
        var restored = JsonSerializer.Deserialize<FantasyProfile>(json, options);
        Check(restored != null && restored.SystemMenuCategories.Count == fantasy.SystemMenuCategories.Count
              && restored.SystemMenuCategories[0].Items[0].Action.Kind == SystemMenuActionKind.Key,
            "系统菜单：JSON 往返");

        var macroAction = new SystemMenuAction
        {
            Kind = SystemMenuActionKind.Macro,
            Steps =
            [
                new SystemMenuMacroStep { Kind = SystemMenuStepKind.Key, Key = 0x41 },
                new SystemMenuMacroStep { Kind = SystemMenuStepKind.Gamepad, GamepadButton = "B" },
                new SystemMenuMacroStep { Kind = SystemMenuStepKind.MouseClick, MouseX = 0.5, MouseY = 0.5 },
                new SystemMenuMacroStep { Kind = SystemMenuStepKind.Delay, DelayMs = 100 },
            ],
        };
        Check(macroAction.Steps.Count == 4 && macroAction.Describe().Contains("4 步"),
            "系统菜单：独立宏混合步骤");
    }

    /// <summary>把 SelfTest 窗口强制切到前台；否则文本注入会落到 Explorer 地址栏等窗口。</summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != currentThread
                       && AttachThreadInput(currentThread, foregroundThread, true);
        SetForegroundWindow(hwnd);
        SetFocus(hwnd);
        if (attached) AttachThreadInput(currentThread, foregroundThread, false);
    }

    private static void Pump(int ms = 0)
    {
        var end = Environment.TickCount + ms;
        do
        {
            Forms.Application.DoEvents();
            Thread.Sleep(10);
        } while (Environment.TickCount < end);
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "  PASS  " : "  FAIL  ") + name);
        if (!condition) _failures++;
    }
}
