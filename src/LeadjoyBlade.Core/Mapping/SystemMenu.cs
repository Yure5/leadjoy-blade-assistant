using System.Text.Json.Serialization;
using LeadjoyBlade.Core.Input;

namespace LeadjoyBlade.Core.Mapping;

/// <summary>系统菜单项目的动作类型。</summary>
public enum SystemMenuActionKind
{
    Key,
    Gamepad,
    MouseClick,
    Macro,
    Text,
}

/// <summary>系统菜单独立宏中的步骤类型。</summary>
public enum SystemMenuStepKind
{
    Key,
    Gamepad,
    MouseClick,
    Delay,
    Text,
}

/// <summary>
/// 系统菜单项目的动作。键盘、手柄、鼠标点击与宏都存放在这里，
/// 不引用全局宏库，因此不会出现在工作台宏列表中，也不会被其他映射调用。
/// </summary>
public sealed class SystemMenuAction
{
    public SystemMenuActionKind Kind { get; set; } = SystemMenuActionKind.Key;

    /// <summary>键盘主键虚拟键码（Kind=Key 时使用）。</summary>
    public int Key { get; set; } = 0x20;

    /// <summary>键盘修饰键（Ctrl/Alt/Shift/Win），可与主键组成组合键。</summary>
    public List<int> Modifiers { get; set; } = [];

    /// <summary>手柄按键，允许多个，形成手柄组合键。</summary>
    public List<string> GamepadButtons { get; set; } = [];

    /// <summary>鼠标点击位置（0~1）：窗口模式下相对游戏窗口客户区，屏幕模式下相对 MouseMonitor 的显示器。</summary>
    public double MouseX { get; set; } = 0.5;
    public double MouseY { get; set; } = 0.5;
    public MouseButtonKind MouseButton { get; set; } = MouseButtonKind.Left;

    /// <summary>true = 相对整块显示器（配置未指定进程时）；false = 相对进程窗口客户区。</summary>
    public bool MouseScreen { get; set; }

    /// <summary>屏幕模式记录的显示器设备名（如 \\.\DISPLAY1），用于多屏时点回同一块屏幕。</summary>
    public string? MouseMonitor { get; set; }

    /// <summary>采集时该显示器的物理边界，设备名找不到时作为兜底。</summary>
    public int MouseMonitorX { get; set; }
    public int MouseMonitorY { get; set; }
    public int MouseMonitorWidth { get; set; }
    public int MouseMonitorHeight { get; set; }

    /// <summary>独立宏步骤，仅本系统菜单项目使用。</summary>
    public List<SystemMenuMacroStep> Steps { get; set; } = [];

