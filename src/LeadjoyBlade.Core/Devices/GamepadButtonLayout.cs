using System.Text;
using System.Text.Json;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.Core.Devices;

/// <summary>
/// 把 Windows.Gaming.Input 的按键标签解析成风刃按键。
///
/// 关键点：<c>RawGameController.GetButtonLabel()</c> 是否返回有效标签取决于系统里的
/// Windows.Gaming.Input 版本 / 手柄驱动。同一只手柄在 A 机器上能拿到
/// <c>Cross / Left1 / Left3</c> 之类的标签，在 B 机器上可能全部返回 <c>None</c>。
/// 一旦全部为 None，旧逻辑会把每个按键都映射成 <see cref="BladeButtons.None"/>，
/// 结果就是“只有 LT/RT 有反应（它们由摇杆轴阈值合成），其它按键全部无响应”。
///
/// 因此这里在标签缺失时按设备厂商的固定物理顺序回退映射，保证任何机器上都能读到手柄键。
/// </summary>
public sealed class GamepadButtonLayout
{
    private GamepadButtonLayout(string[] labels, BladeButtons[] mapped, int labelCount, int positionalCount)
    {
        Labels = labels;
        Mapped = mapped;
        LabelMappedCount = labelCount;
        PositionalMappedCount = positionalCount;
    }

    /// <summary>系统给出的原始标签文本，按按键序号排列。</summary>
    public string[] Labels { get; }

    /// <summary>按键序号 → 风刃按键。</summary>
    public BladeButtons[] Mapped { get; }

    /// <summary>由系统标签成功解析出的按键个数。</summary>
    public int LabelMappedCount { get; }

    /// <summary>由厂商固定顺序回退得到的按键个数。</summary>
    public int PositionalMappedCount { get; }

    /// <summary>系统完全没有提供可用标签（本次回退的主要场景）。</summary>
    public bool AllLabelsMissing => Labels.Length > 0 && LabelMappedCount == 0;

    /// <summary>至少解析出一个按键。</summary>
    public bool AnyMapped
    {
        get
        {
            foreach (var button in Mapped)
                if (button != BladeButtons.None) return true;
            return false;
        }
    }

    /// <summary>是否用到了位置回退。</summary>
    public bool UsedPositionalFallback => PositionalMappedCount > 0;

    public static GamepadButtonLayout Build(
        int vendorId,
        int productId,
        string? deviceName,
        IReadOnlyList<string> labels,
        int switchCount = 1)
    {
        var count = labels.Count;
        var mapped = new BladeButtons[count];
        var labelCount = 0;
        for (var i = 0; i < count; i++)
        {
            mapped[i] = ParseLabel(labels[i]);
            if (mapped[i] != BladeButtons.None) labelCount++;
        }

        var positional = PositionalLayout(vendorId, productId, deviceName, count, switchCount);
        var positionalCount = 0;
        if (positional != null)
        {
            // 标签全部失效：整表回退。标签部分可用：只补空缺，不动已解析的按键。
            var allLabelsMissing = labelCount == 0;
            for (var i = 0; i < count && i < positional.Length; i++)
            {
                if (mapped[i] != BladeButtons.None) continue;
                var candidate = positional[i];
                if (candidate == BladeButtons.None) continue;
                // 防止把已经由标签确认的按键重复分配给另一个序号，导致按错键。
                if (!allLabelsMissing && Contains(mapped, candidate, i)) continue;
                mapped[i] = candidate;
                positionalCount++;
            }
        }

        return new GamepadButtonLayout(labels.ToArray(), mapped, labelCount, positionalCount);
    }

    private static bool Contains(BladeButtons[] mapped, BladeButtons button, int skipIndex)
    {
        for (var i = 0; i < mapped.Length; i++)
        {
            if (i == skipIndex) continue;
            if (mapped[i] == button) return true;
        }
        return false;
    }

