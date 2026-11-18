namespace LeadjoyBlade.App;

/// <summary>关于信息编译进程序集，避免通过外部文件被随意修改。</summary>
public static class AboutInfo
{
    public const string Version = "V0.8.80";

    public const string Description =
        "专为动感指尖 / Leadjoy 风刃 Plus 开发。\n" +
        "通过厂商 BLE / USB 通道读取风刃 Plus 的 23 个实际按键，并使用 SendInput 注入键盘与鼠标。\n" +
        "部分采用 Raw Input 独占或反作弊的游戏可能忽略软件注入，可尝试以管理员身份运行本程序。\n" +
        "「手柄输入法」融合自莫古雨的开源项目 柄输 Binput 2.5.0（GPL-3.0），感谢原作者。\n" +
        "本程序由 Yure5（幻色）独立完成，完全免费使用，无任何广告，谨防收费诈骗。";
}









































