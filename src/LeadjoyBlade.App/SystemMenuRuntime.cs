using System.Threading;
using LeadjoyBlade.Core.Input;
using LeadjoyBlade.Core.Mapping;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.App;

/// <summary>
/// 系统菜单运行时：热键开关、方向键导航、确认/取消、项目动作执行。
/// 只在幻想模式内工作；菜单打开期间由 FantasyRuntime 屏蔽方向键与确认/取消键的原映射。
/// </summary>
public sealed class SystemMenuRuntime
{
    private readonly bool[] _navDown = new bool[4];
    private bool _open;
    private bool _justOpened;
    private bool _hotkeyDown;
    private bool _confirmDown;
    private bool _cancelDown;
    private int _repeatDirection = -1;
    private DateTime _repeatAt;
    private int _categoryIndex;
    private int _itemIndex;
    private string _hotkeyLabel = "SELECT";
    private SystemMenuOverlayWindow? _overlay;
    private SystemMenuClickCatcher? _clickCatcher;
    private GameWindowTarget? _gameWindow;
    private readonly object _sync = new();
    private Timer? _repeatTimer;
    private BladeReport? _lastReport;
    private FantasyProfile? _menuProfile;

    public bool IsOpen => _open;

    public static BladeButtons HotkeyButton(FantasyProfile fantasy) =>
        fantasy.SystemMenuHotkey == "Start" ? BladeButtons.Start : BladeButtons.Select;

    public static bool IsMenuControlButton(FantasyProfile fantasy, string id) =>
        fantasy.SystemMenuEnabled
        && (string.Equals(fantasy.SystemMenuConfirmButton, id, StringComparison.Ordinal)
            || string.Equals(fantasy.SystemMenuCancelButton, id, StringComparison.Ordinal));

    /// <summary>ABXY 中同时按下两个及以上时，视为手柄组合键，不触发菜单确认/取消。</summary>
    public static bool HasFaceCombo(BladeReport report)
    {
        var count = 0;
        if (report.IsPressed(BladeButtons.A)) count++;
        if (report.IsPressed(BladeButtons.B)) count++;
        if (report.IsPressed(BladeButtons.X)) count++;
        if (report.IsPressed(BladeButtons.Y)) count++;
        return count >= 2;
    }

    /// <summary>由 FantasyRuntime 在每帧输入处理中调用。</summary>
    public void Update(BladeReport report, FantasyProfile fantasy, bool engineEnabled, bool modifierHeld = false)
    {
        lock (_sync)
        {
        _lastReport = report;
        var hotkeyPressed = report.IsPressed(HotkeyButton(fantasy));
        if (!engineEnabled || !fantasy.Enabled || !fantasy.SystemMenuEnabled)
        {
            Close();
            _hotkeyDown = hotkeyPressed;
            ResetEdges();
            return;
        }

        if (hotkeyPressed && !_hotkeyDown && !modifierHeld) Toggle(fantasy);
        _hotkeyDown = hotkeyPressed;

        if (!_open)
        {
            ResetEdges();
            return;
        }

        _menuProfile = fantasy;

        if (_justOpened)
        {
            _justOpened = false;
            SyncEdges(report, fantasy);
            return;
        }

        // 扳机组合键不属于系统菜单：菜单保持打开，但本帧不吃任何方向/确认/取消。
        if (modifierHeld)
        {
            SyncEdges(report, fantasy);
            return;
        }

        var now = DateTime.UtcNow;
        var changed = false;
        changed |= HandleDirection(0, report.IsPressed(BladeButtons.Left), now);
        changed |= HandleDirection(1, report.IsPressed(BladeButtons.Right), now);
        changed |= HandleDirection(2, report.IsPressed(BladeButtons.Up), now);
        changed |= HandleDirection(3, report.IsPressed(BladeButtons.Down), now);
        if (changed) PushOverlay(fantasy);

        var confirm = report.IsPressed(ParseButton(fantasy.SystemMenuConfirmButton));
        var cancel = report.IsPressed(ParseButton(fantasy.SystemMenuCancelButton));
        var faceCombo = HasFaceCombo(report);
        if (!faceCombo)
        {
            if (confirm && !_confirmDown)
            {
                _confirmDown = true;
                ExecuteSelected(fantasy);
                return;
            }
            if (cancel && !_cancelDown)
            {
                _cancelDown = true;
                Close();
                return;
            }
        }
        _confirmDown = confirm;
        _cancelDown = cancel;
        }
    }

