using System.Text.Json;
using System.Text.Json.Serialization;
using LeadjoyBlade.Core.Mapping;

namespace LeadjoyBlade.Core.Config;

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public string? LastProfile { get; set; }
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoSwitchByProcess { get; set; } = true;
    public string Language { get; set; } = "zh-CN";
    public bool ShowNotifications { get; set; } = true;
    public bool LightTheme { get; set; }
    public bool StartWithMappingEnabled { get; set; }
    public bool KeepProfileWhenInactive { get; set; }
}

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public static string DataDirectory { get; } = ResolveDataDirectory();
    public static string ProfileDirectory => Path.Combine(DataDirectory, "profiles");
    private static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public AppSettings Settings { get; private set; } = new();
    public List<MappingProfile> Profiles { get; private set; } = [];
    public FantasyLibrary FantasyLibrary { get; private set; } = FantasyLibrary.CreateDefault();
    public MacroLibrary MacroLibrary { get; private set; } = new();

    public event EventHandler? Changed;

    public ProfileStore()
    {
        Directory.CreateDirectory(ProfileDirectory);
        Load();
    }

    private static string ResolveDataDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeadjoyBlade");

        try
        {
            Directory.CreateDirectory(baseDir);
            var probe = Path.Combine(baseDir, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            MigrateLegacyData(legacy, baseDir);
            return baseDir;
        }
        catch
        {
            try
            {
                Directory.CreateDirectory(legacy);
                return legacy;
            }
            catch
            {
                var fallback = Path.Combine(baseDir, "data");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }
    }

    private static void MigrateLegacyData(string legacy, string target)
    {
        if (!Directory.Exists(legacy)) return;

        var oldSettings = Path.Combine(legacy, "settings.json");
        var newSettings = Path.Combine(target, "settings.json");
        if (File.Exists(oldSettings) && !File.Exists(newSettings))
            File.Copy(oldSettings, newSettings);

        // 旧配置不自动复制：新程序只初始化一个默认配置，其他配置由用户导入或手动创建。
    }

    public void Load()
    {
        Profiles.Clear();
        if (File.Exists(SettingsFile))
        {
            try { Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonOptions) ?? new(); }
            catch { Settings = new(); }
        }

        var fantasyFile = Path.Combine(DataDirectory, "fantasy-library.json");
        FantasyLibrary = FantasyLibrary.CreateDefault();
        if (File.Exists(fantasyFile))
        {
            try
            {
                FantasyLibrary = JsonSerializer.Deserialize<FantasyLibrary>(File.ReadAllText(fantasyFile), JsonOptions)
                                 ?? FantasyLibrary.CreateDefault();
            }
            catch { }
        }
        if (FantasyLibrary.Groups.Count == 0)
            FantasyLibrary.Groups.Add(new FantasyGroup { Name = "默认" });

        var macroFile = Path.Combine(DataDirectory, "macro-library.json");
        MacroLibrary = new MacroLibrary();
        if (File.Exists(macroFile))
        {
            try
            {
                MacroLibrary = JsonSerializer.Deserialize<MacroLibrary>(File.ReadAllText(macroFile), JsonOptions)
                               ?? new MacroLibrary();
            }
            catch { }
        }

        foreach (var file in Directory.EnumerateFiles(ProfileDirectory, "*.json"))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<MappingProfile>(File.ReadAllText(file), JsonOptions);
                if (profile != null)
                {
                    profile.Ime ??= new ImeProfileSettings();
                    Profiles.Add(profile);
                }
            }
            catch { }
        }

        if (Profiles.Count == 0)
        {
            Profiles.Add(MappingProfile.CreateDefault());
            Save(Profiles[0]);
        }

        MacroLibraryMigration.Migrate(MacroLibrary, Profiles, FantasyLibrary);
        foreach (var profile in Profiles.ToArray()) SystemMenuDefaults.Normalize(profile.Fantasy);
        SaveMacroLibrary();
        foreach (var profile in Profiles.ToArray()) Save(profile);

        var defaultProfile = Profiles.FirstOrDefault(p => p.IsDefault)
                             ?? Profiles.FirstOrDefault(p => p.Name == "默认配置")
                             ?? Profiles[0];
        foreach (var profile in Profiles) profile.IsDefault = ReferenceEquals(profile, defaultProfile);

        if (Settings.LastProfile == null || Profiles.All(p => p.Name != Settings.LastProfile))
            Settings.LastProfile = Profiles[0].Name;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public MappingProfile? Get(string? name) =>
        name == null ? Profiles.FirstOrDefault() : Profiles.FirstOrDefault(p => p.Name == name);

    public void Save(MappingProfile profile)
    {
        SystemMenuDefaults.Normalize(profile.Fantasy);
        Directory.CreateDirectory(ProfileDirectory);
        var index = Profiles.FindIndex(p => p.Name == profile.Name);
        if (index >= 0) Profiles[index] = profile;
        else Profiles.Add(profile);

        if (profile.IsDefault)
            foreach (var other in Profiles.Where(p => !ReferenceEquals(p, profile))) other.IsDefault = false;
        else if (!Profiles.Any(p => p.IsDefault))
            profile.IsDefault = true;

        var path = Path.Combine(ProfileDirectory, Sanitize(profile.Name) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
        SaveSettings();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(string name, bool createDefault = true)
    {
        var wasDefault = Profiles.Any(p => p.Name == name && p.IsDefault);
        Profiles.RemoveAll(p => p.Name == name);
        var path = Path.Combine(ProfileDirectory, Sanitize(name) + ".json");
        try { File.Delete(path); } catch { }

        if (wasDefault && createDefault)
        {
            var replacement = MappingProfile.CreateDefault();
            Profiles.Add(replacement);
            Save(replacement);
            Settings.LastProfile = replacement.Name;
        }
        else if (Settings.LastProfile == name)
        {
            Settings.LastProfile = Profiles.FirstOrDefault()?.Name;
        }
        SaveSettings();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveSettings()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Settings, JsonOptions));
    }

    public void SaveFantasyLibrary()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(Path.Combine(DataDirectory, "fantasy-library.json"),
            JsonSerializer.Serialize(FantasyLibrary, JsonOptions));
    }

    public void SaveMacroLibrary()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(Path.Combine(DataDirectory, "macro-library.json"),
            JsonSerializer.Serialize(MacroLibrary, JsonOptions));
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(result) ? "profile" : result;
    }
}
