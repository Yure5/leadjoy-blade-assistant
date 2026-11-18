namespace LeadjoyBlade.Core.Mapping;

/// <summary>工作台宏库中的一条宏，与按键映射解耦，可被按键映射和幻想映射共同调用。</summary>
public sealed class MacroDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新宏";
    public List<MacroStep> Steps { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public string StepsText => $"{Steps.Count} 步";
}

public sealed class MacroLibrary
{
    public List<MacroDefinition> Items { get; set; } = [];
}

public static class MacroLibraryMigration
{
    /// <summary>把旧版“宏 Binding”和旧幻想映射中的内联宏迁移为宏库条目，并回填 MacroId。</summary>
    public static void Migrate(MacroLibrary library, IEnumerable<MappingProfile> profiles, FantasyLibrary fantasy)
    {
        foreach (var profile in profiles)
        {
            foreach (var binding in profile.Bindings)
            {
                if (binding.Action.Type != ActionType.Macro) continue;
                var macro = Ensure(library, binding.Action, binding.Name);
                binding.Action.MacroId = macro.Id;
                binding.Action.MacroName = macro.Name;
            }
        }

        foreach (var item in fantasy.Items)
        {
            if (item.Action.Type != ActionType.Macro) continue;
            var macro = Ensure(library, item.Action, item.Name);
            item.Action.MacroId = macro.Id;
            item.Action.MacroName = macro.Name;
        }
    }

    private static MacroDefinition Ensure(MacroLibrary library, BindingAction action, string fallbackName)
    {
        var existing = library.Items.FirstOrDefault(x => x.Id == action.MacroId)
                       ?? library.Items.FirstOrDefault(x => !string.IsNullOrWhiteSpace(action.MacroName)
                           && string.Equals(x.Name, action.MacroName, StringComparison.Ordinal))
                       ?? library.Items.FirstOrDefault(x => !string.IsNullOrWhiteSpace(fallbackName)
                           && string.Equals(x.Name, fallbackName, StringComparison.Ordinal));
        if (existing != null) return existing;

        var name = string.IsNullOrWhiteSpace(action.MacroName) ? fallbackName : action.MacroName;
        var macro = new MacroDefinition
        {
            Name = string.IsNullOrWhiteSpace(name) ? "宏" : name,
            Steps = action.Macro.Select(CloneStep).ToList(),
        };
        library.Items.Add(macro);
        return macro;
    }

    public static MacroStep CloneStep(MacroStep step) => new()
    {
        Kind = step.Kind,
        Key = step.Key,
        Modifiers = [.. step.Modifiers],
        HoldMs = step.HoldMs,
        DelayMs = step.DelayMs,
        MouseButton = step.MouseButton,
        GamepadSource = step.GamepadSource,
        Text = step.Text,
        MouseUsePosition = step.MouseUsePosition,
        MouseX = step.MouseX,
        MouseY = step.MouseY,
        MouseScreen = step.MouseScreen,
        MouseMonitor = step.MouseMonitor,
        MouseMonitorX = step.MouseMonitorX,
        MouseMonitorY = step.MouseMonitorY,
        MouseMonitorWidth = step.MouseMonitorWidth,
        MouseMonitorHeight = step.MouseMonitorHeight,
    };
}
