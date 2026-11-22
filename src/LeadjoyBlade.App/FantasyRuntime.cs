using LeadjoyBlade.Core.Input;
using LeadjoyBlade.Core.Mapping;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.App;

/// <summary>幻想模式运行时：基础键、双十字、RB 套装、LT/RT 扩展与摇杆。</summary>
public sealed class FantasyRuntime
{
    private readonly object _sync = new();
    private readonly Dictionary<string, BindingAction> _held = [];
    private readonly Dictionary<string, CancellationTokenSource> _slotTurbo = [];
    private readonly Dictionary<string, CancellationTokenSource> _baseTurbo = [];
    private readonly Dictionary<int, CancellationTokenSource> _clickTurbo = [];
    private readonly HashSet<string> _stickDirections = [];
    private readonly SystemMenuRuntime _systemMenu = new();
    private readonly Timer _mouseTimer;
    private BladeReport? _lastReport;
    private DateTime _lastReportAt = DateTime.UtcNow;
    private FantasyOverlayWindow? _overlay;
    private bool _wheelMode;
    private double _wheelAccum;
    private bool _lt;
    private bool _rt;
    private bool _activeLt;
    private bool _activeRt;
    private bool _primaryLt = true;
    private bool _extension;
    private bool _extensionRight;
    private bool _rbDown;
    private bool _rbUsed;
    private DateTime _rbDownAt;
    private bool _ltSelectDown;
    private bool _ltSelectSuppressed;
    private bool _ltR3MouseDown;
    private bool _rtR3MouseDown;
    private bool _rbR3Down;
    private bool _rbR3Used;
    private bool _selecting;
    private int _lastSelectMask;
    /// <summary>
    /// 菜单确认/取消按下后，菜单可能在本帧关闭；这一下不能紧接着又映射给键盘。
    /// 位：A=1, B=2, X=4, Y=8。等对应键松开再清。
    /// </summary>
    private int _menuFaceLatch;
    private int _setIndex;
    private int _sensitivityIndex;
    private int _uiSet = -1;
    private bool _uiExtension;
    private bool _uiExtensionRight;
    private bool _uiLt;
    private bool _uiRt;
    private bool _uiSelecting;

    public FantasyRuntime()
    {
        _mouseTimer = new Timer(_ => MouseTick(), null, 8, 8);
    }

    public bool Enabled => CurrentFantasy?.Enabled == true;

    private FantasyProfile? CurrentFantasy => (System.Windows.Application.Current as App)?.Engine?.Profile?.Fantasy;
    private FantasyLibrary Library => App.Current.Store.FantasyLibrary;

