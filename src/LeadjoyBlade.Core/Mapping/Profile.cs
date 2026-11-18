using System.Text.Json.Serialization;
using LeadjoyBlade.Core.Input;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.Core.Mapping;

public enum BindingLayer
{
    Base,
    Fn,
}

public enum ActionType
{
    Key,
    Text,
    MouseButton,
    MouseWheel,
    Macro,
    Disabled,
}

public enum MacroStepKind
{
    Key,
    KeyDown,
    KeyUp,
    Delay,
    MouseButton,
    MouseDown,
    MouseUp,
    Gamepad,
    GamepadDown,
    GamepadUp,
    Text,
}

public sealed class MacroStep
{
    public MacroStepKind Kind { get; set; } = MacroStepKind.Key;
    public int Key { get; set; }
    public List<int> Modifiers { get; set; } = [];
    public int HoldMs { get; set; } = 30;
    public int DelayMs { get; set; } = 30;
    public MouseButtonKind MouseButton { get; set; } = MouseButtonKind.Left;
    public string GamepadSource { get; set; } = "A";

    /// <summary>Kind=Text 时要输入的文字。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>鼠标步骤是否使用固定位置；旧宏默认 false = 点击当前光标。</summary>
    public bool MouseUsePosition { get; set; }

    /// <summary>固定位置（0~1）：窗口模式相对进程客户区，屏幕模式相对 MouseMonitor。</summary>
    public double MouseX { get; set; } = 0.5;
    public double MouseY { get; set; } = 0.5;
    public bool MouseScreen { get; set; }
    public string? MouseMonitor { get; set; }
    public int MouseMonitorX { get; set; }
    public int MouseMonitorY { get; set; }
    public int MouseMonitorWidth { get; set; }
    public int MouseMonitorHeight { get; set; }
}

public sealed class BindingAction
{
    public ActionType Type { get; set; } = ActionType.Key;
    public int Key { get; set; } = 0x20;
    public List<int> Modifiers { get; set; } = [];
    public string Text { get; set; } = string.Empty;
    public MouseButtonKind MouseButton { get; set; } = MouseButtonKind.Left;
    public int WheelDelta { get; set; } = 120;

    /// <summary>鼠标按键/滚轮是否使用固定位置；旧映射默认 false = 当前光标。</summary>
    public bool MouseUsePosition { get; set; }
    public double MouseX { get; set; } = 0.5;
    public double MouseY { get; set; } = 0.5;
    public bool MouseScreen { get; set; }
    public string? MouseMonitor { get; set; }
    public int MouseMonitorX { get; set; }
    public int MouseMonitorY { get; set; }
    public int MouseMonitorWidth { get; set; }
    public int MouseMonitorHeight { get; set; }

    public List<MacroStep> Macro { get; set; } = [];
    public string MacroId { get; set; } = "";
    public string MacroName { get; set; } = "";
    public string MacroSource { get; set; } = "";
    public bool Turbo { get; set; }
    public int TurboIntervalMs { get; set; } = 50;
    public bool Toggle { get; set; }

    [JsonIgnore]
    public bool IsHoldAction => Type is ActionType.Key or ActionType.MouseButton;

    public string Describe()
    {
        var mods = string.Join("+", Modifiers.Select(KeyCatalog.GetName));
        var prefix = string.IsNullOrEmpty(mods) ? string.Empty : mods + "+";
        return Type switch
        {
            ActionType.Key => prefix + KeyCatalog.GetName(Key) + (Toggle ? "（切换）" : Turbo ? "（连发）" : ""),
            ActionType.Text => $"文本「{Text}」",
            ActionType.MouseButton => $"鼠标{MouseButton}",
            ActionType.MouseWheel => WheelDelta > 0 ? "滚轮上" : "滚轮下",
            ActionType.Macro => string.IsNullOrWhiteSpace(MacroName) ? $"宏（{Macro.Count} 步）" : $"宏「{MacroName}」",
            ActionType.Disabled => "不映射",
            _ => Type.ToString(),
        };
    }
}

public sealed class Binding
{
    public string Name { get; set; } = "宏";
    public string Source { get; set; } = "A";
    public BindingLayer Layer { get; set; } = BindingLayer.Base;
    public BindingAction Action { get; set; } = new();
    public bool Enabled { get; set; } = true;
}

public sealed class ChordBinding
{
    public string Name { get; set; } = "组合键";
    public List<string> Sources { get; set; } = [];
    public BindingAction Action { get; set; } = new();
    public bool Enabled { get; set; } = true;

    public string Describe() => $"{string.Join(" + ", Sources.Select(MappingSources.GetDisplayName))} → {Action.Describe()}";
}