    /// <summary>
    /// 常见手柄的物理按键顺序（HID Button 页 usage 1..N 的顺序）。
    /// 仅在系统标签缺失时使用。
    /// </summary>
    /// <param name="switchCount">
    /// 手柄的十字键（Hat）个数。有 Hat 时十字键由开关量上报，末尾 4 个“十字键按键”兜底条目会被裁掉，
    /// 避免把固件里多声明出来的按键误判成方向键。
    /// </param>
    public static BladeButtons[]? PositionalLayout(
        int vendorId,
        int productId,
        string? deviceName,
        int buttonCount,
        int switchCount = 1)
    {
        if (buttonCount <= 0) return null;

        // 允许用户/售后用 hid-layout.json 覆盖个别机型（见类注释与交接文档）。
        var custom = CustomLayout(vendorId, productId);
        if (custom != null) return custom;

        var layout = BuildBuiltInLayout(vendorId, deviceName);
        // 十字键由 Hat 上报时，不需要末尾的方向键兜底。
        if (switchCount > 0 && layout.Length > 14) return layout[..14];
        return layout;
    }

    private static BladeButtons[] BuildBuiltInLayout(int vendorId, string? deviceName)
    {
        // DualShock 4 / DualSense（Windows 的 Wireless Controller 驱动）
        // 1 Square、2 Cross、3 Circle、4 Triangle、5 L1、6 R1、7 L2、8 R2、
        // 9 Share、10 Options、11 L3、12 R3、13 PS、14 Touchpad
        //
        // 注意：序号 6/7 是 DS4 报文里的 L2/R2“数字位”，它的触发点由固件决定，
        // 可能比映射引擎的 50% 阈值更早；扳机已经有模拟量通道（axes 阈值 + 设置项），
        // 所以这里不把数字位映射成 L2/R2，避免换机器后扳机变得一点就触发。
        if (vendorId == 0x054C)
        {
            var ds4 = new[]
            {
                BladeButtons.X, BladeButtons.A, BladeButtons.B, BladeButtons.Y,
                BladeButtons.L1, BladeButtons.R1, BladeButtons.None, BladeButtons.None,
                BladeButtons.Select, BladeButtons.Start, BladeButtons.L3, BladeButtons.R3,
                BladeButtons.Home, BladeButtons.None,
                // 少数克隆固件把十字键单独声明成 4 个按键
                BladeButtons.Up, BladeButtons.Down, BladeButtons.Left, BladeButtons.Right,
            };
            return ds4;
        }

        // Switch Pro / 风刃 Switch 模式：
        // 1 B、2 A、3 Y、4 X、5 L、6 R、7 ZL、8 ZR、9 Minus、10 Plus、11 LStick、12 RStick、13 Home、14 Capture
        if (vendorId == 0x057E)
        {
            return
            [
                BladeButtons.B, BladeButtons.A, BladeButtons.Y, BladeButtons.X,
                BladeButtons.L1, BladeButtons.R1, BladeButtons.L2, BladeButtons.R2,
                BladeButtons.Select, BladeButtons.Start, BladeButtons.L3, BladeButtons.R3,
                BladeButtons.Home, BladeButtons.Capture,
                BladeButtons.Up, BladeButtons.Down, BladeButtons.Left, BladeButtons.Right,
            ];
        }

        // Xbox 及其它通用 HID 手柄的标准顺序：
        // A、B、X、Y、LB、RB、LT、RT、View、Menu、L3、R3、Guide
        _ = deviceName;
        return
        [
            BladeButtons.A, BladeButtons.B, BladeButtons.X, BladeButtons.Y,
            BladeButtons.L1, BladeButtons.R1, BladeButtons.L2, BladeButtons.R2,
            BladeButtons.Select, BladeButtons.Start, BladeButtons.L3, BladeButtons.R3,
            BladeButtons.Home, BladeButtons.Capture,
            BladeButtons.Up, BladeButtons.Down, BladeButtons.Left, BladeButtons.Right,
        ];
    }

    /// <summary>
    /// 解析 <c>GameControllerButtonLabel.ToString()</c> 的文本。
    /// 先按 Windows.Gaming.Input 枚举名精确匹配，再做包含/后缀启发式，最后返回 None。
    /// </summary>
    public static BladeButtons ParseLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return BladeButtons.None;
        var value = text.Trim();
        if (value.Equals("None", StringComparison.OrdinalIgnoreCase)) return BladeButtons.None;

