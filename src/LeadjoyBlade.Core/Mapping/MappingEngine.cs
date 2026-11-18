using LeadjoyBlade.Core.Input;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.Core.Mapping;

/// <summary>
/// 把 BladeReport 翻译成键盘/鼠标动作。支持：
/// 单键映射、Fn 层、组合键（Chord）、连发、切换、宏、文本、鼠标按键/滚轮、右摇杆鼠标。
/// </summary>
public sealed class MappingEngine : IDisposable
{
    private readonly object _sync = new();
    private readonly KeyboardInjector _keyboard;
    private readonly MouseInjector _mouse;
    private readonly HashSet<string> _activeSources = [];
    private readonly Dictionary<string, BindingAction> _heldActions = [];
    private readonly Dictionary<string, (BindingAction Action, CancellationTokenSource Cts)> _turbo = [];
    private readonly Dictionary<string, CancellationTokenSource> _macros = [];
    private readonly Dictionary<string, BindingAction> _heldGamepad = [];
    private readonly Dictionary<string, PendingBinding> _pending = [];
    private readonly HashSet<string> _activeChords = [];

    private MappingProfile _profile = MappingProfile.CreateDefault();
    private bool _enabled = true;
    private DateTime _lastReportTime = DateTime.UtcNow;
    private DateTime _lastReportAt = DateTime.UtcNow;
    private double _mouseAccumX;
    private double _mouseAccumY;
    private BladeReport? _lastReport;
    private readonly Timer _mouseTimer;
    private MacroLibrary? _macroLibrary;

    public MappingEngine(KeyboardInjector keyboard, MouseInjector mouse, MacroLibrary? macroLibrary = null)
    {
        _keyboard = keyboard;
        _mouse = mouse;
        _macroLibrary = macroLibrary;
        _mouseTimer = new Timer(_ => MouseTick(), null, 8, 8);
    }

    private void MouseTick()
    {
        lock (_sync)
        {
            if (!_enabled || !_profile.Settings.Enabled || _lastReport == null) return;
            // 手柄关机/断联时可能不再来 ConnectionChanged，或者最后一帧摇杆在左上角。
            // 但 **不能在这里 ReleaseAllLocked**：BLE 1B 本来上报就稀疏（日志里常见
            // 250ms~2s 的间隔），一释放就把按键连发/按住状态全打断，表现就是映射
            // 断断续续、LB 只能触发一次。这里只停掉摇杆鼠标；真正的断联由
            // OnDisconnected() 立即释放。
            if ((DateTime.UtcNow - _lastReportAt).TotalMilliseconds > 2000)
            {
                _lastReport = null;
                _mouseAccumX = 0;
                _mouseAccumY = 0;
                return;
            }
            UpdateMouse(_lastReport, 0.008);
        }
    }

    /// <summary>手柄断联：立即停掉摇杆鼠标并释放所有按键/鼠标，不等下一帧。</summary>
    public void OnDisconnected()
    {
        lock (_sync)
        {
            _lastReport = null;
            _mouseAccumX = 0;
            _mouseAccumY = 0;
            _lastReportAt = DateTime.UtcNow;
            ReleaseAllLocked();
        }
    }

    public event EventHandler<(string Source, bool Pressed)>? SourceChanged;
    public event EventHandler<string>? ActionFired;
    public event EventHandler<string>? Log;

    public MappingProfile Profile => _profile;

    public MacroLibrary? MacroLibrary
    {
        get => _macroLibrary;
        set => _macroLibrary = value;
    }