    /// <summary>Kind=Text 时要输入的文字。</summary>
    public string Text { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsEmpty => Kind switch
    {
        SystemMenuActionKind.Key => Key == 0,
        SystemMenuActionKind.Gamepad => GamepadButtons.Count == 0,
        SystemMenuActionKind.MouseClick => false,
        SystemMenuActionKind.Macro => Steps.Count == 0,
        SystemMenuActionKind.Text => string.IsNullOrEmpty(Text),
        _ => true,
    };

    public string Describe()
    {
        switch (Kind)
        {
            case SystemMenuActionKind.Key:
                return string.IsNullOrEmpty(ModifierText())
                    ? $"键盘 {KeyCatalog.GetName(Key)}"
                    : $"键盘 {ModifierText()}+{KeyCatalog.GetName(Key)}";
            case SystemMenuActionKind.Gamepad:
                return GamepadButtons.Count == 0
                    ? "手柄（未选择按键）"
                    : $"手柄 {string.Join(" + ", GamepadButtons.Select(MappingSources.GetDisplayName))}";
            case SystemMenuActionKind.MouseClick:
                var scope = MouseScreen ? "屏幕" : "窗口";
                return $"鼠标{MouseButtonText(MouseButton)} {MouseX * 100:0.#}% / {MouseY * 100:0.#}%（{scope}）";
            case SystemMenuActionKind.Macro:
                return $"独立宏（{Steps.Count} 步）";
            case SystemMenuActionKind.Text:
                return $"文本「{Text}」";
            default:
                return Kind.ToString();
        }
    }

    private string ModifierText()
    {
        var names = new List<string>();
        if (Modifiers.Contains(KeyCatalog.VkControl)) names.Add("Ctrl");
        if (Modifiers.Contains(KeyCatalog.VkAlt)) names.Add("Alt");
        if (Modifiers.Contains(KeyCatalog.VkShift)) names.Add("Shift");
        if (Modifiers.Contains(KeyCatalog.VkLWin)) names.Add("Win");
        return string.Join("+", names);
    }

    public static string MouseButtonText(MouseButtonKind button) => button switch
    {
        MouseButtonKind.Left => "左键",
        MouseButtonKind.Right => "右键",
        MouseButtonKind.Middle => "中键",
        MouseButtonKind.X1 => "侧键 1",
        MouseButtonKind.X2 => "侧键 2",
        _ => button.ToString(),
    };

    public static SystemMenuAction CreateKey(int key, params int[] modifiers) => new()
    {
        Kind = SystemMenuActionKind.Key,
        Key = key,
        Modifiers = modifiers.ToList(),
    };

    public SystemMenuAction Clone() => new()
    {
        Kind = Kind,
        Key = Key,
        Modifiers = [.. Modifiers],
        GamepadButtons = [.. GamepadButtons],
        MouseX = MouseX,
        MouseY = MouseY,
        MouseButton = MouseButton,
        MouseScreen = MouseScreen,
        MouseMonitor = MouseMonitor,
        MouseMonitorX = MouseMonitorX,
        MouseMonitorY = MouseMonitorY,
        MouseMonitorWidth = MouseMonitorWidth,
        MouseMonitorHeight = MouseMonitorHeight,
        Steps = Steps.Select(s => s.Clone()).ToList(),
        Text = Text,
    };
}

/// <summary>系统菜单独立宏的一步；键盘、手柄、鼠标点击可任意混合。</summary>
public sealed class SystemMenuMacroStep
{
    public SystemMenuStepKind Kind { get; set; } = SystemMenuStepKind.Key;
    public int Key { get; set; } = 0x20;
    public List<int> Modifiers { get; set; } = [];
    /// <summary>旧版单个手柄键；新数据用 <see cref="GamepadButtons"/>，两者兼容。</summary>
    public string GamepadButton { get; set; } = "A";
    /// <summary>手柄组合键：可同时选多个。</summary>
    public List<string> GamepadButtons { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<string> EffectiveGamepadButtons =>
        GamepadButtons.Count > 0
            ? GamepadButtons
            : (string.IsNullOrWhiteSpace(GamepadButton) ? [] : [GamepadButton]);
    public double MouseX { get; set; } = 0.5;
    public double MouseY { get; set; } = 0.5;
    public MouseButtonKind MouseButton { get; set; } = MouseButtonKind.Left;

    /// <summary>true = 相对整块显示器（未配置进程时），false = 相对进程窗口客户区。</summary>
    public bool MouseScreen { get; set; }
    public string? MouseMonitor { get; set; }
    public int MouseMonitorX { get; set; }
    public int MouseMonitorY { get; set; }
    public int MouseMonitorWidth { get; set; }
    public int MouseMonitorHeight { get; set; }

    public int HoldMs { get; set; } = 60;
    public int DelayMs { get; set; } = 80;

    /// <summary>Kind=Text 时要输入的文字。</summary>
    public string Text { get; set; } = string.Empty;

    public string Describe()
    {
        return Kind switch
        {
            SystemMenuStepKind.Key =>
                $"键盘 {ModifierText()}{KeyCatalog.GetName(Key)}（按住 {HoldMs}ms，间隔 {DelayMs}ms）",
            SystemMenuStepKind.Gamepad =>
                $"手柄 {string.Join(" + ", EffectiveGamepadButtons.Select(MappingSources.GetDisplayName))}（按住 {HoldMs}ms，间隔 {DelayMs}ms）",
            SystemMenuStepKind.MouseClick =>
                $"鼠标{SystemMenuAction.MouseButtonText(MouseButton)} {MouseX * 100:0.#}% / {MouseY * 100:0.#}%（{(MouseScreen ? "屏幕" : "窗口")}，间隔 {DelayMs}ms）",
            SystemMenuStepKind.Delay => $"延迟 {DelayMs}ms",
            SystemMenuStepKind.Text => $"文本「{Text}」（间隔 {DelayMs}ms）",
            _ => "步骤",
        };
    }

    private string ModifierText()
    {
        var names = new List<string>();
        if (Modifiers.Contains(KeyCatalog.VkControl)) names.Add("Ctrl+");
        if (Modifiers.Contains(KeyCatalog.VkAlt)) names.Add("Alt+");
        if (Modifiers.Contains(KeyCatalog.VkShift)) names.Add("Shift+");
        if (Modifiers.Contains(KeyCatalog.VkLWin)) names.Add("Win+");
        return string.Concat(names);
    }

    public SystemMenuMacroStep Clone() => new()
    {
        Kind = Kind,
        Key = Key,
        Modifiers = [.. Modifiers],
        GamepadButton = GamepadButton,
        GamepadButtons = [.. GamepadButtons],
        MouseX = MouseX,
        MouseY = MouseY,
        MouseButton = MouseButton,
        MouseScreen = MouseScreen,
        MouseMonitor = MouseMonitor,
        MouseMonitorX = MouseMonitorX,
        MouseMonitorY = MouseMonitorY,
        MouseMonitorWidth = MouseMonitorWidth,
        MouseMonitorHeight = MouseMonitorHeight,
        HoldMs = HoldMs,
        DelayMs = DelayMs,
        Text = Text,
    };
}

public sealed class SystemMenuItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新项目";
    public string Description { get; set; } = "";
    public SystemMenuAction Action { get; set; } = new();

