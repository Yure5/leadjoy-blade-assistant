using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LeadjoyBlade.Core.Mapping;

namespace LeadjoyBlade.App;

public partial class FantasyOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private const int WmDisplayChange = 0x007E;
    private const double SidePad = 14;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    private int _setIndex;
    private int _displaySetIndex;
    private bool _extension;
    private bool _extensionRight;
    private int _renderedSet = -1;
    private int _renderedDisplaySet = -1;
    private bool _renderedExtension;
    private bool _renderedExtensionRight;
    private bool _hasRendered;
    private bool _suppressSave;
    private bool _selectorVisible;
    private double _selectorHeight;
    private Border? _pressedBorder;
    private int _pressedSlot = -1;

    public FantasyOverlayWindow()
    {
        InitializeComponent();
        Hotbar.SlotSize = 56;
        Hotbar.KeyFontSize = 10;
        Hotbar.NameFontSize = 11.5;
        Hotbar.FooterFontSize = 14;
        Hotbar.IdleOpacity = 0.9;
        Hotbar.ShowSlotKeys = false;
        Hotbar.ShowSetBadge = true;
        Hotbar.ShowLock = true;
        Hotbar.LockClicked += Hotbar_OnLockClicked;
        Selector.SlotSize = 56;
        Selector.RenderTransform = new TranslateTransform(0, 20);
        SourceInitialized += (_, _) =>
        {
            ApplyClickThrough();
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(OverlayWndProc);
        };
        LocationChanged += (_, _) => SavePosition();
    }

    public void PlaySlotTrigger(int index) =>
        Dispatcher.BeginInvoke(new Action(() => Hotbar.PlayTrigger(index)));

    /// <summary>双十字热键栏在屏幕上的物理像素矩形；菜单打开时用它挖出点击穿透区。</summary>
    public Rect? GetHotbarScreenBounds()
    {
        if (!IsVisible || Hotbar.ActualWidth <= 0 || Hotbar.ActualHeight <= 0) return null;
        try
        {
            var topLeft = Hotbar.PointToScreen(new Point(0, 0));
            var bottomRight = Hotbar.PointToScreen(new Point(Hotbar.ActualWidth, Hotbar.ActualHeight));
            return new Rect(topLeft, bottomRight);
        }
        catch
        {
            return null;
        }
    }

    private FantasyProfile? CurrentFantasy => App.Current?.Engine?.Profile?.Fantasy;

    public void ReloadFromProfile()
    {
        var fantasy = CurrentFantasy;
        if (fantasy == null || fantasy.Sets.Count == 0) return;
        _setIndex = Math.Clamp(_setIndex, 0, fantasy.Sets.Count - 1);
        BuildSlots();
        Hotbar.SetActive(false, false, false, false);
        ApplySettings();
        ApplySelector(false);
    }

    public void ApplySettings()
    {
        var fantasy = CurrentFantasy;
        if (fantasy == null) return;
        var settings = fantasy.Overlay;
        var locked = settings.Locked;
        Topmost = true;
        Opacity = Math.Clamp(settings.Opacity, 0.0, 1.0);
        var scale = Math.Clamp(settings.Scale, 0.1, 1.5);
        ChromeRow.Height = new GridLength(24); // 锁定也保留透明顶部，给光晕留空间
        DragBar.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        Root.LayoutTransform = new ScaleTransform(scale, scale);
        UpdateWindowSize(scale);

        if (settings.HasPosition)
        {
            _suppressSave = true;
            Left = settings.X - SidePad * scale;
            Top = settings.Y - (_selectorVisible ? _selectorHeight * scale : 0);
            _suppressSave = false;
        }
        else
        {
            var work = SystemParameters.WorkArea;
            _suppressSave = true;
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Bottom - Height - 72;
            _suppressSave = false;
            settings.HasPosition = true;
            settings.X = Left + SidePad * scale;
            settings.Y = Top;
        }

        Hotbar.SetLocked(locked);
        ApplyClickThrough();
        if (settings.Visible)
        {
            if (!IsVisible) Show();
        }
        else if (IsVisible)
        {
            Hide();
        }
        EnsureOnScreen();
    }

    private IntPtr OverlayWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDisplayChange)
            Dispatcher.BeginInvoke(new Action(EnsureOnScreen));
        return IntPtr.Zero;
    }

    /// <summary>屏幕旋转/分辨率变化后，把跑到可视区域外的悬浮窗拉回来。</summary>
    public void EnsureOnScreen()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        if (w <= 0 || h <= 0) return;

        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vr = vl + SystemParameters.VirtualScreenWidth;
        var vb = vt + SystemParameters.VirtualScreenHeight;
        var work = SystemParameters.WorkArea;

        double left, top;
        if (Left + w <= vl || Left >= vr || Top + h <= vt || Top >= vb)
        {
            left = work.Left + (work.Width - w) / 2;
            top = work.Bottom - h - 72;
        }
        else
        {
            left = Math.Clamp(Left, vl, Math.Max(vl, vr - w));
            top = Math.Clamp(Top, vt, Math.Max(vt, vb - h));
        }

        if (Math.Abs(left - Left) < 0.5 && Math.Abs(top - Top) < 0.5) return;
        _suppressSave = true;
        Left = left;
        Top = top;
        _suppressSave = false;
        SavePosition();
    }

    public void UpdateState(FantasyProfile fantasy, int setIndex, bool extension, bool extensionRight,
        bool lt, bool rt, bool selecting)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateState(fantasy, setIndex, extension, extensionRight, lt, rt, selecting)));
            return;
        }
        var displaySet = extension && fantasy.ExtensionEnabled
            ? Math.Clamp(extensionRight ? fantasy.RightExtensionSet : fantasy.LeftExtensionSet, 0, fantasy.Sets.Count - 1)
            : setIndex;
        var structural = !_hasRendered
                         || setIndex != _renderedSet
                         || displaySet != _renderedDisplaySet
                         || extension != _renderedExtension
                         || extensionRight != _renderedExtensionRight;
        _setIndex = setIndex;
        _extension = extension;
        _extensionRight = extensionRight;
        if (structural)
        {
            var recenter = !double.IsNaN(Left) && !double.IsNaN(Top);
            var centerX = Left + Width / 2;
            var centerY = Top + Height / 2;
            BuildSlots();
            UpdateWindowSize(Math.Clamp(fantasy.Overlay.Scale, 0.1, 1.5));
            if (recenter)
            {
                _suppressSave = true;
                Left = centerX - Width / 2;
                Top = centerY - Height / 2;
                _suppressSave = false;
            }
            Hotbar.SetActive(lt, rt, extension && fantasy.ExtensionEnabled, false);
        }
        else
        {
            Hotbar.SetActive(lt, rt, extension && fantasy.ExtensionEnabled, true);
        }
        ApplySelector(selecting);
    }

    private void BuildSlots(bool animate = false, bool fromRight = false)
    {
        var fantasy = CurrentFantasy;
        if (fantasy == null || fantasy.Sets.Count == 0) return;
        _setIndex = Math.Clamp(_setIndex, 0, fantasy.Sets.Count - 1);
        var library = App.Current.Store.FantasyLibrary;
        var extensionPage = _extension && fantasy.ExtensionEnabled;
        var displaySet = extensionPage
            ? Math.Clamp(_extensionRight ? fantasy.RightExtensionSet : fantasy.LeftExtensionSet, 0, fantasy.Sets.Count - 1)
            : _setIndex;
        int? extensionStart = extensionPage ? (_extensionRight ? 8 : 0) : null;
        var set = fantasy.Sets[displaySet];

        Hotbar.Render(set, library, displaySet, extensionStart, fantasy.Overlay.Locked, animate, fromRight);
        foreach (var border in Hotbar.SlotBorders)
        {
            border.Cursor = Cursors.Hand;
            border.AllowDrop = false;
            border.MouseLeftButtonDown += Slot_OnActivate;
            border.MouseLeftButtonUp += Slot_OnRelease;
        }
        _displaySetIndex = displaySet;
        _renderedSet = _setIndex;
        _renderedDisplaySet = displaySet;
        _renderedExtension = _extension;
        _renderedExtensionRight = _extensionRight;
        _hasRendered = true;

        TitleText.Text = extensionPage
            ? $"热键栏 {displaySet + 1} · 扩展页 · {(_extensionRight ? "RT→LT 右半页" : "LT→RT 左半页")}"
            : $"热键栏 {displaySet + 1}";
    }

    private void Slot_OnActivate(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is int index)
        {
            EndSlotPress();
            _pressedBorder = border;
            _pressedSlot = index;
            border.CaptureMouse();
            border.MouseMove += Slot_OnMouseMoveHold;
            border.MouseLeave += Slot_OnMouseLeaveHold;
            App.Current.Fantasy.PressSlot(index, _displaySetIndex);
            e.Handled = true;
        }
    }

    private void Slot_OnMouseMoveHold(object sender, MouseEventArgs e)
    {
        if (_pressedBorder is not Border border) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndSlotPress();
            return;
        }
        var p = e.GetPosition(border);
        if (p.X < 0 || p.Y < 0 || p.X > border.ActualWidth || p.Y > border.ActualHeight)
            EndSlotPress();
    }

    private void Slot_OnMouseLeaveHold(object sender, MouseEventArgs e)
    {
        if (sender == _pressedBorder) EndSlotPress();
    }

    private void Slot_OnRelease(object sender, MouseButtonEventArgs e)
    {
        EndSlotPress();
        e.Handled = true;
    }

    private void EndSlotPress()
    {
        var border = _pressedBorder;
        var index = _pressedSlot;
        _pressedBorder = null;
        _pressedSlot = -1;
        if (border != null)
        {
            border.MouseMove -= Slot_OnMouseMoveHold;
            border.MouseLeave -= Slot_OnMouseLeaveHold;
            border.ReleaseMouseCapture();
        }
        if (index >= 0) App.Current.Fantasy.ReleaseSlot(index);
    }

    private void Hotbar_OnLockClicked(object? sender, EventArgs e)
    {
        var fantasy = CurrentFantasy;
        if (fantasy == null) return;
        var settings = fantasy.Overlay;
        settings.Locked = !settings.Locked;
        if (!settings.Locked)
            settings.ClickThrough = false;
        App.Current.Store.Save(App.Current.Engine.Profile);
        Hotbar.SetLocked(settings.Locked);
        ApplySettings();
        App.Current.Fantasy.NotifyOverlayLockChanged();
    }

    private void ApplySelector(bool show)
    {
        var fantasy = CurrentFantasy;
        var scale = Math.Clamp(fantasy?.Overlay.Scale ?? 1, 0.1, 1.5);
        if (show == _selectorVisible)
        {
            if (show) Selector.UpdateCurrent(_setIndex);
            return;
        }

        Selector.SlotSize = 56;
        if (show)
        {
            Selector.Render(_setIndex);
            Selector.Visibility = Visibility.Visible;
            SelectorRow.Height = GridLength.Auto;
            Selector.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _selectorHeight = Selector.DesiredSize.Height;
        }
        else
        {
            if (_selectorHeight <= 0)
            {
                Selector.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                _selectorHeight = Selector.DesiredSize.Height;
            }
            Selector.Visibility = Visibility.Collapsed;
            SelectorRow.Height = new GridLength(0);
        }

        _selectorVisible = show;
        var bottom = double.IsNaN(Top) ? double.NaN : Top + Height;
        UpdateWindowSize(scale);
        if (!double.IsNaN(bottom))
        {
            _suppressSave = true;
            Top = bottom - Height;
            _suppressSave = false;
        }
        Selector.SetVisible(show, true);
    }

    private void UpdateWindowSize(double scale)
    {
        if (Hotbar.Width <= 0 || Hotbar.Height <= 0) return;
        var chrome = 24;
        var selector = _selectorVisible ? _selectorHeight : 0;
        Width = (Hotbar.Width + 20 + 2 * SidePad) * scale;
        Height = (Hotbar.Height + chrome + selector + 12) * scale;
    }

    private void DragBar_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (CurrentFantasy?.Overlay.Locked == true) return;
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void DragBar_OnMouseUp(object sender, MouseButtonEventArgs e) => SavePosition();

    private void SavePosition()
    {
        if (_suppressSave) return;
        var fantasy = CurrentFantasy;
        if (fantasy == null) return;
        fantasy.Overlay.X = Left + SidePad * Math.Clamp(fantasy.Overlay.Scale, 0.1, 1.5);
        fantasy.Overlay.Y = Top;
        fantasy.Overlay.HasPosition = true;
        App.Current.Store.Save(App.Current.Engine.Profile);
    }

    private void ApplyClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLong(handle, GwlExStyle);
        var clickThrough = CurrentFantasy?.Overlay.ClickThrough != false;
        style |= WsExToolWindow | WsExNoActivate;
        if (clickThrough) style |= WsExTransparent;
        else style &= ~WsExTransparent;
        SetWindowLong(handle, GwlExStyle, style);
    }
}
