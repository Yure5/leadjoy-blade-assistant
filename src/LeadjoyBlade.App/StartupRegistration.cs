using Microsoft.Win32;

namespace LeadjoyBlade.App;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LeadjoyBlade";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (enabled)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --startup");
            else
                key.DeleteValue(ValueName, false);
        }
        catch { }
    }

    /// <summary>把旧的启动项补上 --startup，避免开机启动时弹出主窗口。</summary>
    public static void EnsureStartupArgument()
    {
        if (IsEnabled()) SetEnabled(true);
    }
}