    public void Update(BladeReport report)
    {
        lock (_sync)
        {
            if (!Enabled)
            {
                ResetLocked();
                return;
            }
            if (!App.Current.Engine.Enabled)
            {
                ResetLocked();
                return;
            }
            var fantasy = CurrentFantasy!;
            var previousReport = _lastReport;
            _lastReport = report;
            _lastReportAt = DateTime.UtcNow;
            _setIndex = Math.Clamp(_setIndex, 0, fantasy.Sets.Count - 1);
            _sensitivityIndex = Math.Clamp(_sensitivityIndex, 0, fantasy.SensitivityLevels.Count - 1);

            // XBOX 模式下扳机可能只上报模拟量而不置 L2/R2 位，因此模拟量也参与判定。
            var lt = report.IsPressed(BladeButtons.L2) || report.LeftTriggerNorm >= 0.15f;
            var rt = report.IsPressed(BladeButtons.R2) || report.RightTriggerNorm >= 0.15f;
            var rb = report.IsPressed(BladeButtons.R1);
            var r3 = report.IsPressed(BladeButtons.R3);
            var c = report.IsPressed(BladeButtons.C);
            var a = report.IsPressed(BladeButtons.A);
            var b = report.IsPressed(BladeButtons.B);
            var x = report.IsPressed(BladeButtons.X);
            var y = report.IsPressed(BladeButtons.Y);
            var up = report.IsPressed(BladeButtons.Up);
            var down = report.IsPressed(BladeButtons.Down);
            var left = report.IsPressed(BladeButtons.Left);
            var right = report.IsPressed(BladeButtons.Right);
            var lb = report.IsPressed(BladeButtons.L1);
            var l4 = report.IsPressed(BladeButtons.M1);
            var r4 = report.IsPressed(BladeButtons.M2);
            var select = report.IsPressed(BladeButtons.Select);
            var start = report.IsPressed(BladeButtons.Start);
            var capture = report.IsPressed(BladeButtons.Capture);
            var l3 = report.IsPressed(BladeButtons.L3);
            var fn = report.IsPressed(BladeButtons.Fn);

            // 系统菜单：处理热键、方向导航、确认/取消；菜单打开时下列按键由菜单接管。
            // menuWasOpen 保证“确认后本帧关闭”时不会再把 A/B 映射给游戏。
            // 扳机按住时菜单不应占用任何按键，组合键要留给原映射/悬浮窗快捷键。
            var modifierHeld = lt || rt;
            var menuWasOpen = _systemMenu.IsOpen;
            _systemMenu.Update(report, fantasy, App.Current.Engine.Enabled, modifierHeld);
            var menuOpen = _systemMenu.IsOpen || menuWasOpen;
            var hotkeyIsSelect = fantasy.SystemMenuEnabled && fantasy.SystemMenuHotkey != "Start";
            var hotkeyIsStart = fantasy.SystemMenuEnabled && fantasy.SystemMenuHotkey == "Start";
            // ABXY 同时按下时是手柄组合键，不当作菜单确认/取消，仍走原映射。
            var faceCombo = menuOpen && SystemMenuRuntime.HasFaceCombo(report);
            // 当前配置启用了手柄输入法：它的唤醒键不能再走幻想模式基础键。
            var ime = App.Current.Engine?.Profile?.Ime;
            var imeActive = ime?.Enabled == true || App.Current.ImeRunning;
            var imeKeys = imeActive
                ? ImeWakeConflicts.WakeBaseKeys(ime?.WakeSpec ?? "")
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var r3Mapped = r3 && !imeKeys.Contains("R3");

            // 菜单关闭的那一帧，确认/取消键可能还按着。只靠 menuOpen 只挡一帧，
            // 下一帧 A/B 仍会走基础键映射。这里把参与菜单的 ABXY 锁到松开为止。
            if (menuWasOpen && !_systemMenu.IsOpen && !modifierHeld && !faceCombo)
            {
                _menuFaceLatch = 0;
                if (a && SystemMenuRuntime.IsMenuControlButton(fantasy, "A")) _menuFaceLatch |= 1;
                if (b && SystemMenuRuntime.IsMenuControlButton(fantasy, "B")) _menuFaceLatch |= 2;
                if (x && SystemMenuRuntime.IsMenuControlButton(fantasy, "X")) _menuFaceLatch |= 4;
                if (y && SystemMenuRuntime.IsMenuControlButton(fantasy, "Y")) _menuFaceLatch |= 8;
            }
            // 松开就解锁，避免下一次正常按键被吞。
            if (!a) _menuFaceLatch &= ~1;
            if (!b) _menuFaceLatch &= ~2;
            if (!x) _menuFaceLatch &= ~4;
            if (!y) _menuFaceLatch &= ~8;
            bool Latched(string id) => id switch
            {
                "A" => (_menuFaceLatch & 1) != 0,
                "B" => (_menuFaceLatch & 2) != 0,
                "X" => (_menuFaceLatch & 4) != 0,
                "Y" => (_menuFaceLatch & 8) != 0,
                _ => false,
            };

            // 触发键状态与扩展页。未启用扩展时只认先按下的那一侧：
            // 后按的扳机不激活自己半栏；先按的松开后才轮到仍在按住的一侧。
            var ltWas = _lt;
            var rtWas = _rt;
            var both = lt && rt;
            if (both && fantasy.ExtensionEnabled)
            {
                if (!_extension)
                {
                    _extension = true;
                    _extensionRight = !ltWas;
                }
                _activeLt = false;
                _activeRt = false;
            }
            else
            {
                _extension = false;
                if (!both)
                {
                    _activeLt = lt;
                    _activeRt = rt;
                    _primaryLt = lt;
                }
                else
                {
                    if (!ltWas && rtWas) _primaryLt = false;
                    else if (ltWas && !rtWas) _primaryLt = true;
                    else if (!ltWas && !rtWas) _primaryLt = true;
                    _activeLt = _primaryLt;
                    _activeRt = !_primaryLt;
                }
            }
            _lt = lt;
            _rt = rt;

            // R3：单独按下切换滚轮模式；LT/RT/RB + R3 属于固定组合，不切换滚轮。
            if (r3Mapped && !_held.ContainsKey("__r3"))
            {
                if (!lt && !rt && !rb)
                {
                    _wheelMode = !_wheelMode;
                    _wheelAccum = 0;
                }
            }
            SetVirtual("__r3", r3Mapped);

            // 固定组合：LT+R3 左键、RT+R3 右键（按住期间保持按下）。
            var ltR3 = lt && r3Mapped;
            if (ltR3 != _ltR3MouseDown)
            {
                _ltR3MouseDown = ltR3;
                if (ltR3) App.Current.Mouse.ButtonDown(MouseButtonKind.Left);
                else App.Current.Mouse.ButtonUp(MouseButtonKind.Left);
            }
            var rtR3 = rt && r3Mapped;
            if (rtR3 != _rtR3MouseDown)
            {
                _rtR3MouseDown = rtR3;
                if (rtR3) App.Current.Mouse.ButtonDown(MouseButtonKind.Right);
                else App.Current.Mouse.ButtonUp(MouseButtonKind.Right);
            }
            // 灵敏度切换键：设置里选 CAPTURE（默认）或 C；选中的键不再走基础键映射。
            var sensitivityIsC = string.Equals(fantasy.SensitivitySwitchKey, "C", StringComparison.OrdinalIgnoreCase);
            var sensitivityPressed = sensitivityIsC ? c : capture;
            if (sensitivityPressed && !_held.ContainsKey("__sensitivity"))
            {
                if (fantasy.SensitivityLevels.Count > 0)
                    _sensitivityIndex = (_sensitivityIndex + 1) % fantasy.SensitivityLevels.Count;
            }
            SetVirtual("__sensitivity", sensitivityPressed);

            // RB：点按循环；长按显示 8 热键栏选择；长按期间按不同键可连续切换。
            var selectMask = ((a && !Latched("A")) ? 1 : 0) | ((b && !Latched("B")) ? 2 : 0)
                             | ((x && !Latched("X")) ? 4 : 0) | ((y && !Latched("Y")) ? 8 : 0)
                             | (up ? 16 : 0) | (down ? 32 : 0) | (left ? 64 : 0) | (right ? 128 : 0);
            if (rb && !_rbDown)
            {
                _rbDown = true;
                _rbUsed = false;
                _selecting = false;
                _lastSelectMask = selectMask;
                _rbDownAt = DateTime.UtcNow;
            }
            if (rb && !_selecting && (DateTime.UtcNow - _rbDownAt).TotalMilliseconds > 180)
                _selecting = true;
            if (rb)
            {
                var newMask = selectMask & ~_lastSelectMask;
                if (newMask != 0)
                {
                    var direct = DirectSetIndex(newMask);
                    if (direct >= 0)
                    {
                        if (direct != _setIndex) SoundEffects.PlayHotbarSwitch(fantasy.HotbarSoundEnabled ? fantasy.HotbarSoundVolume : 0);
                        _setIndex = direct;
                        _rbUsed = true;
                    }
                }
            }
            _lastSelectMask = rb ? selectMask : 0;
            if (!rb && _rbDown)
            {
                _rbDown = false;
                if (!_rbUsed && !_rbR3Used && !_selecting
                    && (DateTime.UtcNow - _rbDownAt).TotalMilliseconds <= 180)
                {
                    var next = NextVisibleSet(fantasy, _setIndex);
                    if (next != _setIndex)
                    {
                        _setIndex = next;
                        SoundEffects.PlayHotbarSwitch(fantasy.HotbarSoundEnabled ? fantasy.HotbarSoundVolume : 0);
                    }
                }
                _rbUsed = false;
                _rbR3Used = false;
                _selecting = false;
                _lastSelectMask = 0;
            }

            // 固定组合：RB+R3 切换灵敏度档位；不触发 RB 的热键栏循环。
            var rbR3 = rb && r3Mapped;
            if (rbR3 && !_rbR3Down)
            {
                if (fantasy.SensitivityLevels.Count > 0)
                    _sensitivityIndex = (_sensitivityIndex + 1) % fantasy.SensitivityLevels.Count;
                _rbR3Used = true;
            }
            _rbR3Down = rbR3;

            var suppressFace = _rbDown;
            var crossActive = (_lt || _rt) && !_rbDown;

            // LT + SELECT：切换悬浮窗显示/隐藏；组合键不触发 SELECT 自身动作。
            // 即使系统菜单把 SELECT 设为呼出键，这个组合也必须保留给悬浮窗。
            var ltSelect = lt && select;
            if (ltSelect && !_ltSelectDown) ToggleOverlay();
            _ltSelectDown = ltSelect;
            if (ltSelect) _ltSelectSuppressed = true;
            if (!select) _ltSelectSuppressed = false;

            // 基础键（十字/ABXY 在触发器激活时走格子）
            // 系统菜单打开时：方向键只做菜单导航，确认/取消键只做菜单操作。
            ApplyBase("A", a, Latched("A") || suppressFace || crossActive || (menuOpen && !modifierHeld && !faceCombo && SystemMenuRuntime.IsMenuControlButton(fantasy, "A")));
            ApplyBase("B", b, Latched("B") || suppressFace || crossActive || (menuOpen && !modifierHeld && !faceCombo && SystemMenuRuntime.IsMenuControlButton(fantasy, "B")));
            ApplyBase("X", x, Latched("X") || suppressFace || crossActive || (menuOpen && !modifierHeld && !faceCombo && SystemMenuRuntime.IsMenuControlButton(fantasy, "X")));
            ApplyBase("Y", y, Latched("Y") || suppressFace || crossActive || (menuOpen && !modifierHeld && !faceCombo && SystemMenuRuntime.IsMenuControlButton(fantasy, "Y")));
            ApplyBase("Up", up, suppressFace || crossActive || (menuOpen && !modifierHeld));
            ApplyBase("Down", down, suppressFace || crossActive || (menuOpen && !modifierHeld));
            ApplyBase("Left", left, suppressFace || crossActive || (menuOpen && !modifierHeld));
            ApplyBase("Right", right, suppressFace || crossActive || (menuOpen && !modifierHeld));
            ApplyBase("LB", lb, suppressFace);
            ApplyBase("L4", l4, suppressFace);
            ApplyBase("R4", r4, suppressFace);
            ApplyBase("Select", select, imeKeys.Contains("Select") || suppressFace || _ltSelectSuppressed || (hotkeyIsSelect && !modifierHeld));
            ApplyBase("Start", start, imeKeys.Contains("Start") || suppressFace || (hotkeyIsStart && !modifierHeld));
            ApplyBase("Capture", capture, suppressFace || !sensitivityIsC);
            ApplyBase("C", c, suppressFace || sensitivityIsC);
            ApplyBase("L3", l3, imeKeys.Contains("L3") || suppressFace);
            ApplyBase("Fn", fn, suppressFace);

            // 菜单打开时方向键/确认/取消不参与双十字格子；X/Y 等其他键照常。
            var slotUp = up && !(menuOpen && !modifierHeld);
            var slotDown = down && !(menuOpen && !modifierHeld);
            var slotLeft = left && !(menuOpen && !modifierHeld);
            var slotRight = right && !(menuOpen && !modifierHeld);
            var slotA = a && !Latched("A") && !(menuOpen && !modifierHeld && !faceCombo && SystemMenuRuntime.IsMenuControlButton(fantasy, "A"));
            var slotB = b && !Latched("B") && !(menuOpen && !modifierHeld && !faceCombo && SystemMenuRuntime.IsMenuControlButton(fantasy, "B"));
            var slotX = x && !Latched("X") && !(menuOpen && !modifierHeld && !faceCombo && SystemMenuRuntime.IsMenuControlButton(fantasy, "X"));
            var slotY = y && !Latched("Y") && !(menuOpen && !modifierHeld && !faceCombo && SystemMenuRuntime.IsMenuControlButton(fantasy, "Y"));

            if (crossActive)
            {
                if (_extension && fantasy.ExtensionEnabled)
                {
                    var extIndex = Math.Clamp(
                        _extensionRight ? fantasy.RightExtensionSet : fantasy.LeftExtensionSet,
                        0, fantasy.Sets.Count - 1);
                    var slots = fantasy.Sets[extIndex].Slots;
                    var baseIndex = _extensionRight ? 8 : 0;
                    ApplySlot("X.Up", slotUp, slots, baseIndex + 0);
                    ApplySlot("X.Down", slotDown, slots, baseIndex + 1);
                    ApplySlot("X.Left", slotLeft, slots, baseIndex + 2);
                    ApplySlot("X.Right", slotRight, slots, baseIndex + 3);
                    ApplySlot("X.Y", slotY, slots, baseIndex + 4);
                    ApplySlot("X.X", slotX, slots, baseIndex + 5);
                    ApplySlot("X.B", slotB, slots, baseIndex + 6);
                    ApplySlot("X.A", slotA, slots, baseIndex + 7);
                    ReleasePrefix("Slot.X2.");
                }
                else
                {
                    var slots = fantasy.Sets[_setIndex].Slots;
                    if (_activeLt)
                    {
                        ApplySlot("X.Up", slotUp, slots, 0);
                        ApplySlot("X.Down", slotDown, slots, 1);
                        ApplySlot("X.Left", slotLeft, slots, 2);
                        ApplySlot("X.Right", slotRight, slots, 3);
                        ApplySlot("X.Y", slotY, slots, 4);
                        ApplySlot("X.X", slotX, slots, 5);
                        ApplySlot("X.B", slotB, slots, 6);
                        ApplySlot("X.A", slotA, slots, 7);
                    }
                    else
                    {
                        ReleasePrefix("Slot.X.");
                    }
                    if (_activeRt)
                    {
                        ApplySlot("X2.Up", slotUp, slots, 8);
                        ApplySlot("X2.Down", slotDown, slots, 9);
                        ApplySlot("X2.Left", slotLeft, slots, 10);
                        ApplySlot("X2.Right", slotRight, slots, 11);
                        ApplySlot("X2.Y", slotY, slots, 12);
                        ApplySlot("X2.X", slotX, slots, 13);
                        ApplySlot("X2.B", slotB, slots, 14);
                        ApplySlot("X2.A", slotA, slots, 15);
                    }
                    else
                    {
                        ReleasePrefix("Slot.X2.");
                    }
                }
            }
            else
            {
                ReleasePrefix("Slot.X.");
                ReleasePrefix("Slot.X2.");
            }

            UpdateLeftStick(report, fantasy);
            // 菜单只接管单键；LT/RT 组合仍要播放按键动画。
            AnimateActiveSlots(fantasy, previousReport, report, menuOpen && !modifierHeld);
            PushOverlay(fantasy);
        }
    }