public sealed class ProfileSettings
{
    public bool Enabled { get; set; } = true;
    public double LeftStickDeadzone { get; set; } = 0.25;
    public double RightStickDeadzone { get; set; } = 0.25;
    public bool InvertLeftY { get; set; }
    public bool InvertRightY { get; set; }
    public bool RightStickAsMouse { get; set; }
    public bool LeftStickAsMouse { get; set; }
    public double MouseSensitivity { get; set; } = 1.0;
    public double MouseAcceleration { get; set; } = 0.2;
    public int MouseDeadzonePercent { get; set; } = 25;
    public bool TriggerAsButton { get; set; } = true;
    public bool LeftTriggerAsButton { get; set; } = true;
    public bool RightTriggerAsButton { get; set; } = true;
}

/// <summary>
/// 每个配置自己的手柄输入法设置。旧的 Binput 全局配置只在首次迁移时作为默认值，
/// 之后切换配置以这里为准。
/// </summary>
public sealed class ImeProfileSettings
{
    /// <summary>是否已经迁移/写过；false 表示这是旧配置，启动时用全局值补一次。</summary>
    public bool Configured { get; set; }
    public bool Enabled { get; set; }
    public bool Preview { get; set; } = true;
    public string WakeSpec { get; set; } = "";
    public int Tier { get; set; }
}

public sealed class MappingProfile
{
    public string Name { get; set; } = "默认配置";
    public bool IsDefault { get; set; }
    public string? ProcessName { get; set; }
    public string DeviceModel { get; set; } = "auto";
    public List<Binding> Bindings { get; set; } = [];
    public List<ChordBinding> Chords { get; set; } = [];
    public FantasyProfile Fantasy { get; set; } = new();
    public ProfileSettings Settings { get; set; } = new();
    public ImeProfileSettings Ime { get; set; } = new();

    public Binding? FindBinding(string source, BindingLayer layer) =>
        Bindings.FirstOrDefault(b => b.Enabled && b.Source == source && b.Layer == layer);

    public static MappingProfile CreateDefault()
    {
        var profile = new MappingProfile { Name = "默认配置", IsDefault = true };
        profile.Settings.Enabled = false;
        // 一套可直接使用的示例：ABXY → 键盘，背键 → F1-F4，Fn 层 → 数字键
        var defaults = new (string Source, int Key)[]
        {
            ("A", 0x20), ("B", 0x1B), ("X", 0x52), ("Y", 0x46),
            ("L1", 0x51), ("R1", 0x45),
            ("M1", 0x70), ("M2", 0x71), ("M3", 0x72), ("M4", 0x73),
            ("Up", 0x26), ("Down", 0x28), ("Left", 0x25), ("Right", 0x27),
        };
        foreach (var (source, key) in defaults)
        {
            profile.Bindings.Add(new Binding
            {
                Source = source,
                Action = new BindingAction { Type = ActionType.Key, Key = key },
            });
        }

        profile.Bindings.Add(new Binding
        {
            Source = "M1",
            Layer = BindingLayer.Fn,
            Action = new BindingAction { Type = ActionType.Key, Key = 0x31 },
        });
        profile.Bindings.Add(new Binding
        {
            Source = "M2",
            Layer = BindingLayer.Fn,
            Action = new BindingAction { Type = ActionType.Key, Key = 0x32 },
        });
        profile.Chords.Add(new ChordBinding
        {
            Name = "Fn + Start",
            Sources = ["Fn", "Start"],
            Action = new BindingAction { Type = ActionType.Key, Key = 0x0D, Modifiers = [KeyCatalog.VkControl] },
        });
        return profile;
    }
}

public static class MappingSources
{
    public sealed record SourceInfo(string Id, string DisplayName, string Category);

    public static readonly IReadOnlyList<SourceInfo> All = Build();

    private static List<SourceInfo> Build()
    {
        var list = new List<SourceInfo>();
        foreach (var button in Protocol.BladeButtonsExtensions.All)
            list.Add(new SourceInfo(button.ToString(), button.ToShortName(), "按键"));

        foreach (var (prefix, label) in new[] { ("LS", "左摇杆"), ("RS", "右摇杆") })
        {
            foreach (var (suffix, dir) in new[]
                     {
                         ("UP", "上"), ("DOWN", "下"), ("LEFT", "左"), ("RIGHT", "右"),
                     })
            {
                list.Add(new SourceInfo($"{prefix}_{suffix}", $"{label}{dir}", "摇杆方向"));
            }
        }
        return list;
    }

    public static string GetDisplayName(string id)
    {
        var found = All.FirstOrDefault(s => s.Id == id);
        if (found != null) return found.DisplayName;
        if (id.Length == 1) return id;
        return id;
    }
}