    private List<MacroStep> ResolveMacro(BindingAction action)
    {
        var macro = _macroLibrary?.Items.FirstOrDefault(x => x.Id == action.MacroId);
        if (macro == null && !string.IsNullOrWhiteSpace(action.MacroName))
            macro = _macroLibrary?.Items.FirstOrDefault(x => x.Name == action.MacroName);
        return macro?.Steps ?? action.Macro;
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value)
            {
                _lastReport = null;
                ReleaseAll();
            }
        }
    }

    public void SetProfile(MappingProfile profile)
    {
        lock (_sync)
        {
            ReleaseAllLocked();
            _profile = profile;
            _lastReport = null;
        }
    }

    public void Update(BladeReport report)
    {
        lock (_sync)
        {
            if (!_enabled || !_profile.Settings.Enabled)
            {
                if (_activeSources.Count > 0) ReleaseAllLocked();
                return;
            }

            var now = report.Timestamp;
            var dt = Math.Clamp((now - _lastReportTime).TotalSeconds, 0.001, 0.1);
            _lastReportTime = now;

            var sources = ComputeSources(report);
            _lastReport = report;
            _lastReportAt = DateTime.UtcNow;

            // 1) 组合键优先
            foreach (var chord in _profile.Chords.Where(c => c.Enabled).OrderByDescending(c => c.Sources.Count))
            {
                if (chord.Sources.Count < 2) continue;
                if (!chord.Sources.All(sources.Contains))
                {
                    if (_activeChords.Contains(chord.Name)) ReleaseChordLocked(chord);
                    continue;
                }

                if (_activeChords.Add(chord.Name))
                {
                    foreach (var s in chord.Sources) CancelPendingLocked(s);
                    ApplyDownLocked($"chord:{chord.Name}", chord.Action, isChord: true);
                    ActionFired?.Invoke(this, $"组合键 {chord.Name} → {chord.Action.Describe()}");
                }
            }

            // 2) 释放
            foreach (var source in _activeSources.Where(s => !sources.Contains(s)).ToArray())
            {
                CancelPendingLocked(source);
                ApplyUpLocked(source);
                SourceChanged?.Invoke(this, (source, false));
            }

            // 3) 按下
            var layer = sources.Contains(nameof(BladeButtons.Fn)) ? BindingLayer.Fn : BindingLayer.Base;
            foreach (var source in sources.Where(s => !_activeSources.Contains(s)))
            {
                SourceChanged?.Invoke(this, (source, true));
                if (_activeChords.Any(c => _profile.Chords.FirstOrDefault(x => x.Name == c)?.Sources.Contains(source) == true))
                    continue;

                var binding = _profile.Bindings.FirstOrDefault(b => b.Enabled && b.Source == source && b.Action.Type == ActionType.Macro)
                              ?? _profile.FindBinding(source, layer)
                              ?? _profile.FindBinding(source, BindingLayer.Base);
                if (binding == null) continue;

                // 组合键窗口：若该键参与某个组合键，延迟 45ms 再执行单键动作
                if (ChordMayCompleteLater(source, sources))
                {
                    SchedulePendingLocked(source, binding);
                }
                else
                {
                    ApplyDownLocked(source, binding.Action, isChord: false);
                }
            }

            _activeSources.Clear();
            foreach (var s in sources) _activeSources.Add(s);

            // 4) 右摇杆/左摇杆鼠标由固定频率计时器持续处理
        }
    }

    private HashSet<string> ComputeSources(BladeReport report)
    {
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var button in BladeButtonsExtensions.All)
            if (report.IsPressed(button)) sources.Add(button.ToString());

        if (report.IsPressed(BladeButtons.L2)) sources.Add("L2");
        if (report.IsPressed(BladeButtons.R2)) sources.Add("R2");

        var settings = _profile.Settings;
        AddStickSources(sources, "LS", report.LeftXNorm,
            settings.InvertLeftY ? -report.LeftYNorm : report.LeftYNorm, settings.LeftStickDeadzone);
        AddStickSources(sources, "RS", report.RightXNorm,
            settings.InvertRightY ? -report.RightYNorm : report.RightYNorm, settings.RightStickDeadzone);

        if (settings.LeftTriggerAsButton && report.LeftTriggerNorm > 0.5f) sources.Add("L2");
        if (settings.RightTriggerAsButton && report.RightTriggerNorm > 0.5f) sources.Add("R2");

        return sources;
    }

    private void AddStickSources(HashSet<string> sources, string prefix, float x, float y, double deadzone)
    {
        var dz = (float)Math.Clamp(deadzone, 0.05, 0.9);
        var ax = Math.Abs(x);
        var ay = Math.Abs(y);
        // 先按整体半径判定死区
        if (MathF.Sqrt(x * x + y * y) < dz) return;

        // 固定角度扇区：水平 ±38°，斜向 38°–58°，垂直 58°–122°
        if (ax >= ay * 1.28f)
        {
            sources.Add(x > 0 ? $"{prefix}_RIGHT" : $"{prefix}_LEFT");
            return;
        }
        if (ay >= ax * 1.60f)
        {
            sources.Add(y > 0 ? $"{prefix}_UP" : $"{prefix}_DOWN");
            return;
        }

        // 接近正斜向：同时按下两个方向键
        sources.Add(x > 0 ? $"{prefix}_RIGHT" : $"{prefix}_LEFT");
        sources.Add(y > 0 ? $"{prefix}_UP" : $"{prefix}_DOWN");
    }

    private bool ChordMayCompleteLater(string source, HashSet<string> current)
    {
        foreach (var chord in _profile.Chords.Where(c => c.Enabled))
        {
            if (!chord.Sources.Contains(source)) continue;
            if (chord.Sources.All(current.Contains)) return false;
            return true;
        }
        return false;
    }

    private void SchedulePendingLocked(string source, Binding binding)
    {
        CancelPendingLocked(source);
        var cts = new CancellationTokenSource();
        var timer = new Timer(_ =>
        {
            lock (_sync)
            {
                if (!_pending.TryGetValue(source, out var pending) || pending.Cts != cts) return;
                _pending.Remove(source);
                ApplyDownLocked(source, binding.Action, isChord: false);
            }
        }, null, 45, Timeout.Infinite);
        _pending[source] = new PendingBinding(binding, cts, timer);
    }

    private void CancelPendingLocked(string source)
    {
        if (_pending.TryGetValue(source, out var pending))
        {
            pending.Timer.Dispose();
            pending.Cts.Cancel();
            _pending.Remove(source);
        }
    }

    private void ApplyDownLocked(string key, BindingAction action, bool isChord)
    {
        switch (action.Type)
        {
            case ActionType.Disabled:
                return;
            case ActionType.Key:
                if (action.Toggle)
                {
                    if (_keyboard.IsKeyDown(action.Key))
                    {
                        _keyboard.KeyUp(action.Key);
                        for (var i = action.Modifiers.Count - 1; i >= 0; i--) _keyboard.KeyUp(action.Modifiers[i]);
                    }
                    else
                    {
                        foreach (var modifier in action.Modifiers) _keyboard.KeyDown(modifier);
                        _keyboard.KeyDown(action.Key);
                    }
                    return;
                }
                if (action.Turbo)
                {
                    StartTurbo(key, action);
                    return;
                }
                foreach (var modifier in action.Modifiers) _keyboard.KeyDown(modifier);
                _keyboard.KeyDown(action.Key);
                _heldActions[key] = action;
                ActionFired?.Invoke(this, action.Describe());
                return;
            case ActionType.MouseButton:
                if (action.MouseUsePosition)
                    MacroMouse.MoveTo(MousePositionStep(action), _mouse, _profile.ProcessName);
                _mouse.ButtonDown(action.MouseButton);
                _heldActions[key] = action;
                ActionFired?.Invoke(this, action.Describe());
                return;
            case ActionType.MouseWheel:
                if (action.MouseUsePosition)
                    MacroMouse.MoveTo(MousePositionStep(action), _mouse, _profile.ProcessName);
                _mouse.Wheel(action.WheelDelta);
                ActionFired?.Invoke(this, action.Describe());
                return;
            case ActionType.Text:
                _keyboard.TypeText(action.Text);
                ActionFired?.Invoke(this, action.Describe());
                return;
            case ActionType.Macro:
                StartMacro(key, action);
                ActionFired?.Invoke(this, action.Describe());
                return;
        }
    }

    private void ApplyUpLocked(string key)
    {
        CancelPendingLocked(key);
        StopTurboLocked(key);
        StopMacroLocked(key);

        if (!_heldActions.Remove(key, out var action)) return;
        switch (action.Type)
        {
            case ActionType.Key:
                _keyboard.KeyUp(action.Key);
                for (var i = action.Modifiers.Count - 1; i >= 0; i--) _keyboard.KeyUp(action.Modifiers[i]);
                break;
            case ActionType.MouseButton:
                _mouse.ButtonUp(action.MouseButton);
                break;
        }
    }

    private static MacroStep MousePositionStep(BindingAction action) => new()
    {
        MouseUsePosition = action.MouseUsePosition,
        MouseX = action.MouseX,
        MouseY = action.MouseY,
        MouseScreen = action.MouseScreen,
        MouseMonitor = action.MouseMonitor,
        MouseMonitorX = action.MouseMonitorX,
        MouseMonitorY = action.MouseMonitorY,
        MouseMonitorWidth = action.MouseMonitorWidth,
        MouseMonitorHeight = action.MouseMonitorHeight,
    };

    private void ReleaseChordLocked(ChordBinding chord)
    {
        if (!_activeChords.Remove(chord.Name)) return;
        ApplyUpLocked($"chord:{chord.Name}");
    }

    private void StartTurbo(string key, BindingAction action)
    {
        StopTurboLocked(key);
        var cts = new CancellationTokenSource();
        _turbo[key] = (action, cts);
        _ = Task.Run(async () =>
        {
            var token = cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    _keyboard.Chord(action.Modifiers, action.Key, 15);
                    await Task.Delay(Math.Max(15, action.TurboIntervalMs), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _keyboard.KeyUp(action.Key);
            }
        }, cts.Token);
    }

    private void StopTurboLocked(string key)
    {
        if (_turbo.Remove(key, out var entry))
        {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }
    }

    private void StartMacro(string key, BindingAction action)
    {
        StopMacroLocked(key);
        var cts = new CancellationTokenSource();
        _macros[key] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var step in ResolveMacro(action))
                {
                    cts.Token.ThrowIfCancellationRequested();
                    switch (step.Kind)
                    {
                        case MacroStepKind.Delay:
                            await Task.Delay(Math.Max(1, step.DelayMs), cts.Token).ConfigureAwait(false);
                            break;
                        case MacroStepKind.KeyDown:
                            foreach (var modifier in step.Modifiers) _keyboard.KeyDown(modifier);
                            _keyboard.KeyDown(step.Key);
                            if (step.DelayMs > 0) await Task.Delay(step.DelayMs, cts.Token).ConfigureAwait(false);
                            break;
                        case MacroStepKind.KeyUp:
                            _keyboard.KeyUp(step.Key);
                            for (var i = step.Modifiers.Count - 1; i >= 0; i--) _keyboard.KeyUp(step.Modifiers[i]);
                            if (step.DelayMs > 0) await Task.Delay(step.DelayMs, cts.Token).ConfigureAwait(false);
                            break;
                        case MacroStepKind.Key:
                            foreach (var modifier in step.Modifiers) _keyboard.KeyDown(modifier);
                            _keyboard.KeyDown(step.Key);
                            await Task.Delay(Math.Max(5, step.HoldMs), cts.Token).ConfigureAwait(false);
                            _keyboard.KeyUp(step.Key);
                            for (var i = step.Modifiers.Count - 1; i >= 0; i--) _keyboard.KeyUp(step.Modifiers[i]);
                            if (step.DelayMs > 0) await Task.Delay(step.DelayMs, cts.Token).ConfigureAwait(false);
                            break;
                        case MacroStepKind.MouseDown:
                            if (step.MouseUsePosition)
                                MacroMouse.MoveTo(step, _mouse, _profile.ProcessName);
                            _mouse.ButtonDown(step.MouseButton);
                            if (step.DelayMs > 0) await Task.Delay(step.DelayMs, cts.Token).ConfigureAwait(false);
                            break;
                        case MacroStepKind.MouseUp:
                            if (step.MouseUsePosition)
                                MacroMouse.MoveTo(step, _mouse, _profile.ProcessName);
                            _mouse.ButtonUp(step.MouseButton);
                            if (step.DelayMs > 0) await Task.Delay(step.DelayMs, cts.Token).ConfigureAwait(false);
                            break;
                        case MacroStepKind.MouseButton:
                            if (step.MouseUsePosition &&
                                MacroMouse.Click(step, _mouse, _profile.ProcessName, Math.Max(5, step.HoldMs)))
                            {
                                if (step.DelayMs > 0) await Task.Delay(step.DelayMs, cts.Token).ConfigureAwait(false);
                                break;
                            }
                            _mouse.ButtonDown(step.MouseButton);
                            await Task.Delay(Math.Max(5, step.HoldMs), cts.Token).ConfigureAwait(false);
                            _mouse.ButtonUp(step.MouseButton);
                            if (step.DelayMs > 0) await Task.Delay(step.DelayMs, cts.Token).ConfigureAwait(false);
                            break;
                        case MacroStepKind.Gamepad:
                            var bound = _profile.FindBinding(step.GamepadSource, BindingLayer.Base);
                            if (bound != null)
                                await ExecuteActionAsync(bound.Action, cts.Token).ConfigureAwait(false);
                            if (step.DelayMs > 0) await Task.Delay(step.DelayMs, cts.Token).ConfigureAwait(false);
                            break;
                        case MacroStepKind.GamepadDown:
                            PressGamepad(step.GamepadSource);
                            if (step.DelayMs > 0) await Task.Delay(step.DelayMs, cts.Token).ConfigureAwait(false);
                            break;
                        case MacroStepKind.GamepadUp:
                            ReleaseGamepad(step.GamepadSource);
                            if (step.DelayMs > 0) await Task.Delay(step.DelayMs, cts.Token).ConfigureAwait(false);
                            break;
                        case MacroStepKind.Text:
                            _keyboard.TypeText(step.Text);
                            if (step.DelayMs > 0) await Task.Delay(step.DelayMs, cts.Token).ConfigureAwait(false);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"宏执行失败：{ex.Message}");
            }
            finally
            {
                lock (_sync) _macros.Remove(key);
            }
        }, cts.Token);
    }

    private async Task ExecuteActionAsync(BindingAction action, CancellationToken token)
    {
        switch (action.Type)
        {
            case ActionType.Key:
                foreach (var modifier in action.Modifiers) _keyboard.KeyDown(modifier);
                _keyboard.KeyDown(action.Key);
                await Task.Delay(30, token).ConfigureAwait(false);
                _keyboard.KeyUp(action.Key);
                for (var i = action.Modifiers.Count - 1; i >= 0; i--) _keyboard.KeyUp(action.Modifiers[i]);
                break;
            case ActionType.Text:
                _keyboard.TypeText(action.Text);
                break;
            case ActionType.MouseButton:
                _mouse.ButtonDown(action.MouseButton);
                await Task.Delay(30, token).ConfigureAwait(false);
                _mouse.ButtonUp(action.MouseButton);
                break;
            case ActionType.MouseWheel:
                _mouse.Wheel(action.WheelDelta);
                break;
        }
    }

    private void PressGamepad(string source)
    {
        var binding = _profile.FindBinding(source, BindingLayer.Base);
        if (binding == null) return;
        _heldGamepad[source] = binding.Action;
        switch (binding.Action.Type)
        {
            case ActionType.Key:
                foreach (var modifier in binding.Action.Modifiers) _keyboard.KeyDown(modifier);
                _keyboard.KeyDown(binding.Action.Key);
                break;
            case ActionType.MouseButton:
                _mouse.ButtonDown(binding.Action.MouseButton);
                break;
        }
    }

    private void ReleaseGamepad(string source)
    {
        if (!_heldGamepad.Remove(source, out var action)) return;
        switch (action.Type)
        {
            case ActionType.Key:
                _keyboard.KeyUp(action.Key);
                for (var i = action.Modifiers.Count - 1; i >= 0; i--) _keyboard.KeyUp(action.Modifiers[i]);
                break;
            case ActionType.MouseButton:
                _mouse.ButtonUp(action.MouseButton);
                break;
        }
    }

    private void StopMacroLocked(string key)
    {
        if (_macros.Remove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void UpdateMouse(BladeReport report, double dt)
    {
        var settings = _profile.Settings;
        if (!settings.RightStickAsMouse && !settings.LeftStickAsMouse) return;

        var x = settings.LeftStickAsMouse ? report.LeftXNorm : report.RightXNorm;
        var y = settings.LeftStickAsMouse ? report.LeftYNorm : report.RightYNorm;
        if (settings.LeftStickAsMouse ? settings.InvertLeftY : settings.InvertRightY) y = -y;

        // 摇杆作为一个整体：径向死区，保留方向
        var magnitude = Math.Sqrt(x * x + y * y);
        var deadzone = settings.MouseDeadzonePercent / 100.0;
        if (magnitude <= deadzone) return;

        var normalized = (magnitude - deadzone) / Math.Max(0.01, 1 - deadzone);
        normalized = Math.Clamp(normalized, 0, 1);
        var speed = settings.MouseSensitivity * 1200 * normalized;
        speed *= 1 + Math.Max(0, settings.MouseAcceleration) * normalized * normalized * 2;

        var dirX = x / magnitude;
        var dirY = y / magnitude;
        _mouseAccumX += dirX * speed * dt;
        _mouseAccumY += -dirY * speed * dt;
        var dx = (int)_mouseAccumX;
        var dy = (int)_mouseAccumY;
        if (dx == 0 && dy == 0) return;
        _mouseAccumX -= dx;
        _mouseAccumY -= dy;
        _mouse.MoveBy(dx, dy);
    }

    public void ReleaseAll()
    {
        lock (_sync) ReleaseAllLocked();
    }

    private void ReleaseAllLocked()
    {
        foreach (var source in _activeSources.ToArray()) ApplyUpLocked(source);
        foreach (var chord in _activeChords.ToArray())
        {
            var definition = _profile.Chords.FirstOrDefault(c => c.Name == chord);
            if (definition != null) ReleaseChordLocked(definition);
        }
        _activeSources.Clear();
        _activeChords.Clear();
        _heldActions.Clear();
        foreach (var pending in _pending.Values)
        {
            pending.Timer.Dispose();
            pending.Cts.Cancel();
        }
        _pending.Clear();
        foreach (var turbo in _turbo.Values)
        {
            turbo.Cts.Cancel();
            turbo.Cts.Dispose();
        }
        _turbo.Clear();
        foreach (var macro in _macros.Values)
        {
            macro.Cancel();
            macro.Dispose();
        }
        _macros.Clear();
        foreach (var source in _heldGamepad.Keys.ToArray()) ReleaseGamepad(source);
        _keyboard.ReleaseAll();
        _mouse.ReleaseAll();
    }

    public void Dispose()
    {
        _mouseTimer.Dispose();
        ReleaseAll();
    }

    private sealed record PendingBinding(Binding Binding, CancellationTokenSource Cts, Timer Timer);
}