        switch (value.ToLowerInvariant())
        {
            case "xboxa":
            case "cross":
            case "lettera":
                return BladeButtons.A;
            case "xboxb":
            case "circle":
            case "letterb":
                return BladeButtons.B;
            case "xboxx":
            case "square":
            case "letterx":
                return BladeButtons.X;
            case "xboxy":
            case "triangle":
            case "lettery":
                return BladeButtons.Y;

            // DS 系列的肩键 / 扳机 / 摇杆按下：Left1/Right1、Left2/Right2、Left3/Right3
            case "left1":
            case "xboxleftbumper":
            case "leftbumper":
                return BladeButtons.L1;
            case "right1":
            case "xboxrightbumper":
            case "rightbumper":
                return BladeButtons.R1;
            case "left2":
            case "xboxlefttrigger":
            case "lefttrigger":
                return BladeButtons.L2;
            case "right2":
            case "xboxrighttrigger":
            case "righttrigger":
                return BladeButtons.R2;
            case "left3":
            case "xboxleftstick":
            case "leftstick":
            case "leftstickbutton":
            case "xboxleftstickbutton":
            case "leftthumbstick":
            case "leftthumb":
                return BladeButtons.L3;
            case "right3":
            case "xboxrightstick":
            case "rightstick":
            case "rightstickbutton":
            case "xboxrightstickbutton":
            case "rightthumbstick":
            case "rightthumb":
                return BladeButtons.R3;

            case "dpadup":
            case "xboxup":
                return BladeButtons.Up;
            case "dpaddown":
            case "xboxdown":
                return BladeButtons.Down;
            case "dpadleft":
            case "xboxleft":
                return BladeButtons.Left;
            case "dpadright":
            case "xboxright":
                return BladeButtons.Right;

            case "share":
            case "xboxshare":
            case "select":
            case "back":
            case "view":
            case "xboxview":
            case "minus":
                return BladeButtons.Select;
            case "options":
            case "start":
            case "menu":
            case "xboxstart":
            case "xboxmenu":
            case "plus":
                return BladeButtons.Start;
            case "home":
            case "guide":
            case "xboxguide":
            case "ps":
            case "playstation":
                return BladeButtons.Home;
            case "capture":
                return BladeButtons.Capture;
        }