    [JsonIgnore] public bool IsCut { get; set; }
    [JsonIgnore] public double RowOpacity => IsCut ? 0.45 : 1.0;
    [JsonIgnore] public string CutLabel => IsCut ? "已剪切" : "";

    [JsonIgnore]
    public string ActionText => Action.Describe();

    public SystemMenuItem Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        Action = Action.Clone(),
    };
}

public sealed class SystemMenuCategory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新菜单栏";
    public List<SystemMenuItem> Items { get; set; } = [];

    [JsonIgnore] public bool IsCut { get; set; }
    [JsonIgnore] public double RowOpacity => IsCut ? 0.45 : 1.0;
    [JsonIgnore] public string CutLabel => IsCut ? "已剪切" : "";

    [JsonIgnore]
    public string CountText => $"{Items.Count} 项";

    public SystemMenuCategory Clone() => new()
    {
        Id = Id,
        Name = Name,
        Items = Items.Select(i => i.Clone()).ToList(),
    };
}

public static class SystemMenuDefaults
{
    /// <summary>用于新建配置的示例菜单；用户可直接编辑或删除。</summary>
    public static List<SystemMenuCategory> CreateCategories() =>
    [
        new SystemMenuCategory
        {
            Name = "常用",
            Items =
            [
                new SystemMenuItem
                {
                    Name = "角色",
                    Description = "打开角色信息",
                    Action = SystemMenuAction.CreateKey(0x43),
                },
                new SystemMenuItem
                {
                    Name = "背包",
                    Description = "打开物品背包",
                    Action = SystemMenuAction.CreateKey(0x49),
                },
                new SystemMenuItem
                {
                    Name = "地图",
                    Description = "打开地图",
                    Action = SystemMenuAction.CreateKey(0x4D),
                },
                new SystemMenuItem
                {
                    Name = "任务",
                    Description = "打开任务列表",
                    Action = SystemMenuAction.CreateKey(0x4A),
                },
            ],
        },
        new SystemMenuCategory
        {
            Name = "系统",
            Items =
            [
                new SystemMenuItem
                {
                    Name = "系统菜单",
                    Description = "打开游戏自带系统菜单",
                    Action = SystemMenuAction.CreateKey(0x1B),
                },
                new SystemMenuItem
                {
                    Name = "截图",
                    Description = "截取当前画面",
                    Action = SystemMenuAction.CreateKey(0x2C),
                },
            ],
        },
    ];

    public static readonly string[] FaceButtons = ["A", "B", "X", "Y"];

    public static bool IsFaceButton(string? id) =>
        id != null && FaceButtons.Contains(id, StringComparer.Ordinal);

    public static void Normalize(FantasyProfile fantasy)
    {
        fantasy.SystemMenuCategories ??= CreateCategories();
        if (!IsFaceButton(fantasy.SystemMenuConfirmButton)) fantasy.SystemMenuConfirmButton = "A";
        if (!IsFaceButton(fantasy.SystemMenuCancelButton)
            || fantasy.SystemMenuCancelButton == fantasy.SystemMenuConfirmButton)
            fantasy.SystemMenuCancelButton = FirstOther(fantasy.SystemMenuConfirmButton);
        if (fantasy.SystemMenuHotkey != "Start") fantasy.SystemMenuHotkey = "Select";

        foreach (var category in fantasy.SystemMenuCategories)
        {
            category.Items ??= [];
            if (string.IsNullOrWhiteSpace(category.Id)) category.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(category.Name)) category.Name = "菜单栏";
            foreach (var item in category.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(item.Name)) item.Name = "项目";
                item.Action ??= new SystemMenuAction();
                item.Action.Modifiers ??= [];
                item.Action.GamepadButtons ??= [];
                item.Action.Steps ??= [];
                item.Action.Text ??= "";
                item.Action.MouseX = Math.Clamp(item.Action.MouseX, 0, 1);
                item.Action.MouseY = Math.Clamp(item.Action.MouseY, 0, 1);
                foreach (var step in item.Action.Steps)
                {
                    step.Modifiers ??= [];
                    step.GamepadButtons ??= [];
                    step.Text ??= "";
                    step.MouseX = Math.Clamp(step.MouseX, 0, 1);
                    step.MouseY = Math.Clamp(step.MouseY, 0, 1);
                }
            }
        }
    }

    public static string FirstOther(string button)
    {
        foreach (var candidate in FaceButtons)
            if (!string.Equals(candidate, button, StringComparison.Ordinal))
                return candidate;
        return "B";
    }
}


