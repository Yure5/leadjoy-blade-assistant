using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LeadjoyBlade.Binput;
using LeadjoyBlade.Core.Config;
using LeadjoyBlade.Core.Devices;
using LeadjoyBlade.Core.Input;
using LeadjoyBlade.Core.Mapping;
using LeadjoyBlade.Core.Protocol;
using BinputConfig = LeadjoyBlade.Binput.Config;

namespace LeadjoyBlade.App;

public partial class App : Application
{
    private const int WmShowMainWindow = 0x8001;
    private Mutex? _mutex;
    private bool _deviceOnline;
    private DispatcherTimer? _imeDeadzoneTimer;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static new App Current => (App)Application.Current;

    public ProfileStore Store { get; private set; } = null!;
    public BladeDeviceManager Devices { get; private set; } = null!;
    public KeyboardInjector Keyboard { get; private set; } = null!;
    public MouseInjector Mouse { get; private set; } = null!;
    public MappingEngine Engine { get; private set; } = null!;
    public FantasyRuntime Fantasy { get; private set; } = null!;

    /// <summary>手柄输入法（融合的柄输 Binput）。未启用时 Running=false，不占任何资源。</summary>
    public BinputHost Ime { get; private set; } = null!;

    public bool ImeRunning => Ime?.Running == true;

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrash(args.Exception);
            MessageBox.Show(args.Exception.ToString(), "程序发生错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrash(args.ExceptionObject as Exception);

        try
        {
            await StartupCore(e);
        }
        catch (Exception ex)
        {
            WriteCrash(ex);
            MessageBox.Show(ex.ToString(), "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task StartupCore(StartupEventArgs e)
    {
        LogStartup("begin");
        _mutex = new Mutex(true, "LeadjoyBlade.SingleInstance", out var created);
        LogStartup($"mutex created={created}");
        if (!created)
        {
            // 再次双击程序：唤醒已运行实例并显示主窗口
            var hwnd = FindWindow(null, "风刃手柄助手");
            LogStartup($"existing instance hwnd={hwnd}");
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WmShowMainWindow, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        var startMinimized = e.Args.Any(arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));

        base.OnStartup(e);
        LogStartup("base startup done");
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        LogStartup("creating store");
        Store = new ProfileStore();
        try { FantasyIconMigration.Migrate(Store); } catch { }
        ThemeManager.Apply(Store.Settings.LightTheme);
        Keyboard = new KeyboardInjector();
        Mouse = new MouseInjector();
        Engine = new MappingEngine(Keyboard, Mouse, Store.MacroLibrary);
        Fantasy = new FantasyRuntime();
        Ime = new BinputHost();
        // 输入法不能继续用 Binput 写死的死区：每 500ms 把「摇杆与扳机」里的
        // 左右摇杆死区同步过去，配置切换/拖动滑块后也会自动跟上。
        _imeDeadzoneTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _imeDeadzoneTimer.Tick += (_, _) => SyncImeDeadzones();
        _imeDeadzoneTimer.Start();
        LogStartup("store ready");

        var profile = Store.Get(Store.Settings.LastProfile) ?? Store.Profiles.FirstOrDefault() ?? MappingProfile.CreateDefault();
        Engine.SetProfile(profile);
        Engine.Enabled = Store.Settings.StartWithMappingEnabled;
        MigrateImeProfiles();

        Devices = new BladeDeviceManager();
        // DS5 复用 Binput 的 HidPad 解析；其他连接方式完全不动。
        Devices.TransportFactory = candidate =>
            candidate.Kind == BladeTransportKind.DualSenseHid
                ? new DualSenseBinputTransport(candidate)
                : null;
        Devices.ReportReceived += (_, report) =>
        {
            // 断联后的残余报文一律不处理：否则最后一帧的摇杆值会继续驱动映射。
            if (!_deviceOnline) return;
            // 输入法运行时，把风刃解析好的手柄状态喂给柄输 —— BLE 1B / Switch / DS4
            // 这些 Binput 原生认不出的传输，也能直接用来打字。
            if (Ime?.Running == true) Ime.FeedPad(ToPadState(report), "风刃手柄 · 直连");

            // 手柄输入法唤出时，手柄归输入法用：暂停风刃映射/幻想模式，
            // 否则按 A 选键会同时触发一次映射动作。收起后自动恢复。
            if (Ime?.Visible == true) return;

            if (!Engine.Enabled)
            {
                Fantasy.Suspend();
                Engine.Update(report);
                return;
            }
            if (Fantasy.Enabled) Fantasy.Update(report);
            else Engine.Update(report);
        };
        Devices.Log += (_, message) => LogBus.Publish(message);
        Devices.ConnectionChanged += (_, connected) =>
        {
            _deviceOnline = connected;
            if (connected) return;
            // 关机/断联：立即释放摇杆鼠标、按键和输入法直连通道。
            // MappingEngine/FantasyRuntime 内部还有 250ms 无报文兜底。
            Ime?.FeedPadDisconnected();
            try { Engine?.OnDisconnected(); } catch { }
            try { Fantasy?.Suspend(); } catch { }
            LogBus.Publish("手柄断开：已释放摇杆/按键/鼠标");
        };

        var window = new MainWindow();
        MainWindow = window;
        if (startMinimized)
        {
            window.StartHidden();
            LogStartup("window hidden to tray");
        }
        else
        {
            window.Show();
            LogStartup("window shown");
        }

        if (Store.Settings.StartWithWindows) StartupRegistration.EnsureStartupArgument();

        // 上次退出时如果当前配置开着「手柄输入法」，这次照旧自动启用。
        if (profile.Ime.Enabled)
        {
            try
            {
                Ime.Start();
                ApplyImeSettings(profile.Ime);
                SyncImeDeadzones();
                LogStartup("ime started");
            }
            catch (Exception ex)
            {
                LogStartup("ime start failed: " + ex.Message);
                LogBus.Publish("手柄输入法启动失败：" + ex.Message);
            }
        }

        var demoDialog = Environment.GetEnvironmentVariable("LEADJOY_DEMO_DIALOG");
        if (!string.IsNullOrWhiteSpace(demoDialog))
        {
            LogStartup($"demo dialog requested: {demoDialog}");
            _ = window.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    LogStartup("opening dialog");
                    if (demoDialog == "chord")
                    {
                        var chord = new Core.Mapping.ChordBinding
                        {
                            Name = "Fn + M1",
                            Sources = ["Fn", "M1"],
                            Action = new Core.Mapping.BindingAction { Type = Core.Mapping.ActionType.Key, Key = 0x70 },
                        };
                        new Dialogs.ChordEditorDialog(chord) { Owner = window }.ShowDialog();
                    }
                    else
                    {
                        var binding = new Core.Mapping.Binding
                        {
                            Source = "M1",
                            Layer = Core.Mapping.BindingLayer.Base,
                            Action = new Core.Mapping.BindingAction
                            {
                                Type = Core.Mapping.ActionType.Key,
                                Key = 0x70,
                                Modifiers = [Core.Input.KeyCatalog.VkControl],
                                Turbo = true,
                                TurboIntervalMs = 50,
                            },
                        };
                        new Dialogs.BindingEditorDialog(binding) { Owner = window }.ShowDialog();
                    }
                    LogStartup("dialog closed");
                }
                catch (Exception ex)
                {
                    WriteCrash(ex);
                    LogStartup("dialog error: " + ex.Message);
                }
            });
        }

        try
        {
            await Devices.StartAsync();
        }
        catch (Exception ex)
        {
            LogBus.Publish($"初始化设备管理器失败：{ex.Message}");
        }

        if (Environment.GetEnvironmentVariable("LEADJOY_SIM") == "1")
        {
            await Devices.ConnectAsync(new Core.Devices.BladeDeviceCandidate(
                "simulator", "模拟手柄", Core.Devices.BladeTransportKind.Simulator));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _imeDeadzoneTimer?.Stop(); } catch { }
        try { Ime?.Stop(); } catch { }
        try { Engine?.ReleaseAll(); } catch { }
        try { Devices?.DisposeAsync().AsTask().Wait(1500); } catch { }
        try { Store?.SaveSettings(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>把当前配置的左右摇杆死区同步给手柄输入法。</summary>
    private void SyncImeDeadzones()
    {
        if (Ime?.Running != true) return;
        var settings = Engine?.Profile?.Settings;
        if (settings == null) return;
        Ime.SetStickDeadzones(settings.LeftStickDeadzone, settings.RightStickDeadzone);
    }

    /// <summary>
    /// 老配置没有 Ime 段：用原先的全局开关/模式/唤醒键补一次，之后以配置为准。
    /// </summary>
    private void MigrateImeProfiles()
    {
        var globalOn = BinputConfig.LoadEnabled();
        var mode = BinputConfig.LoadMode();
        var wake = BinputConfig.LoadWake();
        var changed = new List<MappingProfile>();
        foreach (var p in Store.Profiles.ToArray())
        {
            if (p.Ime == null || p.Ime.Configured) continue;
            p.Ime.Enabled = globalOn;
            p.Ime.Preview = mode.Preview;
            p.Ime.Tier = mode.Tier;
            p.Ime.WakeSpec = wake;
            p.Ime.Configured = true;
            changed.Add(p);
        }
        foreach (var p in changed) Store.Save(p);
    }

    /// <summary>把配置里的输入法设置应用到正在运行的宿主。</summary>
    private void ApplyImeSettings(ImeProfileSettings settings)
    {
        if (Ime?.Running != true) return;
        if (Ime.Preview != settings.Preview) Ime.Preview = settings.Preview;
        if (Ime.PasteTier != settings.Tier) Ime.PasteTier = settings.Tier;
        if (!string.Equals(Ime.WakeSpec ?? "", settings.WakeSpec ?? "", StringComparison.OrdinalIgnoreCase))
            Ime.SetWake(settings.WakeSpec ?? "");
        SyncImeDeadzones();
    }

    /// <summary>切换配置后按该配置的开关和输入设置启动/停止/更新输入法。</summary>
    public void ApplyImeProfile()
    {
        var profile = Engine?.Profile;
        if (profile?.Ime == null || Ime == null) return;
        if (profile.Ime.Enabled)
        {
            if (!Ime.Running) StartIme();
            else ApplyImeSettings(profile.Ime);
        }
        else if (Ime.Running)
        {
            StopIme();
        }
        RefreshFantasyBaseKeys();
    }

    /// <summary>配置管理 / 输入法页统一走这里：保存到当前配置并立即应用。</summary>
    public void SetImeEnabled(bool enabled)
    {
        var profile = Engine?.Profile;
        if (profile?.Ime == null) return;
        profile.Ime.Enabled = enabled;
        profile.Ime.Configured = true;
        Store.Save(profile);
        if (enabled) ApplyImeProfile();
        else StopIme();
        RefreshFantasyBaseKeys();
    }

    /// <summary>输入法开关/唤醒键变化后，让幻想模式基础键重新置灰。</summary>
    public void RefreshFantasyBaseKeys()
    {
        (MainWindow as MainWindow)?.RefreshFantasyBaseKeys();
    }

    /// <summary>启用「手柄输入法」：启动悬浮键盘、手柄轮询和设置页。幂等。</summary>
    public void StartIme()
    {
        try
        {
            if (Ime == null) return;
            var profile = Engine?.Profile;
            if (profile?.Ime != null)
            {
                profile.Ime.Enabled = true;
                profile.Ime.Configured = true;
                Store.Save(profile);
            }
            if (!Ime.Running) Ime.Start();
            if (profile?.Ime != null) ApplyImeSettings(profile.Ime);
            SyncImeDeadzones();
            BinputConfig.SaveEnabled(true);
            RefreshFantasyBaseKeys();
            LogBus.Publish("手柄输入法：已启用（L3 + R3 唤出）");
        }
        catch (Exception ex)
        {
            LogBus.Publish("手柄输入法启用失败：" + ex.Message);
        }
    }

    /// <summary>停用「手柄输入法」：收起键盘、停轮询、卸键盘钩子。</summary>
    public void StopIme()
    {
        try
        {
            var profile = Engine?.Profile;
            if (profile?.Ime != null)
            {
                profile.Ime.Enabled = false;
                profile.Ime.Configured = true;
                Store.Save(profile);
            }
            Ime?.Stop();
            BinputConfig.SaveEnabled(false);
            RefreshFantasyBaseKeys();
            LogBus.Publish("手柄输入法：已停用");
        }
        catch (Exception ex)
        {
            LogBus.Publish("手柄输入法停用失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把风刃自己的 BladeReport 翻译成柄输认识的 PadState（XInput 位布局）。
    /// 摇杆方向两套都是「+Y = 上」，扳机都是 0~255，直接搬。
    /// </summary>
    private static PadState ToPadState(BladeReport r)
    {
        ushort buttons = 0;
        if (r.IsPressed(BladeButtons.A)) buttons |= GamepadInput.A;
        if (r.IsPressed(BladeButtons.B)) buttons |= GamepadInput.B;
        if (r.IsPressed(BladeButtons.X)) buttons |= GamepadInput.X;
        if (r.IsPressed(BladeButtons.Y)) buttons |= GamepadInput.Y;
        if (r.IsPressed(BladeButtons.L1)) buttons |= GamepadInput.LB;
        if (r.IsPressed(BladeButtons.R1)) buttons |= GamepadInput.RB;
        if (r.IsPressed(BladeButtons.L3)) buttons |= GamepadInput.L3;
        if (r.IsPressed(BladeButtons.R3)) buttons |= GamepadInput.R3;
        if (r.IsPressed(BladeButtons.Select) || r.IsPressed(BladeButtons.Capture))
            buttons |= GamepadInput.BACK;
        if (r.IsPressed(BladeButtons.Start)) buttons |= GamepadInput.START;
        if (r.IsPressed(BladeButtons.Up)) buttons |= GamepadInput.DPAD_UP;
        if (r.IsPressed(BladeButtons.Down)) buttons |= GamepadInput.DPAD_DOWN;
        if (r.IsPressed(BladeButtons.Left)) buttons |= GamepadInput.DPAD_LEFT;
        if (r.IsPressed(BladeButtons.Right)) buttons |= GamepadInput.DPAD_RIGHT;

        byte lt = r.LeftTrigger;
        byte rt = r.RightTrigger;
        // 数字扳机被按下时直接当满行程；BLE 低上报下按键位比模拟量可靠。
        if (r.IsPressed(BladeButtons.L2)) lt = 255;
        if (r.IsPressed(BladeButtons.R2)) rt = 255;

        return new PadState
        {
            Connected = true,
            Buttons = buttons,
            LeftTrigger = lt,
            RightTrigger = rt,
            LX = r.LeftX,
            LY = r.LeftY,
            RX = r.RightX,
            RY = r.RightY,
        };
    }

    private static void WriteCrash(Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path, $"\n===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\n{ex}\n");
        }
        catch { }
    }

    private static void LogStartup(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }
}

/// <summary>简单的进程内日志总线，供界面与诊断页订阅。</summary>
public static class LogBus
{
    private static readonly object Sync = new();
    private static readonly Queue<string> Recent = new();
    private const int RecentLimit = 400;

    public static event EventHandler<string>? Message;

    public static void Publish(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (Sync)
        {
            // 保留最近若干条：诊断页是懒加载的，晚打开也要能看到连接时的按键映射。
            Recent.Enqueue(line);
            while (Recent.Count > RecentLimit) Recent.Dequeue();
            Message?.Invoke(null, line);
        }
    }

    /// <summary>最近日志快照，用于新建诊断页时补历史。</summary>
    public static string[] Snapshot()
    {
        lock (Sync) return Recent.ToArray();
    }
}

