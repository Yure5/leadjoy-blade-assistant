namespace LeadjoyBlade.Core.Input;

public static class KeyCatalog
{
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkAlt = 0x12;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;
    public const int VkLShift = 0xA0;
    public const int VkRShift = 0xA1;
    public const int VkLControl = 0xA2;
    public const int VkRControl = 0xA3;
    public const int VkLAlt = 0xA4;
    public const int VkRAlt = 0xA5;

    private static readonly Dictionary<int, string> Names = new()
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pause",
        [0x14] = "CapsLock", [0x1B] = "Esc", [0x20] = "Space", [0x21] = "PageUp",
        [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home", [0x25] = "←",
        [0x26] = "↑", [0x27] = "→", [0x28] = "↓", [0x2C] = "PrintScreen",
        [0x2D] = "Insert", [0x2E] = "Delete", [0x5B] = "左Win", [0x5C] = "右Win",
        [0x5D] = "Menu", [0x90] = "NumLock", [0x91] = "ScrollLock",
        [0xA0] = "左Shift", [0xA1] = "右Shift", [0xA2] = "左Ctrl", [0xA3] = "右Ctrl",
        [0xA4] = "左Alt", [0xA5] = "右Alt",
        [0xAD] = "静音", [0xAE] = "音量-", [0xAF] = "音量+",
        [0xB0] = "下一曲", [0xB1] = "上一曲", [0xB2] = "停止", [0xB3] = "播放/暂停",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".",
        [0xBF] = "/", [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]",
        [0xDE] = "'",
    };

    static KeyCatalog()
    {
        for (var i = 0; i < 26; i++) Names[0x41 + i] = ((char)('A' + i)).ToString();
        for (var i = 0; i < 10; i++) Names[0x30 + i] = i.ToString();
        for (var i = 0; i < 10; i++) Names[0x60 + i] = $"小键盘{i}";
        Names[0x6A] = "小键盘*"; Names[0x6B] = "小键盘+"; Names[0x6D] = "小键盘-";
        Names[0x6E] = "小键盘."; Names[0x6F] = "小键盘/";
        for (var i = 0; i < 24; i++) Names[0x70 + i] = $"F{i + 1}";
        Names[0x10] = "Shift"; Names[0x11] = "Ctrl"; Names[0x12] = "Alt";
    }

    public static string GetName(int vk) => Names.TryGetValue(vk, out var name) ? name : $"VK_{vk:X2}";

    public static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
        or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    public static IReadOnlyList<int> AllKeys => Names.Keys.OrderBy(k => k).ToList();
}
