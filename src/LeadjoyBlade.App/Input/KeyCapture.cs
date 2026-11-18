using System.Windows.Input;

namespace LeadjoyBlade.App.Input;

public static class KeyCapture
{
    /// <summary>取 IME/系统键处理前的真实按键，避免中文输入法下得到 VK_PROCESSKEY(0xE5)。</summary>
    public static Key Resolve(KeyEventArgs e) => e.Key switch
    {
        Key.System when e.SystemKey != Key.None => e.SystemKey,
        Key.ImeProcessed when e.ImeProcessedKey != Key.None => e.ImeProcessedKey,
        Key.DeadCharProcessed when e.DeadCharProcessedKey != Key.None => e.DeadCharProcessedKey,
        _ => e.Key,
    };
}
