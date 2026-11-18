using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using LeadjoyBlade.App.Dialogs;
using LeadjoyBlade.Core.Mapping;

namespace LeadjoyBlade.App.Views;

public partial class SettingsPage : UserControl
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        AboutTitle.Text = $"风刃手柄助手 {AboutInfo.Version}";
        AboutBody.Text = AboutInfo.Description;
        Loaded += (_, _) => LoadValues();
        IsVisibleChanged += (_, _) => { if (IsVisible) SyncImeCheck(); };
    }

    /// <summary>在其他页面切换了输入法开关后，回到配置管理时同步勾选状态。</summary>
    public void SyncImeCheck()
    {
        if (ImeCheck == null) return;
        var was = _loading;
        _loading = true;
        ImeCheck.IsChecked = App.Current.Engine.Profile.Ime.Enabled;
        _loading = was;
    }

    private void LoadValues()
    {
        _loading = true;
        var profile = App.Current.Engine.Profile;
        var settings = profile.Settings;
        ProfileNameBox.Text = profile.Name;
        ProcessBox.Text = profile.ProcessName ?? "";
        if (ProfileSelectCombo != null)
        {
            ProfileSelectCombo.Items.Clear();
            foreach (var item in App.Current.Store.Profiles)
                ProfileSelectCombo.Items.Add(item.Name);
            ProfileSelectCombo.SelectedItem = profile.Name;
        }
        LeftDeadzoneSlider.Value = settings.LeftStickDeadzone * 100;
        RightDeadzoneSlider.Value = settings.RightStickDeadzone * 100;
        InvertLeftCheck.IsChecked = settings.InvertLeftY;
        InvertRightCheck.IsChecked = settings.InvertRightY;
        LeftTriggerFastCheck.IsChecked = settings.LeftTriggerAsButton;
        RightTriggerFastCheck.IsChecked = settings.RightTriggerAsButton;
        ImeCheck.IsChecked = profile.Ime.Enabled;

        var app = App.Current.Store.Settings;
        StartWithWindowsCheck.IsChecked = IsStartupEnabled();
        StartMappingCheck.IsChecked = app.StartWithMappingEnabled;
        MinimizeToTrayCheck.IsChecked = app.MinimizeToTray;
        AutoSwitchCheck.IsChecked = app.AutoSwitchByProcess;
        KeepProfileCheck.IsChecked = app.KeepProfileWhenInactive;
        UpdateLabels();
        _loading = false;
    }

    private void UpdateLabels()
    {
        if (LeftDeadzoneText == null || RightDeadzoneText == null) return;
        LeftDeadzoneText.Text = $"{LeftDeadzoneSlider.Value:0}%";
        RightDeadzoneText.Text = $"{RightDeadzoneSlider.Value:0}%";
    }

    private void Deadzone_OnChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || LeftDeadzoneText == null) return;
        var settings = App.Current.Engine.Profile.Settings;
        settings.LeftStickDeadzone = LeftDeadzoneSlider.Value / 100.0;
        settings.RightStickDeadzone = RightDeadzoneSlider.Value / 100.0;
        UpdateLabels();
        Save();
    }

    private void ImeCheck_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Current.SetImeEnabled(ImeCheck.IsChecked == true);
    }

    private void Setting_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var settings = App.Current.Engine.Profile.Settings;
        settings.InvertLeftY = InvertLeftCheck.IsChecked == true;
        settings.InvertRightY = InvertRightCheck.IsChecked == true;
        settings.LeftTriggerAsButton = LeftTriggerFastCheck.IsChecked == true;
        settings.RightTriggerAsButton = RightTriggerFastCheck.IsChecked == true;

        var app = App.Current.Store.Settings;
        app.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
        app.AutoSwitchByProcess = AutoSwitchCheck.IsChecked == true;
        app.KeepProfileWhenInactive = KeepProfileCheck.IsChecked == true;
        app.StartWithMappingEnabled = StartMappingCheck.IsChecked == true;
        var startWithWindows = StartWithWindowsCheck.IsChecked == true;
        app.StartWithWindows = startWithWindows;
        if (startWithWindows != IsStartupEnabled()) SetStartup(startWithWindows);
        App.Current.Store.SaveSettings();
        Save();
    }

    private void Save()
    {
        App.Current.Store.Save(App.Current.Engine.Profile);
    }

    private void SaveProfile_OnClick(object sender, RoutedEventArgs e)
    {
        var profile = App.Current.Engine.Profile;
        var newName = ProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            MessageBox.Show("配置名称不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (newName != profile.Name)
        {
            if (App.Current.Store.Profiles.Any(p => p.Name == newName))
            {
                MessageBox.Show("已存在同名配置。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var oldName = profile.Name;
            profile.Name = newName;
            App.Current.Store.Delete(oldName, createDefault: false);
        }

        profile.ProcessName = string.IsNullOrWhiteSpace(ProcessBox.Text) ? null : ProcessBox.Text.Trim();
        profile.Settings.Enabled = App.Current.Engine.Enabled;
        App.Current.Store.Save(profile);
        App.Current.Store.Settings.LastProfile = profile.Name;
        App.Current.Store.SaveSettings();
        (Window.GetWindow(this) as MainWindow)?.ReloadProfiles();
        MessageBox.Show("配置已保存。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void NewProfile_OnClick(object sender, RoutedEventArgs e)
    {
        var source = App.Current.Engine.Profile;
        var index = 1;
        string name;
        do
        {
            name = $"新配置 {index++}";
        } while (App.Current.Store.Profiles.Any(p => p.Name == name));

        var profile = new MappingProfile
        {
            Name = name,
            Bindings = source.Bindings.Select(CloneBinding).ToList(),
            Chords = source.Chords.Select(CloneChord).ToList(),
            Settings = new ProfileSettings
            {
                Enabled = source.Settings.Enabled,
                LeftStickDeadzone = source.Settings.LeftStickDeadzone,
                RightStickDeadzone = source.Settings.RightStickDeadzone,
                InvertLeftY = source.Settings.InvertLeftY,
                InvertRightY = source.Settings.InvertRightY,
                RightStickAsMouse = source.Settings.RightStickAsMouse,
                LeftStickAsMouse = source.Settings.LeftStickAsMouse,
                MouseSensitivity = source.Settings.MouseSensitivity,
                MouseAcceleration = source.Settings.MouseAcceleration,
                MouseDeadzonePercent = source.Settings.MouseDeadzonePercent,
                TriggerAsButton = source.Settings.TriggerAsButton,
                LeftTriggerAsButton = source.Settings.LeftTriggerAsButton,
                RightTriggerAsButton = source.Settings.RightTriggerAsButton,
            },
            Ime = new ImeProfileSettings
            {
                Configured = true,
                Enabled = source.Ime.Enabled,
                Preview = source.Ime.Preview,
                WakeSpec = source.Ime.WakeSpec,
                Tier = source.Ime.Tier,
            },
        };
        App.Current.Store.Save(profile);
        App.Current.Store.Settings.LastProfile = name;
        App.Current.Store.SaveSettings();
        (Window.GetWindow(this) as MainWindow)?.ReloadProfiles();
        LoadValues();
    }

    public void SyncProfileSelection() => LoadValues();

    private void ProfileSelect_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileSelectCombo.SelectedItem is not string name) return;
        if (name == App.Current.Engine.Profile.Name) return;
        (Window.GetWindow(this) as MainWindow)?.SelectProfile(name);
        LoadValues();
    }

    private void DeleteProfile_OnClick(object sender, RoutedEventArgs e)
    {
        var name = ProfileSelectCombo.SelectedItem as string ?? App.Current.Engine.Profile.Name;
        var profile = App.Current.Store.Get(name);
        if (profile == null) return;
        if (MessageBox.Show($"确定删除配置“{profile.Name}”吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        App.Current.Store.Delete(profile.Name);
        var next = App.Current.Store.Profiles.FirstOrDefault(p => p.IsDefault)
                   ?? App.Current.Store.Profiles.FirstOrDefault();
        if (next != null)
        {
            App.Current.Store.Settings.LastProfile = next.Name;
            App.Current.Store.SaveSettings();
            App.Current.Engine.SetProfile(next);
            App.Current.ApplyImeProfile();
        }
        (Window.GetWindow(this) as MainWindow)?.ReloadProfiles();
        LoadValues();
    }

    private static readonly JsonSerializerOptions ExportJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class ProfileBundle
    {
        public int Version { get; set; } = 1;
        public List<MappingProfile> Profiles { get; set; } = [];
    }

    private void ExportProfiles_OnClick(object sender, RoutedEventArgs e)
    {
        var store = App.Current.Store;
        if (store.Profiles.Count == 0) return;
        var select = new ProfileExportDialog(store.Profiles.Select(p => p.Name)) { Owner = Window.GetWindow(this) };
        if (select.ShowDialog() != true) return;

        var dialog = new SaveFileDialog
        {
            Title = "导出配置",
            Filter = "配置文件 (*.json)|*.json",
            FileName = "Leadjoy配置.json",
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var bundle = new ProfileBundle
            {
                Profiles = store.Profiles.Where(p => select.SelectedProfiles.Contains(p.Name)).ToList(),
            };
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(bundle, ExportJson));
            MessageBox.Show($"已导出 {bundle.Profiles.Count} 个配置。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportProfiles_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入配置",
            Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;

        var store = App.Current.Store;
        var imported = new List<MappingProfile>();
        foreach (var file in dialog.FileNames)
        {
            try
            {
                var text = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("profiles", out _))
                {
                    var bundle = JsonSerializer.Deserialize<ProfileBundle>(text, ExportJson);
                    if (bundle?.Profiles != null) imported.AddRange(bundle.Profiles);
                }
                else
                {
                    var profile = JsonSerializer.Deserialize<MappingProfile>(text, ExportJson);
                    if (profile != null) imported.Add(profile);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取 {Path.GetFileName(file)} 失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        foreach (var profile in imported)
        {
            var name = string.IsNullOrWhiteSpace(profile.Name) ? "导入配置" : profile.Name.Trim();
            if (store.Profiles.Any(p => p.Name == name))
            {
                var baseName = name + " (导入)";
                name = baseName;
                var index = 2;
                while (store.Profiles.Any(p => p.Name == name)) name = $"{baseName} {index++}";
            }
            profile.Name = name;
            profile.IsDefault = false;
            if (profile.Ime == null || !profile.Ime.Configured)
            {
                var current = App.Current.Engine.Profile.Ime;
                profile.Ime = new ImeProfileSettings
                {
                    Configured = true,
                    Enabled = current.Enabled,
                    Preview = current.Preview,
                    WakeSpec = current.WakeSpec,
                    Tier = current.Tier,
                };
            }
            store.Save(profile);
        }

        (Window.GetWindow(this) as MainWindow)?.ReloadProfiles();
        LoadValues();
        if (imported.Count > 0)
            MessageBox.Show($"已导入 {imported.Count} 个配置。", "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenData_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Core.Config.ProfileStore.DataDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "无法打开目录", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsStartupEnabled() => StartupRegistration.IsEnabled();

    private static void SetStartup(bool enabled) => StartupRegistration.SetEnabled(enabled);

    private static Binding CloneBinding(Binding source) => new()
    {
        Name = source.Name,
        Source = source.Source,
        Layer = source.Layer,
        Enabled = source.Enabled,
        Action = new BindingAction
        {
            Type = source.Action.Type,
            Key = source.Action.Key,
            Text = source.Action.Text,
            Modifiers = [.. source.Action.Modifiers],
            MouseButton = source.Action.MouseButton,
            WheelDelta = source.Action.WheelDelta,
            Turbo = source.Action.Turbo,
            Toggle = source.Action.Toggle,
            TurboIntervalMs = source.Action.TurboIntervalMs,
            MacroId = source.Action.MacroId,
            MacroName = source.Action.MacroName,
            MacroSource = source.Action.MacroSource,
            Macro = source.Action.Macro.Select(s => new MacroStep
            {
                Kind = s.Kind,
                Key = s.Key,
                Modifiers = [.. s.Modifiers],
                HoldMs = s.HoldMs,
                DelayMs = s.DelayMs,
                MouseButton = s.MouseButton,
                GamepadSource = s.GamepadSource,
            }).ToList(),
        },
    };

    private static ChordBinding CloneChord(ChordBinding source) => new()
    {
        Name = source.Name,
        Sources = [.. source.Sources],
        Enabled = source.Enabled,
        Action = new BindingAction
        {
            Type = source.Action.Type,
            Key = source.Action.Key,
            Text = source.Action.Text,
            Modifiers = [.. source.Action.Modifiers],
            MouseButton = source.Action.MouseButton,
            WheelDelta = source.Action.WheelDelta,
            Turbo = source.Action.Turbo,
            Toggle = source.Action.Toggle,
            TurboIntervalMs = source.Action.TurboIntervalMs,
            MacroId = source.Action.MacroId,
            MacroName = source.Action.MacroName,
            MacroSource = source.Action.MacroSource,
            Macro = source.Action.Macro.Select(s => new MacroStep
            {
                Kind = s.Kind,
                Key = s.Key,
                Modifiers = [.. s.Modifiers],
                HoldMs = s.HoldMs,
                DelayMs = s.DelayMs,
                MouseButton = s.MouseButton,
                GamepadSource = s.GamepadSource,
            }).ToList(),
        },
    };

    private void Bilibili_OnClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://space.bilibili.com/549549") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开浏览器：" + ex.Message, "B 站主页", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
