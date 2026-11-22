using LeadjoyBlade.Core.Input;
using System.Text.Json.Serialization;

namespace LeadjoyBlade.Core.Mapping;

/// <summary>幻想模式全局共享库：标签组 + 可拖拽映射项（含宏）。</summary>
public sealed class FantasyLibrary
{
    public List<FantasyGroup> Groups { get; set; } = [];
    public List<FantasyItem> Items { get; set; } = [];

    public static FantasyLibrary CreateDefault()
    {
        var group = new FantasyGroup { Id = Guid.NewGuid().ToString("N"), Name = "默认" };
        return new FantasyLibrary { Groups = [group] };
    }

    public FantasyGroup EnsureGroup(string? id, string fallbackName = "默认")
    {
        var group = Groups.FirstOrDefault(g => g.Id == id);
        if (group != null) return group;
        group = new FantasyGroup { Id = Guid.NewGuid().ToString("N"), Name = fallbackName };
        Groups.Add(group);
        return group;
    }
}

public sealed class FantasyGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新标签组";
}

public sealed class FantasyItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GroupId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ImageBase64 { get; set; }
    public string? IconText { get; set; }
    public string? IconColor { get; set; }
    public BindingAction Action { get; set; } = new();
    public bool IsMacro => Action.Type == ActionType.Macro;

    [JsonIgnore] public bool IsCut { get; set; }
    [JsonIgnore] public double RowOpacity => IsCut ? 0.45 : 1.0;
    [JsonIgnore] public string CutLabel => IsCut ? "已剪切" : "";

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Action.Describe() : Name;
    public string ActionText => Action.Describe();
}

public sealed class FantasyProfile
{
    public bool Enabled { get; set; }
    public string LeftStickMode { get; set; } = "arrows";
    public List<FantasySet> Sets { get; set; } = CreateSets();
    public List<bool> VisibleSets { get; set; } = [true, true, true, true, true, true, true, true];
    public bool ExtensionEnabled { get; set; }
    public int LeftExtensionSet { get; set; } = 7;
    public int RightExtensionSet { get; set; } = 7;
    public List<double> SensitivityLevels { get; set; } = [0.5, 1.0, 1.5, 2.2];
    public int SensitivityIndex { get; set; } = 1;

    /// <summary>灵敏度切换键："Capture"（默认）或 "C"；选中的键从基础键位中移除。</summary>
    public string SensitivitySwitchKey { get; set; } = "Capture";
    public FantasyOverlaySettings Overlay { get; set; } = new();
    public Dictionary<string, BindingAction> BaseActions { get; set; } = CreateBaseActions();
    public Dictionary<string, string> BaseKeyLabels { get; set; } = [];
    public Dictionary<string, string> BaseKeyDescriptions { get; set; } = [];

    /// <summary>是否在幻想模式内启用系统菜单。</summary>
    public bool SystemMenuEnabled { get; set; }

    /// <summary>呼出热键，Select（默认）或 Start。</summary>
    public string SystemMenuHotkey { get; set; } = "Select";

    /// <summary>菜单打开时的确认键，A/B/X/Y 之一。</summary>
    public string SystemMenuConfirmButton { get; set; } = "A";

    /// <summary>菜单打开时的取消键，A/B/X/Y 之一，且不能与确认键相同。</summary>
    public string SystemMenuCancelButton { get; set; } = "B";

    /// <summary>系统菜单的菜单栏与项目，保存在当前配置中。</summary>
    public List<SystemMenuCategory> SystemMenuCategories { get; set; } = SystemMenuDefaults.CreateCategories();

    /// <summary>是否启用双十字热键栏切换音效。</summary>
    public bool HotbarSoundEnabled { get; set; } = true;

    /// <summary>双十字热键栏切换音效音量（0–100）。</summary>
    public int HotbarSoundVolume { get; set; } = 100;

    /// <summary>是否启用系统菜单呼出音效。</summary>
    public bool MenuSoundEnabled { get; set; } = true;

    /// <summary>系统菜单呼出音效音量（0–100）。</summary>
    public int MenuSoundVolume { get; set; } = 100;

    public static List<FantasySet> CreateSets() =>
        Enumerable.Range(0, 8).Select(i => new FantasySet { Name = $"热键栏 {i + 1}" }).ToList();

    public static Dictionary<string, BindingAction> CreateBaseActions() => new()
    {
        ["A"] = Key(0x0D), ["B"] = Key(0x1B), ["X"] = Key(0x59), ["Y"] = Key(0x12),
        ["Up"] = Key(0x26), ["Down"] = Key(0x28), ["Left"] = Key(0x25), ["Right"] = Key(0x27),
        ["LB"] = Key(0x5A),
        ["L4"] = new BindingAction { Type = ActionType.MouseButton, MouseButton = MouseButtonKind.Left },
        ["R4"] = new BindingAction { Type = ActionType.MouseButton, MouseButton = MouseButtonKind.Right },
        ["Select"] = Key(0xC0), ["Start"] = Key(0xBD), ["Capture"] = Key(0x09),
        ["C"] = new BindingAction { Type = ActionType.Disabled },
        ["L3"] = new BindingAction { Type = ActionType.Disabled },
        ["Fn"] = new BindingAction { Type = ActionType.Disabled },
    };

    private static BindingAction Key(int vk) => new() { Type = ActionType.Key, Key = vk };
}

public sealed class FantasySet
{
    public string Name { get; set; } = "新配置";
    /// <summary>16 个格子，顺序：LT 方向 4、LT ABXY 4、RT 方向 4、RT ABXY 4。</summary>
    public List<string?> Slots { get; set; } = Enumerable.Repeat<string?>(null, 16).ToList();
}

public sealed class FantasyOverlaySettings
{
    public bool HasPosition { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 1.0;
    public double Opacity { get; set; } = 0.9;
    public bool Visible { get; set; }
    public bool ClickThrough { get; set; } = true;
    public bool Locked { get; set; }
}

public static class FantasySlots
{
    public static readonly string[] Ids =
    [
        "LT_UP", "LT_DOWN", "LT_LEFT", "LT_RIGHT", "LT_Y", "LT_X", "LT_B", "LT_A",
        "RT_UP", "RT_DOWN", "RT_LEFT", "RT_RIGHT", "RT_Y", "RT_X", "RT_B", "RT_A",
    ];

    public static readonly string[] Labels =
    [
        "LT+↑", "LT+↓", "LT+←", "LT+→", "LT+Y", "LT+X", "LT+B", "LT+A",
        "RT+↑", "RT+↓", "RT+←", "RT+→", "RT+Y", "RT+X", "RT+B", "RT+A",
    ];
}