    /// <summary>方向键按下沿：立即移动一次，并记录长按重复的起始时间。</summary>
    private bool HandleDirection(int direction, bool pressed, DateTime now)
    {
        var edge = pressed && !_navDown[direction];
        _navDown[direction] = pressed;

        if (edge)
        {
            var changed = MoveSelection(direction);
            _repeatDirection = direction;
            _repeatAt = now.AddMilliseconds(340);
            return changed;
        }

        if (!pressed && direction == _repeatDirection) _repeatDirection = -1;
        return false;
    }

    /// <summary>
    /// 长按重复由本地定时器驱动，不再依赖手柄持续上报。
    /// BLE（Blade Plus_1B / G-Touch）只在状态变化时通知，按住方向键时没有新报文，
    /// 之前只能在 Switch 这种高频上报的通路上连续切换。
    /// </summary>
    private void RepeatTick()
    {
        try
        {
            lock (_sync)
            {
                if (!_open || _repeatDirection < 0 || _lastReport == null || _menuProfile == null) return;
                var now = DateTime.UtcNow;
                if (now < _repeatAt) return;
                var pressed = _repeatDirection switch
                {
                    0 => _lastReport.IsPressed(BladeButtons.Left),
                    1 => _lastReport.IsPressed(BladeButtons.Right),
                    2 => _lastReport.IsPressed(BladeButtons.Up),
                    _ => _lastReport.IsPressed(BladeButtons.Down),
                };
                if (!pressed)
                {
                    _repeatDirection = -1;
                    return;
                }
                var changed = MoveSelection(_repeatDirection);
                _repeatAt = now.AddMilliseconds(90);
                if (changed) PushOverlay(_menuProfile);
            }
        }
        catch { }
    }

    private bool MoveSelection(int direction)
    {
        var fantasy = App.Current.Engine.Profile.Fantasy;
        var categories = fantasy.SystemMenuCategories;
        if (categories.Count == 0) return false;
        var previousItem = _itemIndex;

        if (direction is 0 or 1)
        {
            // 菜单栏到边界后停住，不再左右回绕；没换菜单栏时不重置项目。
            var next = direction == 1
                ? Math.Min(categories.Count - 1, _categoryIndex + 1)
                : Math.Max(0, _categoryIndex - 1);
            if (next == _categoryIndex) return false;
            _categoryIndex = next;
            _itemIndex = 0;
            return true;
        }

        var items = categories[Math.Clamp(_categoryIndex, 0, categories.Count - 1)].Items;
        if (items.Count == 0)
        {
            _itemIndex = 0;
            return _itemIndex != previousItem;
        }
        // 上下导航到边界停住，不再从第一项回绕到最后一项（反之亦然）。
        _itemIndex = direction == 3
            ? Math.Min(items.Count - 1, _itemIndex + 1)
            : Math.Max(0, _itemIndex - 1);
        return _itemIndex != previousItem;
    }

    private void SyncEdges(BladeReport report, FantasyProfile fantasy)
    {
        _confirmDown = report.IsPressed(ParseButton(fantasy.SystemMenuConfirmButton));
        _cancelDown = report.IsPressed(ParseButton(fantasy.SystemMenuCancelButton));
        _navDown[0] = report.IsPressed(BladeButtons.Left);
        _navDown[1] = report.IsPressed(BladeButtons.Right);
        _navDown[2] = report.IsPressed(BladeButtons.Up);
        _navDown[3] = report.IsPressed(BladeButtons.Down);
        _repeatDirection = -1;
    }

    private void ResetEdges()
    {
        _confirmDown = false;
        _cancelDown = false;
        Array.Clear(_navDown);
        _repeatDirection = -1;
    }

    public void Toggle(FantasyProfile fantasy)
    {
        if (_open) Close();
        else Open(fantasy);
    }