    private void AnimateActiveSlots(FantasyProfile fantasy, BladeReport? previous, BladeReport report, bool menuOpen)
    {
        if (_rbDown || menuOpen)
        {
            return;
        }
        var any = _activeLt || _activeRt || (_extension && fantasy.ExtensionEnabled);
        if (!any)
        {
            return;
        }

        if (_extension && fantasy.ExtensionEnabled)
        {
            var extIndex = Math.Clamp(
                _extensionRight ? fantasy.RightExtensionSet : fantasy.LeftExtensionSet,
                0, fantasy.Sets.Count - 1);
            AnimateSlots(fantasy.Sets[extIndex].Slots, _extensionRight ? 8 : 0, previous, report);
            return;
        }

        var slots = fantasy.Sets[_setIndex].Slots;
        if (_activeLt) AnimateSlots(slots, 0, previous, report);
        if (_activeRt) AnimateSlots(slots, 8, previous, report);
    }

    private void AnimateSlots(List<string?> slots, int baseIndex, BladeReport? previous, BladeReport report)
    {
        AnimateSlot(baseIndex + 0, report.IsPressed(BladeButtons.Up), previous?.IsPressed(BladeButtons.Up) == true, slots);
        AnimateSlot(baseIndex + 1, report.IsPressed(BladeButtons.Down), previous?.IsPressed(BladeButtons.Down) == true, slots);
        AnimateSlot(baseIndex + 2, report.IsPressed(BladeButtons.Left), previous?.IsPressed(BladeButtons.Left) == true, slots);
        AnimateSlot(baseIndex + 3, report.IsPressed(BladeButtons.Right), previous?.IsPressed(BladeButtons.Right) == true, slots);
        AnimateSlot(baseIndex + 4, report.IsPressed(BladeButtons.Y), previous?.IsPressed(BladeButtons.Y) == true, slots);
        AnimateSlot(baseIndex + 5, report.IsPressed(BladeButtons.X), previous?.IsPressed(BladeButtons.X) == true, slots);
        AnimateSlot(baseIndex + 6, report.IsPressed(BladeButtons.B), previous?.IsPressed(BladeButtons.B) == true, slots);
        AnimateSlot(baseIndex + 7, report.IsPressed(BladeButtons.A), previous?.IsPressed(BladeButtons.A) == true, slots);
    }

