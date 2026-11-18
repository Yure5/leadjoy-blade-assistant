namespace LeadjoyBlade.Core.Mapping;

/// <summary>
/// 手柄输入法唤醒键与当前配置的冲突检查。
/// 只做提示，不阻止保存：用户可能就是想临时用同一个键。
/// </summary>
public static class ImeWakeConflicts
{
    /// <summary>唤醒键占用的幻想模式基础键 ID（用于置灰）。</summary>
    public static HashSet<string> WakeBaseKeys(string? wakeSpec)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(wakeSpec))
        {
            // 默认 L3 + R3
            set.Add("L3");
            set.Add("R3");
            return set;
        }

        if (!wakeSpec.StartsWith("pad:", StringComparison.OrdinalIgnoreCase)) return set;
        foreach (var token in wakeSpec[4..].Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token.Trim().ToLowerInvariant())
            {
                case "back":
                case "select":
                    set.Add("Select");
                    break;
                case "start":
                    set.Add("Start");
                    break;
                case "l3":
                    set.Add("L3");
                    break;
                case "r3":
                    set.Add("R3");
                    break;
            }
        }
        return set;
    }

    /// <summary>返回人类可读的冲突说明；没有冲突返回空列表。</summary>
    public static List<string> Find(MappingProfile profile)
    {
        var result = new List<string>();
        var ime = profile.Ime;
        if (ime == null) return result;

        var padKeys = WakeBaseKeys(ime.WakeSpec);
        // 幻想模式启用时，按键映射/组合键整体不可用，不再提示它们的冲突。
        // 映射总开关关闭时同理。
        var mappingActive = profile.Settings.Enabled && !profile.Fantasy.Enabled;
        if (padKeys.Count > 0)
        {
            if (mappingActive)
            {
                foreach (var binding in profile.Bindings.Where(b => b.Enabled))
                {
                    if (!padKeys.Contains(binding.Source)) continue;
                    result.Add($"按键映射「{binding.Name}」使用 {MappingSources.GetDisplayName(binding.Source)}");
                }

                foreach (var chord in profile.Chords.Where(c => c.Enabled))
                {
                    var hit = chord.Sources.FirstOrDefault(padKeys.Contains);
                    if (hit == null) continue;
                    result.Add($"组合键「{chord.Name}」包含 {MappingSources.GetDisplayName(hit)}");
                }
            }

            foreach (var (key, action) in profile.Fantasy.BaseActions)
            {
                if (action.Type == ActionType.Disabled) continue;
                if (!padKeys.Contains(key)) continue;
                result.Add($"幻想模式基础键 {key}");
            }

            if (profile.Fantasy.SystemMenuEnabled)
            {
                var hotkey = profile.Fantasy.SystemMenuHotkey == "Start" ? "Start" : "Select";
                if (padKeys.Contains(hotkey))
                    result.Add($"系统菜单呼出键 {hotkey.ToUpperInvariant()}");
            }
        }

        var vk = WakeVirtualKey(ime.WakeSpec);
        if (vk != 0)
        {
            var keyName = WakeKeyName(ime.WakeSpec);
            if (mappingActive)
            {
                foreach (var binding in profile.Bindings.Where(b => b.Enabled))
                {
                    if (binding.Action.Type != ActionType.Key || binding.Action.Key != vk) continue;
                    result.Add($"按键映射「{binding.Name}」输出 {keyName}");
                }
            }

            foreach (var (key, action) in profile.Fantasy.BaseActions)
            {
                if (action.Type != ActionType.Key || action.Key != vk) continue;
                result.Add($"幻想模式基础键 {key} 输出 {keyName}");
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    public static int WakeVirtualKey(string? wakeSpec)
    {
        if (string.IsNullOrWhiteSpace(wakeSpec) ||
            !wakeSpec.StartsWith("kb:f", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (!int.TryParse(wakeSpec.AsSpan(4), out var n) || n < 1 || n > 12) return 0;
        return 0x70 + n - 1; // VK_F1 .. VK_F12
    }

    public static string WakeKeyName(string? wakeSpec)
        => WakeVirtualKey(wakeSpec) is var vk && vk != 0 ? $"F{vk - 0x6F}" : "唤醒键";
}