    public void Open(FantasyProfile fantasy)
    {
        lock (_sync)
        {
        SystemMenuDefaults.Normalize(fantasy);
        _categoryIndex = 0;
        _itemIndex = 0;
        _hotkeyLabel = fantasy.SystemMenuHotkey == "Start" ? "START" : "SELECT";
        _gameWindow = GameWindowTarget.Capture(App.Current.Engine.Profile.ProcessName);
        _open = true;
        _justOpened = true;
        SoundEffects.PlayMenuOpen(fantasy.MenuSoundEnabled ? fantasy.MenuSoundVolume : 0);
        _repeatDirection = -1;
        Array.Clear(_navDown);
        _confirmDown = false;
        _menuProfile = fantasy;
        _repeatTimer ??= new Timer(_ => RepeatTick(), null, 0, 33);
        // 先铺全屏点击层，再显示菜单，保证菜单绘制在点击层之上。
        App.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_clickCatcher == null)
            {
                _clickCatcher = new SystemMenuClickCatcher();
                _clickCatcher.DismissRequested += (_, _) => Close();
            }
            _clickCatcher.ShowCatcher();
        }));
        PushOverlay(fantasy);
        LogBus.Publish($"系统菜单：已打开（{_hotkeyLabel}，{fantasy.SystemMenuCategories.Count} 个菜单栏）");
        }
    }

    public void Close()
    {
        lock (_sync)
        {
        if (!_open) return;
        _repeatTimer?.Dispose();
        _repeatTimer = null;
        _menuProfile = null;
        _open = false;
        _justOpened = false;
        _repeatDirection = -1;
        Array.Clear(_navDown);
        var catcher = _clickCatcher;
        if (catcher != null)
        {
            App.Current.Dispatcher.BeginInvoke(new Action(() => catcher.HideCatcher()));
        }
        var overlay = _overlay;
        if (overlay != null)
        {
            App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { overlay.HideMenu(); } catch { }
            }));
        }
        LogBus.Publish("系统菜单：已关闭");
        }
    }

    public void Suspend() => Close();

    /// <summary>配置或菜单内容变化后刷新；关闭或禁用时收起悬浮窗。</summary>
    public void Refresh(FantasyProfile fantasy)
    {
        lock (_sync)
        {
        if (!fantasy.Enabled || !fantasy.SystemMenuEnabled)
        {
            Close();
            return;
        }
        SystemMenuDefaults.Normalize(fantasy);
        if (!_open) return;
        var categories = fantasy.SystemMenuCategories;
        if (categories.Count > 0)
        {
            _categoryIndex = Math.Clamp(_categoryIndex, 0, categories.Count - 1);
            var items = categories[_categoryIndex].Items;
            _itemIndex = items.Count == 0 ? 0 : Math.Clamp(_itemIndex, 0, items.Count - 1);
        }
        else
        {
            _categoryIndex = 0;
            _itemIndex = 0;
        }
        PushOverlay(fantasy);
        }
    }

    private void PushOverlay(FantasyProfile fantasy)
    {
        var categoryIndex = _categoryIndex;
        var itemIndex = _itemIndex;
        var hotkey = _hotkeyLabel;
        var dispatcher = App.Current.Dispatcher;
        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_overlay == null)
                {
                    _overlay = new SystemMenuOverlayWindow();
                    _overlay.DismissRequested += (_, _) => Close();
                }
                // 双十字热键栏区域保留可点击，其余区域点击退出菜单。
                _overlay.ClickThroughHole = App.Current.Fantasy.GetHotbarScreenBounds();
                _overlay.ShowMenu(fantasy, categoryIndex, itemIndex, hotkey);
            }
            catch (Exception ex)
            {
                LogBus.Publish($"系统菜单悬浮窗显示失败：{ex.Message}");
            }
        }));
    }

    private void ExecuteSelected(FantasyProfile fantasy)
    {
        SystemMenuCategory? category = null;
        SystemMenuItem? item = null;
        if (fantasy.SystemMenuCategories.Count > 0)
        {
            category = fantasy.SystemMenuCategories[Math.Clamp(_categoryIndex, 0, fantasy.SystemMenuCategories.Count - 1)];
            if (category.Items.Count > 0)
                item = category.Items[Math.Clamp(_itemIndex, 0, category.Items.Count - 1)];
        }

        var action = item?.Action.Clone();
        var itemName = item?.Name ?? "（空项目）";
        var categoryName = category?.Name ?? "（空菜单栏）";
        var target = _gameWindow;
        Close();

        if (action == null || action.IsEmpty)
        {
            LogBus.Publish($"系统菜单：{categoryName} / {itemName} 未配置映射，已关闭");
            return;
        }

        LogBus.Publish($"系统菜单：{categoryName} / {itemName} → {action.Describe()}");
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteAsync(action, target);
            }
            catch (Exception ex)
            {
                LogBus.Publish($"系统菜单动作执行失败：{ex.Message}");
            }
        });
    }

    private static async Task ExecuteAsync(SystemMenuAction action, GameWindowTarget? target)
    {
        switch (action.Kind)
        {
            case SystemMenuActionKind.Key:
                await ExecuteKeyChordAsync(action.Modifiers, action.Key, 70);
                break;
            case SystemMenuActionKind.Gamepad:
                await ExecuteGamepadAsync(action.GamepadButtons, 80, 0, target);
                break;
            case SystemMenuActionKind.MouseClick:
                if (action.MouseScreen)
                {
                    ScreenTarget.Click(action.MouseMonitor, action.MouseMonitorX, action.MouseMonitorY,
                        action.MouseMonitorWidth, action.MouseMonitorHeight,
                        action.MouseX, action.MouseY, action.MouseButton, App.Current.Mouse);
                }
                else
                {
                    GameWindowTarget.ClickRelative(ResolveWindow(target), action.MouseX, action.MouseY,
                        action.MouseButton, App.Current.Mouse);
                }
                break;
            case SystemMenuActionKind.Macro:
                await ExecuteSystemMacroAsync(action.Steps, target);
                break;
            case SystemMenuActionKind.Text:
                App.Current.Keyboard.TypeText(action.Text);
                await Task.Delay(70);
                break;
        }
    }

    private static async Task ExecuteSystemMacroAsync(List<SystemMenuMacroStep> steps, GameWindowTarget? target)
    {
        foreach (var step in steps)
        {
            if (!App.Current.Engine.Enabled) break;
            switch (step.Kind)
            {
                case SystemMenuStepKind.Delay:
                    await Task.Delay(Math.Max(1, step.DelayMs));
                    continue;
                case SystemMenuStepKind.Key:
                    await ExecuteKeyChordAsync(step.Modifiers, step.Key, Math.Max(10, step.HoldMs));
                    break;
                case SystemMenuStepKind.Gamepad:
                    await ExecuteGamepadAsync(step.EffectiveGamepadButtons.ToList(), Math.Max(10, step.HoldMs), 0, target);
                    break;
                case SystemMenuStepKind.MouseClick:
                    if (step.MouseScreen)
                    {
                        ScreenTarget.Click(step.MouseMonitor, step.MouseMonitorX, step.MouseMonitorY,
                            step.MouseMonitorWidth, step.MouseMonitorHeight,
                            step.MouseX, step.MouseY, step.MouseButton, App.Current.Mouse);
                    }
                    else
                    {
                        GameWindowTarget.ClickRelative(ResolveWindow(target), step.MouseX, step.MouseY,
                            step.MouseButton, App.Current.Mouse);
                    }
                    break;
                case SystemMenuStepKind.Text:
                    App.Current.Keyboard.TypeText(step.Text);
                    break;
            }
            if (step.DelayMs > 0) await Task.Delay(step.DelayMs);
        }
    }

    /// <summary>窗口模式优先点击配置进程的窗口；找不到时回退到打开菜单时捕获的目标。</summary>
    private static GameWindowTarget? ResolveWindow(GameWindowTarget? fallback)
    {
        var process = App.Current.Engine.Profile.ProcessName;
        if (!string.IsNullOrWhiteSpace(process))
        {
            var handle = GameWindowTarget.FindProcessWindow(process);
            if (handle != IntPtr.Zero) return GameWindowTarget.Create(handle) ?? fallback;
        }
        return fallback;
    }

    private static async Task ExecuteKeyChordAsync(List<int> modifiers, int key, int holdMs)
    {
        foreach (var modifier in modifiers) App.Current.Keyboard.KeyDown(modifier);
        App.Current.Keyboard.KeyDown(key);
        await Task.Delay(Math.Max(10, holdMs));
        App.Current.Keyboard.KeyUp(key);
        for (var i = modifiers.Count - 1; i >= 0; i--) App.Current.Keyboard.KeyUp(modifiers[i]);
    }

    private static async Task ExecuteGamepadAsync(List<string> sources, int holdMs, int delayMs, GameWindowTarget? target)
    {
        var held = new List<BindingAction>();
        foreach (var source in sources)
        {
            var action = ResolveGamepadAction(source);
            if (action == null || action.Type == ActionType.Disabled) continue;
            switch (action.Type)
            {
                case ActionType.Key:
                    foreach (var modifier in action.Modifiers) App.Current.Keyboard.KeyDown(modifier);
                    App.Current.Keyboard.KeyDown(action.Key);
                    held.Add(action);
                    break;
                case ActionType.MouseButton:
                    App.Current.Mouse.ButtonDown(action.MouseButton);
                    held.Add(action);
                    break;
                case ActionType.Text:
                    App.Current.Keyboard.TypeText(action.Text);
                    break;
                case ActionType.MouseWheel:
                    App.Current.Mouse.Wheel(action.WheelDelta);
                    break;
                case ActionType.Macro:
                    await ExecuteProfileMacroAsync(action, target);
                    break;
            }
        }

        if (held.Count > 0) await Task.Delay(Math.Max(10, holdMs));
        for (var i = held.Count - 1; i >= 0; i--)
        {
            var action = held[i];
            switch (action.Type)
            {
                case ActionType.Key:
                    App.Current.Keyboard.KeyUp(action.Key);
                    for (var m = action.Modifiers.Count - 1; m >= 0; m--) App.Current.Keyboard.KeyUp(action.Modifiers[m]);
                    break;
                case ActionType.MouseButton:
                    App.Current.Mouse.ButtonUp(action.MouseButton);
                    break;
            }
        }
        if (delayMs > 0) await Task.Delay(delayMs);
    }

    private static BindingAction? ResolveGamepadAction(string source)
    {
        var fantasy = App.Current.Engine.Profile.Fantasy;
        var normalized = NormalizeGamepadSource(source);
        if (fantasy.BaseActions.TryGetValue(normalized, out var baseAction) && baseAction.Type != ActionType.Disabled)
            return baseAction;
        return App.Current.Engine.Profile.FindBinding(source, BindingLayer.Base)?.Action
               ?? App.Current.Engine.Profile.FindBinding(normalized, BindingLayer.Base)?.Action;
    }

    private static string NormalizeGamepadSource(string source) => source switch
    {
        "L1" => "LB",
        "R1" => "RB",
        "M1" => "L4",
        "M2" => "R4",
        _ => source,
    };

    private static async Task ExecuteProfileMacroAsync(BindingAction action, GameWindowTarget? target)
    {
        var library = App.Current.Store.MacroLibrary;
        var macro = library.Items.FirstOrDefault(x => x.Id == action.MacroId);
        if (macro == null && !string.IsNullOrWhiteSpace(action.MacroName))
            macro = library.Items.FirstOrDefault(x => x.Name == action.MacroName);
        var steps = macro?.Steps ?? action.Macro;
        foreach (var step in steps)
        {
            if (!App.Current.Engine.Enabled) break;
            switch (step.Kind)
            {
                case MacroStepKind.Delay:
                    await Task.Delay(Math.Max(1, step.DelayMs));
                    continue;
                case MacroStepKind.Key:
                    await ExecuteKeyChordAsync(step.Modifiers, step.Key, Math.Max(5, step.HoldMs));
                    break;
                case MacroStepKind.KeyDown:
                    foreach (var modifier in step.Modifiers) App.Current.Keyboard.KeyDown(modifier);
                    App.Current.Keyboard.KeyDown(step.Key);
                    break;
                case MacroStepKind.KeyUp:
                    App.Current.Keyboard.KeyUp(step.Key);
                    for (var i = step.Modifiers.Count - 1; i >= 0; i--) App.Current.Keyboard.KeyUp(step.Modifiers[i]);
                    break;
                case MacroStepKind.MouseButton:
                    if (step.MouseUsePosition &&
                        MacroMouse.Click(step, App.Current.Mouse,
                            App.Current.Engine.Profile.ProcessName, Math.Max(5, step.HoldMs)))
                        break;
                    App.Current.Mouse.ButtonDown(step.MouseButton);
                    await Task.Delay(Math.Max(5, step.HoldMs));
                    App.Current.Mouse.ButtonUp(step.MouseButton);
                    break;
                case MacroStepKind.MouseDown:
                    if (step.MouseUsePosition)
                        MacroMouse.MoveTo(step, App.Current.Mouse, App.Current.Engine.Profile.ProcessName);
                    App.Current.Mouse.ButtonDown(step.MouseButton);
                    break;
                case MacroStepKind.MouseUp:
                    if (step.MouseUsePosition)
                        MacroMouse.MoveTo(step, App.Current.Mouse, App.Current.Engine.Profile.ProcessName);
                    App.Current.Mouse.ButtonUp(step.MouseButton);
                    break;
                case MacroStepKind.Gamepad:
                case MacroStepKind.GamepadDown:
                case MacroStepKind.GamepadUp:
                    await ExecuteGamepadAsync([step.GamepadSource], Math.Max(10, step.HoldMs), 0, target);
                    break;
                case MacroStepKind.Text:
                    App.Current.Keyboard.TypeText(step.Text);
                    break;
            }
            if (step.DelayMs > 0) await Task.Delay(step.DelayMs);
        }
    }

    private static BladeButtons ParseButton(string id) =>
        Enum.TryParse<BladeButtons>(id, ignoreCase: true, out var button) ? button : BladeButtons.None;
}