    private void AnimateSlot(int index, bool pressed, bool wasPressed, List<string?> slots)
    {
        if (!pressed || wasPressed) return;
        if (index < 0 || index >= slots.Count) return;
        var id = slots[index];
        if (string.IsNullOrWhiteSpace(id)) return;
        var item = Library.Items.FirstOrDefault(x => x.Id == id);
        if (item == null || item.Action.Type == ActionType.Disabled) return;
        // 初次按下立即播放；连发动作的后续动画由对应动作循环按间隔继续触发。
        FireSlotTrigger(index);
    }

    private void FireSlotTrigger(int index)
    {
        try { _overlay?.PlaySlotTrigger(index); } catch { }
        try { SlotTriggered?.Invoke(index); } catch { }
    }

    /// <summary>只在可见状态变化时推送到悬浮窗，避免每个报文都排队 UI 调度。</summary>
    private void PushOverlay(FantasyProfile fantasy)
    {
        if (_overlay == null) return;
        if (_uiSet == _setIndex && _uiExtension == _extension && _uiExtensionRight == _extensionRight
            && _uiLt == _activeLt && _uiRt == _activeRt && _uiSelecting == _selecting) return;
        _uiSet = _setIndex;
        _uiExtension = _extension;
        _uiExtensionRight = _extensionRight;
        _uiLt = _activeLt;
        _uiRt = _activeRt;
        _uiSelecting = _selecting;
        _overlay.UpdateState(fantasy, _setIndex, _extension, _extensionRight, _activeLt, _activeRt, _selecting);
    }