        // 未知命名（其它厂商/本地化文本）的兜底启发式；顺序不能调整。
        if (value.Contains("DPadUp", StringComparison.OrdinalIgnoreCase)) return BladeButtons.Up;
        if (value.Contains("DPadDown", StringComparison.OrdinalIgnoreCase)) return BladeButtons.Down;
        if (value.Contains("DPadLeft", StringComparison.OrdinalIgnoreCase)) return BladeButtons.Left;
        if (value.Contains("DPadRight", StringComparison.OrdinalIgnoreCase)) return BladeButtons.Right;
        if (value.Contains("Left1", StringComparison.OrdinalIgnoreCase)) return BladeButtons.L1;
        if (value.Contains("Right1", StringComparison.OrdinalIgnoreCase)) return BladeButtons.R1;
        if (value.Contains("Left2", StringComparison.OrdinalIgnoreCase)) return BladeButtons.L2;
        if (value.Contains("Right2", StringComparison.OrdinalIgnoreCase)) return BladeButtons.R2;
        if (value.Contains("Left3", StringComparison.OrdinalIgnoreCase)) return BladeButtons.L3;
        if (value.Contains("Right3", StringComparison.OrdinalIgnoreCase)) return BladeButtons.R3;
        if (value.Contains("Bumper", StringComparison.OrdinalIgnoreCase))
            return value.Contains("Left", StringComparison.OrdinalIgnoreCase) ? BladeButtons.L1 : BladeButtons.R1;
        if (value.Contains("Trigger", StringComparison.OrdinalIgnoreCase))
            return value.Contains("Left", StringComparison.OrdinalIgnoreCase) ? BladeButtons.L2 : BladeButtons.R2;
        if (value.Contains("Stick", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Thumb", StringComparison.OrdinalIgnoreCase))
            return value.Contains("Left", StringComparison.OrdinalIgnoreCase) ? BladeButtons.L3 : BladeButtons.R3;
        if (value.Contains("Share", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Select", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Minus", StringComparison.OrdinalIgnoreCase)) return BladeButtons.Select;
        if (value.Contains("Options", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Start", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Plus", StringComparison.OrdinalIgnoreCase)) return BladeButtons.Start;
        if (value.Contains("Guide", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Home", StringComparison.OrdinalIgnoreCase)) return BladeButtons.Home;
        if (value.Contains("Cross", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith("A", StringComparison.OrdinalIgnoreCase)) return BladeButtons.A;
        if (value.Contains("Circle", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith("B", StringComparison.OrdinalIgnoreCase)) return BladeButtons.B;
        if (value.Contains("Square", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith("X", StringComparison.OrdinalIgnoreCase)) return BladeButtons.X;
        if (value.Contains("Triangle", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith("Y", StringComparison.OrdinalIgnoreCase)) return BladeButtons.Y;

        return BladeButtons.None;
    }

    /// <summary>便于写入诊断日志的一行映射表。</summary>
    public string Describe()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < Mapped.Length; i++)
        {
            if (builder.Length > 0) builder.Append(", ");
            builder.Append(i).Append(':');
            builder.Append(string.IsNullOrWhiteSpace(Labels[i]) ? "?" : Labels[i]);
            builder.Append("→");
            builder.Append(Mapped[i] == BladeButtons.None ? "-" : Mapped[i].ToShortName());
        }
        return builder.ToString();
    }

    /// <summary>可选的机型按键顺序覆盖文件（放在 exe 同目录）。</summary>
    public static string CustomLayoutPath => Path.Combine(AppContext.BaseDirectory, "hid-layout.json");

    private static readonly object CustomSync = new();
    private static Dictionary<string, string[]>? _customRaw;
    private static bool _customLoaded;

    /// <summary>
    /// 读取 hid-layout.json：<c>{ "054C:09CC": ["X","A","B","Y", ...] }</c>。
    /// 键支持 "VID:PID"、"VID" 或 "*"，值为按键名（枚举名或 LB/RB/LT/RT/↑ 等短名，"-" 表示不映射）。
    /// 匹配到覆盖表时优先于内置顺序，便于在特殊固件上不改程序也能修好映射。
    /// </summary>
    public static BladeButtons[]? CustomLayout(int vendorId, int productId)
    {
        string[]? names;
        lock (CustomSync)
        {
            if (!_customLoaded)
            {
                _customLoaded = true;
                _customRaw = LoadCustomRaw();
            }

            names = null;
            _customRaw?.TryGetValue($"{vendorId:X4}:{productId:X4}", out names);
            if (names == null) _customRaw?.TryGetValue($"{vendorId:X4}", out names);
            if (names == null) _customRaw?.TryGetValue("*", out names);
        }

        if (names == null || names.Length == 0) return null;
        var mapped = new BladeButtons[names.Length];
        for (var i = 0; i < names.Length; i++) mapped[i] = ParseName(names[i]);
        return mapped;
    }

    private static Dictionary<string, string[]>? LoadCustomRaw()
    {
        try
        {
            var path = CustomLayoutPath;
            if (!File.Exists(path)) return null;
            var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (raw != null)
                DiagnosticsLog.Write("hiddle.log", $"已加载 {path}（{raw.Count} 组自定义按键顺序）");
            return raw;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("hiddle.log", $"hid-layout.json 解析失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>解析配置里写的按键名：枚举名、短名（LB/LT/↑）或系统标签。</summary>
    public static BladeButtons ParseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return BladeButtons.None;
        var value = name.Trim();
        if (value is "-" || value == "0") return BladeButtons.None;
        if (!int.TryParse(value, out _) &&
            Enum.TryParse<BladeButtons>(value, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
            return parsed;

        return value.ToUpperInvariant() switch
        {
            "LB" => BladeButtons.L1,
            "RB" => BladeButtons.R1,
            "LT" => BladeButtons.L2,
            "RT" => BladeButtons.R2,
            "UP" => BladeButtons.Up,
            "DOWN" => BladeButtons.Down,
            "LEFT" => BladeButtons.Left,
            "RIGHT" => BladeButtons.Right,
            "↑" => BladeButtons.Up,
            "↓" => BladeButtons.Down,
            "←" => BladeButtons.Left,
            "→" => BladeButtons.Right,
            "SELECT" => BladeButtons.Select,
            "START" => BladeButtons.Start,
            "HOME" => BladeButtons.Home,
            "L4" => BladeButtons.M1,
            "R4" => BladeButtons.M2,
            _ => ParseLabel(value),
        };
    }
}