    private void ApplyBase(string id, bool pressed, bool suppressed)
    {
        var key = "Base." + id;
        if (suppressed)
        {
            StopBaseTurbo(key);
            Release(key);
            return;
        }
        var action = CurrentFantasy?.BaseActions.GetValueOrDefault(id);
        if (!pressed)
        {
            StopBaseTurbo(key);
            Release(key);
            return;
        }
        if (action is { Turbo: true } && action.Type is ActionType.Key or ActionType.MouseButton or ActionType.MouseWheel)
        {
            if (!_baseTurbo.ContainsKey(key)) StartBaseTurbo(key, action);
            return;
        }
        Apply(key, action);
    }

    private void StartBaseTurbo(string key, BindingAction action)
    {
        StopBaseTurbo(key);
        var cts = new CancellationTokenSource();
        _baseTurbo[key] = cts;
        _ = Task.Run(async () =>
        {
            var token = cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await TurboStep(action, token);
                    await Task.Delay(Math.Max(15, action.TurboIntervalMs), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            finally
            {
                EndTurboStep(action);
            }
        }, cts.Token);
    }

    private void StopBaseTurbo(string key)
    {
        if (_baseTurbo.Remove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void ApplySlot(string id, bool pressed, List<string?> slots, int index)
    {
        var key = "Slot." + id;
        if (!pressed) { StopSlotTurbo(key); Release(key); return; }
        if (index < 0 || index >= slots.Count) return;
        var itemId = slots[index];
        if (string.IsNullOrWhiteSpace(itemId)) return;
        var item = Library.Items.FirstOrDefault(x => x.Id == itemId);
        if (item == null) return;
        if (item.Action.Turbo && item.Action.Type is ActionType.Key or ActionType.MouseButton or ActionType.MouseWheel)
        {
            if (!_slotTurbo.ContainsKey(key))
                StartSlotTurbo(key, index, item.Action);
        }
        else
        {
            Apply(key, item.Action);
        }
    }

    private void StartSlotTurbo(string key, int slotIndex, BindingAction action)
    {
        StopSlotTurbo(key);
        var cts = new CancellationTokenSource();
        _slotTurbo[key] = cts;
        _ = Task.Run(async () =>
        {
            var token = cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await TurboStep(action, token);
                    await Task.Delay(Math.Max(15, action.TurboIntervalMs), token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested) FireSlotTrigger(slotIndex);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            finally
            {
                EndTurboStep(action);
            }
        }, cts.Token);
    }

    private static async Task TurboStep(BindingAction action, CancellationToken token)
    {
        switch (action.Type)
        {
            case ActionType.Key:
                App.Current.Keyboard.Chord(action.Modifiers, action.Key, 15);
                break;
            case ActionType.MouseButton:
                App.Current.Mouse.ButtonDown(action.MouseButton);
                await Task.Delay(15, token).ConfigureAwait(false);
                App.Current.Mouse.ButtonUp(action.MouseButton);
                break;
            case ActionType.MouseWheel:
                App.Current.Mouse.Wheel(action.WheelDelta);
                break;
        }
    }

    private static void EndTurboStep(BindingAction action)
    {
        if (action.Type == ActionType.Key) App.Current.Keyboard.KeyUp(action.Key);
        else if (action.Type == ActionType.MouseButton) App.Current.Mouse.ButtonUp(action.MouseButton);
    }

    private void StopSlotTurbo(string key)
    {
        if (_slotTurbo.Remove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>鼠标按下格子：普通动作立即触发；连发动作按住持续，松开停止。</summary>
    public void PressSlot(int slotIndex, int setIndex)
    {
        var fantasy = CurrentFantasy;
        if (fantasy == null || setIndex < 0 || setIndex >= fantasy.Sets.Count) return;
        if (slotIndex < 0 || slotIndex >= fantasy.Sets[setIndex].Slots.Count) return;
        var id = fantasy.Sets[setIndex].Slots[slotIndex];
        if (string.IsNullOrWhiteSpace(id)) return;
        var item = Library.Items.FirstOrDefault(x => x.Id == id);
        if (item == null) return;

        StopClickTurbo();
        FireSlotTrigger(slotIndex);
        if (item.Action.Turbo && item.Action.Type is ActionType.Key or ActionType.MouseButton or ActionType.MouseWheel)
            StartClickTurbo(slotIndex, item.Action);
        else
            _ = TapAsync(item.Action);
    }

    public void ReleaseSlot(int slotIndex) => StopClickTurbo();

    private void StartClickTurbo(int slotIndex, BindingAction action)
    {
        var cts = new CancellationTokenSource();
        _clickTurbo[slotIndex] = cts;
        _ = Task.Run(async () =>
        {
            var token = cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await TurboStep(action, token);
                    await Task.Delay(Math.Max(15, action.TurboIntervalMs), token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested) FireSlotTrigger(slotIndex);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            finally
            {
                EndTurboStep(action);
            }
        }, cts.Token);
    }

    private void StopClickTurbo()
    {
        foreach (var cts in _clickTurbo.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _clickTurbo.Clear();
    }

    private void Apply(string key, BindingAction? action)
    {
        if (action == null || action.Type == ActionType.Disabled) return;
        if (_held.TryGetValue(key, out var old) && ReferenceEquals(old, action)) return;
        Release(key);
        _held[key] = action;
        switch (action.Type)
        {
            case ActionType.Key:
                foreach (var m in action.Modifiers) App.Current.Keyboard.KeyDown(m);
                App.Current.Keyboard.KeyDown(action.Key);
                break;
            case ActionType.MouseButton:
                App.Current.Mouse.ButtonDown(action.MouseButton);
                break;
            case ActionType.MouseWheel:
                App.Current.Mouse.Wheel(action.WheelDelta);
                break;
            case ActionType.Text:
                App.Current.Keyboard.TypeText(action.Text);
                break;
            case ActionType.Macro:
                _ = PlayMacro(action);
                break;
        }
    }

    private void Release(string key)
    {
        if (!_held.Remove(key, out var action)) return;
        switch (action.Type)
        {
            case ActionType.Key:
                App.Current.Keyboard.KeyUp(action.Key);
                for (var i = action.Modifiers.Count - 1; i >= 0; i--) App.Current.Keyboard.KeyUp(action.Modifiers[i]);
                break;
            case ActionType.MouseButton:
                App.Current.Mouse.ButtonUp(action.MouseButton);
                break;
        }
    }

    private void ReleasePrefix(string prefix)
    {
        foreach (var key in _held.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            Release(key);
        foreach (var key in _slotTurbo.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            StopSlotTurbo(key);
    }

    private async Task PlayMacro(BindingAction action)
    {
        var heldGamepad = new Dictionary<string, BindingAction>();
        foreach (var step in ResolveMacro(action))
        {
            if (!App.Current.Engine.Enabled) return;
            try
            {
                switch (step.Kind)
                {
                    case MacroStepKind.Key:
                        foreach (var m in step.Modifiers) App.Current.Keyboard.KeyDown(m);
                        App.Current.Keyboard.KeyDown(step.Key);
                        await Task.Delay(Math.Max(5, step.HoldMs));
                        App.Current.Keyboard.KeyUp(step.Key);
                        for (var i = step.Modifiers.Count - 1; i >= 0; i--) App.Current.Keyboard.KeyUp(step.Modifiers[i]);
                        break;
                    case MacroStepKind.KeyDown:
                        foreach (var m in step.Modifiers) App.Current.Keyboard.KeyDown(m);
                        App.Current.Keyboard.KeyDown(step.Key);
                        break;
                    case MacroStepKind.KeyUp:
                        App.Current.Keyboard.KeyUp(step.Key);
                        for (var i = step.Modifiers.Count - 1; i >= 0; i--) App.Current.Keyboard.KeyUp(step.Modifiers[i]);
                        break;
                    case MacroStepKind.Delay:
                        await Task.Delay(Math.Max(1, step.DelayMs));
                        continue;
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
                        if (FindBaseAction(step.GamepadSource) is { } bound)
                            await ExecuteMacroAction(bound.Action);
                        break;
                    case MacroStepKind.GamepadDown:
                        if (FindBaseAction(step.GamepadSource) is { } down)
                        {
                            heldGamepad[step.GamepadSource] = down.Action;
                            MacroPress(down.Action);
                        }
                        break;
                    case MacroStepKind.GamepadUp:
                        if (heldGamepad.Remove(step.GamepadSource, out var held))
                            MacroRelease(held);
                        break;
                    case MacroStepKind.Text:
                        App.Current.Keyboard.TypeText(step.Text);
                        break;
                }

                if (step.DelayMs > 0) await Task.Delay(step.DelayMs);
            }
            catch { }
        }
    }

    private static Binding? FindBaseAction(string source) =>
        App.Current.Engine.Profile.FindBinding(source, BindingLayer.Base);

    private static void MacroPress(BindingAction action)
    {
        switch (action.Type)
        {
            case ActionType.Key:
                foreach (var m in action.Modifiers) App.Current.Keyboard.KeyDown(m);
                App.Current.Keyboard.KeyDown(action.Key);
                break;
            case ActionType.MouseButton:
                App.Current.Mouse.ButtonDown(action.MouseButton);
                break;
        }
    }

    private static void MacroRelease(BindingAction action)
    {
        switch (action.Type)
        {
            case ActionType.Key:
                App.Current.Keyboard.KeyUp(action.Key);
                for (var i = action.Modifiers.Count - 1; i >= 0; i--) App.Current.Keyboard.KeyUp(action.Modifiers[i]);
                break;
            case ActionType.MouseButton:
                App.Current.Mouse.ButtonUp(action.MouseButton);
                break;
        }
    }

    private async Task ExecuteMacroAction(BindingAction action)
    {
        switch (action.Type)
        {
            case ActionType.Key:
                MacroPress(action);
                await Task.Delay(30);
                MacroRelease(action);
                break;
            case ActionType.Text:
                App.Current.Keyboard.TypeText(action.Text);
                break;
            case ActionType.MouseButton:
                MacroPress(action);
                await Task.Delay(30);
                MacroRelease(action);
                break;
            case ActionType.MouseWheel:
                App.Current.Mouse.Wheel(action.WheelDelta);
                break;
            case ActionType.Macro:
                await PlayMacro(action);
                break;
        }
    }

    private static List<MacroStep> ResolveMacro(BindingAction action)
    {
        var library = App.Current.Store.MacroLibrary;
        var macro = library.Items.FirstOrDefault(x => x.Id == action.MacroId);
        if (macro == null && !string.IsNullOrWhiteSpace(action.MacroName))
            macro = library.Items.FirstOrDefault(x => x.Name == action.MacroName);
        return macro?.Steps ?? action.Macro;
    }

    private void UpdateLeftStick(BladeReport report, FantasyProfile fantasy)
    {
        var x = report.LeftXNorm;
        var y = report.LeftYNorm;
        var dz = Math.Max(0.1, App.Current.Engine.Profile.Settings.LeftStickDeadzone);
        var mag = MathF.Sqrt(x * x + y * y);
        var desired = new HashSet<string>();
        if (mag >= dz)
        {
            var wasd = fantasy.LeftStickMode == "wasd";
            var ax = Math.Abs(x);
            var ay = Math.Abs(y);
            if (ax >= ay * 0.5f)
                desired.Add(x > 0 ? (wasd ? "D" : "Right") : (wasd ? "A" : "Left"));
            if (ay >= ax * 0.5f)
                desired.Add(y > 0 ? (wasd ? "W" : "Up") : (wasd ? "S" : "Down"));
        }
        foreach (var d in _stickDirections.Except(desired).ToArray())
        {
            App.Current.Keyboard.KeyUp(KeyForStick(d));
            _stickDirections.Remove(d);
        }
        foreach (var d in desired.Except(_stickDirections))
        {
            App.Current.Keyboard.KeyDown(KeyForStick(d));
            _stickDirections.Add(d);
        }
    }

    private static int KeyForStick(string direction) => direction switch
    {
        "W" or "Up" => direction == "W" ? 0x57 : 0x26,
        "S" or "Down" => direction == "S" ? 0x53 : 0x28,
        "A" or "Left" => direction == "A" ? 0x41 : 0x25,
        "D" or "Right" => direction == "D" ? 0x44 : 0x27,
        _ => 0,
    };

    private void MouseTick()
    {
        try
        {
            if (System.Windows.Application.Current is not App app || app.Engine?.Profile == null) return;
            lock (_sync)
            {
                if (!Enabled || _lastReport == null) return;
                // 手柄断联/关机：没有新报文就停止鼠标推进。
                // **不要 ResetLocked**：BLE 上报稀疏时会把连发/按住一起释放。
                if ((DateTime.UtcNow - _lastReportAt).TotalMilliseconds > 2000) return;
                // 手柄可能只在按键变化时上报；长按 RB 的判定不能只依赖报文。
                if (_rbDown && !_selecting && (DateTime.UtcNow - _rbDownAt).TotalMilliseconds > 180)
                {
                    _selecting = true;
                    var pressedFantasy = CurrentFantasy;
                    if (pressedFantasy != null) PushOverlay(pressedFantasy);
                }
                var x = _lastReport.RightXNorm;
                var y = _lastReport.RightYNorm;
                var mag = MathF.Sqrt(x * x + y * y);
                if (mag < 0.15f) return;
                var fantasy = CurrentFantasy;
                if (fantasy == null) return;
                var sensitivity = fantasy.SensitivityLevels.Count == 0
                    ? 1
                    : fantasy.SensitivityLevels[Math.Clamp(_sensitivityIndex, 0, fantasy.SensitivityLevels.Count - 1)];
                if (_wheelMode)
                {
                    // 滚轮与指针共用档位，但单独降低增益，避免高灵敏度档位一下滚很多格。
                    const double wheelGain = 0.12;
                    _wheelAccum += y * sensitivity * wheelGain * 120;
                    var notches = Math.Clamp((int)(_wheelAccum / 120), -1, 1);
                    if (notches != 0)
                    {
                        App.Current.Mouse.Wheel(notches * 120);
                        _wheelAccum -= notches * 120;
                    }
                    return;
                }
                var scale = sensitivity * 900;
                App.Current.Mouse.MoveBy((int)(x * scale * 0.008), (int)(-y * scale * 0.008));
            }
        }
        catch
        {
            // 悬浮窗/配置切换期间可能短暂不可用，忽略本次鼠标推进。
        }
    }

    private static int DirectSetIndex(int mask)
    {
        if ((mask & 8) != 0) return 0;   // Y -> 热键栏 1
        if ((mask & 2) != 0) return 1;   // B -> 2
        if ((mask & 1) != 0) return 2;   // A -> 3
        if ((mask & 4) != 0) return 3;   // X -> 4
        if ((mask & 16) != 0) return 4;  // ↑ -> 5
        if ((mask & 128) != 0) return 5; // → -> 6
        if ((mask & 32) != 0) return 6;  // ↓ -> 7
        if ((mask & 64) != 0) return 7;  // ← -> 8
        return -1;
    }

    private static int NextVisibleSet(FantasyProfile fantasy, int current)
    {
        for (var step = 1; step <= 8; step++)
        {
            var index = (current + step) % 8;
            if (index < fantasy.VisibleSets.Count && fantasy.VisibleSets[index]) return index;
        }
        return current;
    }

    private void SetVirtual(string key, bool pressed)
    {
        if (pressed) _held[key] = new BindingAction { Type = ActionType.Disabled };
        else _held.Remove(key);
    }

    private void ResetLocked()
    {
        _systemMenu.Suspend();
        foreach (var key in _held.Keys.ToArray()) Release(key);
        foreach (var d in _stickDirections.ToArray()) App.Current.Keyboard.KeyUp(KeyForStick(d));
        _stickDirections.Clear();
        _lastReport = null;
        foreach (var key in _slotTurbo.Keys.ToArray()) StopSlotTurbo(key);
        foreach (var key in _baseTurbo.Keys.ToArray()) StopBaseTurbo(key);
        StopClickTurbo();
        _lt = _rt = _activeLt = _activeRt = _extension = _rbDown = _selecting = false;
        _ltSelectDown = false;
        _ltSelectSuppressed = false;
        if (_ltR3MouseDown) App.Current.Mouse.ButtonUp(MouseButtonKind.Left);
        if (_rtR3MouseDown) App.Current.Mouse.ButtonUp(MouseButtonKind.Right);
        _ltR3MouseDown = _rtR3MouseDown = _rbR3Down = _rbR3Used = false;
        _primaryLt = true;
        _lastSelectMask = 0;
        _menuFaceLatch = 0;
    }

    public void RefreshMode()
    {
        lock (_sync)
        {
            if (!Enabled) _overlay?.Hide();
            _overlay?.ReloadFromProfile();
            var fantasy = CurrentFantasy;
            if (fantasy != null) _systemMenu.Refresh(fantasy);
            if (_overlay != null && fantasy != null)
            {
                _uiSet = -1;
                PushOverlay(fantasy);
            }
        }
    }

    /// <summary>从编辑页“当前热键栏”切换运行时热键栏，并同步悬浮窗。</summary>
    public void SelectSet(int index)
    {
        lock (_sync)
        {
            var fantasy = CurrentFantasy;
            if (fantasy == null || fantasy.Sets.Count == 0) return;
            _setIndex = Math.Clamp(index, 0, fantasy.Sets.Count - 1);
            _uiSet = -1;
            PushOverlay(fantasy);
        }
    }

    /// <summary>编辑页内容变化后刷新悬浮窗（不改当前热键栏）。</summary>
    public void NotifyContentChanged()
    {
        lock (_sync)
        {
            var fantasy = CurrentFantasy;
            if (_overlay == null || fantasy == null) return;
            _overlay.ReloadFromProfile();
            _systemMenu.Refresh(fantasy);
            _uiSet = -1;
            PushOverlay(fantasy);
        }
    }

    public void NotifySlotsChanged()
    {
        lock (_sync)
        {
            _overlay?.ReloadFromProfile();
            var fantasy = CurrentFantasy;
            if (fantasy != null) _systemMenu.Refresh(fantasy);
        }
        SlotsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>双十字热键栏的屏幕矩形；未显示时返回 null。</summary>
    public System.Windows.Rect? GetHotbarScreenBounds() => _overlay?.GetHotbarScreenBounds();

    public void ShowOverlay()
    {
        _overlay ??= new FantasyOverlayWindow();
        var fantasy = CurrentFantasy;
        if (fantasy != null && !fantasy.Overlay.Locked && fantasy.Overlay.ClickThrough)
        {
            // 解锁状态用于拖动/点击，自动关闭穿透，避免顶部条拖不动。
            fantasy.Overlay.ClickThrough = false;
            App.Current.Store.Save(App.Current.Engine.Profile);
        }
        _overlay.ReloadFromProfile();
        if (fantasy != null)
        {
            _uiSet = -1;
            PushOverlay(fantasy);
        }
        _overlay.Show();
        _overlay.Activate();
    }

    public void SetOverlayVisible(bool visible)
    {
        var fantasy = CurrentFantasy;
        if (fantasy == null) return;
        if (visible && !fantasy.Enabled) return;
        fantasy.Overlay.Visible = visible;
        App.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            App.Current.Store.Save(App.Current.Engine.Profile);
            ApplyOverlaySettings();
            OverlayVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }));
    }

    public void ToggleOverlay()
    {
        var fantasy = CurrentFantasy;
        if (fantasy is { Enabled: true }) SetOverlayVisible(!fantasy.Overlay.Visible);
    }

    /// <summary>幻想模式开关时的大规则：开启则显示悬浮窗，关闭则隐藏。</summary>
    public void ApplyFantasyModeVisibility(bool enabled)
    {
        var fantasy = CurrentFantasy;
        if (fantasy == null) return;
        if (!enabled) _systemMenu.Suspend();
        fantasy.Overlay.Visible = enabled;
        App.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (enabled) ApplyOverlaySettings();
            else _overlay?.Hide();
            App.Current.Store.Save(App.Current.Engine.Profile);
            OverlayVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }));
    }

    public void Suspend()
    {
        lock (_sync) ResetLocked();
    }

    /// <summary>系统菜单设置/内容变化后刷新运行状态与悬浮窗。</summary>
    public void RefreshSystemMenu()
    {
        lock (_sync)
        {
            var fantasy = CurrentFantasy;
            if (fantasy != null) _systemMenu.Refresh(fantasy);
        }
    }

    public void ApplyOverlaySettings()
    {
        if (_overlay == null)
        {
            if (CurrentFantasy?.Overlay.Visible == true) ShowOverlay();
            return;
        }
        _overlay.ApplySettings();
    }

    /// <summary>只刷新已存在的悬浮窗（锁定/缩放等），不因 Visible 设置去创建窗口。</summary>
    public void ApplyOverlayChrome() => _overlay?.ApplySettings();

    public event EventHandler? OverlayLockChanged;
    public event EventHandler? OverlayVisibilityChanged;
    public event Action<int>? SlotTriggered;
    public event EventHandler? SlotsChanged;

    public void NotifyOverlayLockChanged() => OverlayLockChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>鼠标点击热键栏格子：直接触发该格动作，等同按一次对应手柄组合键。</summary>
    public async void TriggerSlot(int slotIndex, int setIndex)
    {
        try
        {
            var fantasy = CurrentFantasy;
            if (fantasy == null || fantasy.Sets.Count == 0) return;
            if (setIndex < 0 || setIndex >= fantasy.Sets.Count) return;
            var set = fantasy.Sets[setIndex];
            if (slotIndex < 0 || slotIndex >= set.Slots.Count) return;
            var id = set.Slots[slotIndex];
            if (string.IsNullOrWhiteSpace(id)) return;
            var item = Library.Items.FirstOrDefault(x => x.Id == id);
            if (item == null) return;
            FireSlotTrigger(slotIndex);
            await TapAsync(item.Action);
        }
        catch
        {
            // 点击触发失败不应影响界面。
        }
    }

    private async Task TapAsync(BindingAction action)
    {
        switch (action.Type)
        {
            case ActionType.Key:
                foreach (var modifier in action.Modifiers) App.Current.Keyboard.KeyDown(modifier);
                App.Current.Keyboard.KeyDown(action.Key);
                await Task.Delay(70);
                App.Current.Keyboard.KeyUp(action.Key);
                for (var i = action.Modifiers.Count - 1; i >= 0; i--) App.Current.Keyboard.KeyUp(action.Modifiers[i]);
                break;
            case ActionType.MouseButton:
                App.Current.Mouse.ButtonDown(action.MouseButton);
                await Task.Delay(70);
                App.Current.Mouse.ButtonUp(action.MouseButton);
                break;
            case ActionType.MouseWheel:
                App.Current.Mouse.Wheel(action.WheelDelta);
                break;
            case ActionType.Text:
                App.Current.Keyboard.TypeText(action.Text);
                break;
            case ActionType.Macro:
                await PlayMacro(action);
                break;
        }
    }
}

