using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using LeadjoyBlade.Core.Input;
using LeadjoyBlade.Core.Mapping;

namespace LeadjoyBlade.App;

/// <summary>
/// 系统菜单悬浮窗。样式参考 FF14 快捷指令：一条横向菜单栏，选中菜单栏的正下方纵向排列项目。
/// 窗口不激活、鼠标穿透，只接收由手柄驱动的运行时状态。
/// </summary>
public partial class SystemMenuOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int SwpNoActivate = 0x0010;
    private const int SwpShowWindow = 0x0040;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    private const double RowHeight = 23;
    private const double RowGap = 2;
    private const double RowStep = RowHeight + RowGap;
    private const double CategoryFadeMs = 100;
    private const double BandTop = 50;
    private const double BandHeight = 78;
    // 选中框固定在第二行并贴着蓝条下沿：ListTop + RowStep = BandTop + BandHeight
    private const double ListTop = BandTop + BandHeight - RowStep; // BandHeight 85→78 后为 103

    private static readonly SolidColorBrush Gold = Frozen("#C8A46A");
    private static readonly SolidColorBrush MenuGold = Frozen("#F8FBFF");
    private static readonly SolidColorBrush MenuGoldBright = Frozen("#FFFFFFFF");
    private static readonly SolidColorBrush TabBlueText = Frozen("#F8FBFF");
    private static readonly SolidColorBrush GoldEdgeFill = Frozen("#FFD76A");
    private static readonly SolidColorBrush BlueEdgeFill = Frozen("#7FB8FF");
    private static readonly SolidColorBrush Muted = Frozen("#AFC0D2");
    private static readonly SolidColorBrush Dim = Frozen("#7E93A8");
    private static readonly Brush SelectedItem = CreateSelectedGradient();
    private static readonly Brush SelectionMask = CreateSelectionMask();

    private static readonly SolidColorBrush TabBorderIdle = Frozen("#8096B4D0");
    private static readonly SolidColorBrush InnerWhiteLine = Frozen("#FFFFFFFF");

    private static readonly DropShadowEffect TabBlueGlow = CreateGlow("#6FA8FF", 16, 0.7);
    private static readonly DropShadowEffect TabGoldGlow = CreateGlow("#F6D98E", 16, 0.7);
    private static readonly DropShadowEffect TextBlueGlow = CreateGlow("#6FA8FF", 11, 0.75);
    private static readonly DropShadowEffect TextGlow = CreateGlow("#FFD76A", 10, 1.0);
    private static readonly DropShadowEffect SelectedTextGlow = CreateGlow("#FFE08A", 10, 1.0);
    private static readonly BlurEffect SoftEdgeBlur = CreateBlur(1.2);
    private static readonly DropShadowEffect TabInnerWhiteGlow = CreateGlow("#FFFFFF", 5, 0.7);
    private static readonly DropShadowEffect ItemShadow = CreateShadow(12, 3, 270, 0.7);

    private readonly List<FrameworkElement> _tabBorders = [];
    private readonly List<double> _tabOffsets = [];
    private readonly List<TextBlock> _itemTexts = [];
    private double _tabTotalWidth;
    private FantasyProfile? _fantasy;
    private int _categoryIndex;
    private int _itemIndex;
    private int _renderedCategoryIndex = -1;
    private string _hotkey = "SELECT";
    private double _itemWidth = 230;
    private Border? _selectionBar;
    private Grid? _selectionHost;
    private ScaleTransform? _selectionScale;

    private sealed class ItemHost
    {
        public Grid Group = null!;
        public Canvas SelectionLayer = null!;
        public Grid MaskHost = null!;
        public Canvas ItemsLayer = null!;
        public Grid ItemsHost = null!;
        public Canvas ItemsCanvas = null!;
        public TranslateTransform Transform { get; } = new();
    }

    private ItemHost _hostA = null!, _hostB = null!, _live = null!, _build = null!;
    private bool _buildingSpare;
    private int _hostSwapToken;
    private DispatcherTimer? _fadeTimer;
    private bool _appearing;
    private bool _wantVisible;
    private bool _snapListOnce;
    private readonly List<(FrameworkElement Element, Effect Effect)> _fadeEffects = [];
    private readonly List<(FrameworkElement Element, Effect Effect)> _categoryEffects = [];
    private readonly DispatcherTimer _effectRestoreTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };

    /// <summary>菜单打开时用户点击鼠标，请求关闭菜单。</summary>
    public event EventHandler? DismissRequested;

    /// <summary>屏幕坐标下允许点击穿透的区域（双十字热键栏），其余区域点击退出菜单。</summary>
    public Rect? ClickThroughHole { get; set; }

    private bool _interactive;
    private bool _styleApplied;
    private Rect? _windowBounds;
    private Rect? _appliedBounds;
    private double _shadowTopExtension;
    private const double MaxShadowTopExtension = 200;

    private ItemHost Current => _buildingSpare ? _build : _live;
    private Grid ItemsGroup => Current.Group;
    private Canvas SelectionLayer => Current.SelectionLayer;
    private Grid ItemsMaskHost => Current.MaskHost;
    private Canvas ItemsLayer => Current.ItemsLayer;
    private Grid ItemsHost => Current.ItemsHost;
    private Canvas ItemsCanvas => Current.ItemsCanvas;
    private TranslateTransform _itemsTransform => Current.Transform;

    public SystemMenuOverlayWindow()
    {
        InitializeComponent();
        _hostA = new ItemHost
        {
            Group = ItemsGroupA,
            SelectionLayer = SelectionLayerA,
            MaskHost = ItemsMaskHostA,
            ItemsLayer = ItemsLayerA,
            ItemsHost = ItemsHostA,
            ItemsCanvas = ItemsCanvasA,
        };
        _hostB = new ItemHost
        {
            Group = ItemsGroupB,
            SelectionLayer = SelectionLayerB,
            MaskHost = ItemsMaskHostB,
            ItemsLayer = ItemsLayerB,
            ItemsHost = ItemsHostB,
            ItemsCanvas = ItemsCanvasB,
        };
        _hostA.ItemsHost.RenderTransform = _hostA.Transform;
        _hostB.ItemsHost.RenderTransform = _hostB.Transform;
        _live = _hostA;
        _build = _hostB;
        ItemsGroupB.Opacity = 0;
        _effectRestoreTimer.Tick += (_, _) =>
        {
            _effectRestoreTimer.Stop();
            RestoreEffects();
            RestoreEffectsFrom(_categoryEffects);
        };
        SourceInitialized += (_, _) => SetInteractive(false);
        SizeChanged += (_, _) => { if (_interactive) ApplyInputRegion(); };
        LocationChanged += (_, _) => { if (_interactive) ApplyInputRegion(); };
        PreviewMouseDown += (_, _) =>
        {
            if (IsVisible) DismissRequested?.Invoke(this, EventArgs.Empty);
        };
        SizeChanged += (_, _) =>
        {
            UpdateItemsClip();
            UpdateItemsPosition();
        };
    }

    /// <summary>显示或刷新菜单内容；必须在 UI 线程调用。</summary>
    public void ShowMenu(FantasyProfile fantasy, int categoryIndex, int itemIndex, string hotkey)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ShowMenu(fantasy, categoryIndex, itemIndex, hotkey)));
            return;
        }

        var appearing = !IsVisible || !_wantVisible;
        _appearing = appearing;
        _wantVisible = true;
        _fantasy = fantasy;
        _hotkey = hotkey;
        _categoryIndex = Math.Clamp(categoryIndex, 0, Math.Max(0, fantasy.SystemMenuCategories.Count - 1));
        _itemIndex = itemIndex;
        Render();
        UpdateLayout();
        PositionWindow();
        if (appearing)
        {
            // 每次呼出都重放淡入：上一次动画默认 HoldEnd 停在 1，
            // 不清掉的话新动画会从 1 开始，二次打开就变成瞬间出现。
            Root.BeginAnimation(OpacityProperty, null);
            Root.Opacity = 0;
            Root.RenderTransform = null;
            // 先摘特效再显示：首帧不用光栅化 DropShadow/Blur，避免 60~90ms 的阻塞把淡入压跳。
            SuspendEffects(Root);
            if (!IsVisible) Show();
            ApplyTargetBounds();
            StartFadeIn();
        }
        else
        {
            if (!IsVisible) Show();
            ApplyTargetBounds();
        }
        // 点击由独立的全屏捕获层处理，菜单本身保持鼠标穿透。
        SetInteractive(false);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateItemsPosition();
            _appearing = false;
        }));
    }

    /// <summary>
    /// 等窗口首帧合成（建 CompositionTarget、光栅化特效，本机约 60~90ms）结束后再淡入。
    /// 否则淡入动画的时钟在首帧阻塞期间照走，动画会被压成两跳，看起来像抖动两次。
    /// </summary>
    private void StartFadeIn()
    {
        _fadeTimer?.Stop();
        _fadeTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        _fadeTimer.Tick += (_, _) =>
        {
            _fadeTimer?.Stop();
            _fadeTimer = null;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            fade.Completed += (_, _) => RestoreEffects();
            _effectRestoreTimer.Stop();
            _effectRestoreTimer.Interval = TimeSpan.FromMilliseconds(170);
            _effectRestoreTimer.Start();
            Root.BeginAnimation(OpacityProperty, fade);
        };
        _fadeTimer.Start();
    }

    private void SuspendEffects(DependencyObject node)
    {
        RestoreEffects();
        SuspendEffectsInto(node, _fadeEffects);
    }

    private static void SuspendEffectsInto(DependencyObject node, List<(FrameworkElement Element, Effect Effect)> list)
    {
        if (node is FrameworkElement fe && fe.Effect is { } effect)
        {
            list.Add((fe, effect));
            fe.Effect = null;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            SuspendEffectsInto(VisualTreeHelper.GetChild(node, i), list);
    }

    private static void RestoreEffectsFrom(List<(FrameworkElement Element, Effect Effect)> list)
    {
        foreach (var (element, effect) in list) element.Effect = effect;
        list.Clear();
    }

    private void RestoreEffects() => RestoreEffectsFrom(_fadeEffects);

    public void HideMenu()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(HideMenu));
            return;
        }
        _fadeTimer?.Stop();
        _fadeTimer = null;
        _effectRestoreTimer.Stop();
        _wantVisible = false;
        _appearing = false;
        SetInteractive(false);
        // 关闭时做 160ms 淡出，再停留在全透明；窗口本身不 Hide（避免下次 Show 贴出旧帧）。
        var from = Root.Opacity;
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 0;
        if (from > 0.01)
        {
            SuspendEffects(Root);
            var fadeOut = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            fadeOut.Completed += (_, _) => RestoreEffects();
            Root.BeginAnimation(OpacityProperty, fadeOut);
        }
        else
        {
            RestoreEffects();
        }
        _fantasy = null;
        _selectionBar = null;
        _selectionHost = null;
        _selectionScale = null;
        _renderedCategoryIndex = -1;
    }

    private void Render()
    {
        if (_fantasy == null) return;
        var categories = _fantasy.SystemMenuCategories;
        if (categories.Count == 0)
        {
            _categoryIndex = 0;
            _itemIndex = 0;
        }
        else
        {
            _categoryIndex = Math.Clamp(_categoryIndex, 0, categories.Count - 1);
        }

        HintText.Text = $"{_hotkey} 呼出  ·  ←→ 菜单栏  ·  ↑↓ 项目  ·  "
                        + $"{_fantasy.SystemMenuConfirmButton} 确认  ·  {_fantasy.SystemMenuCancelButton} 关闭";
        var categoryChanged = _renderedCategoryIndex >= 0 && _renderedCategoryIndex != _categoryIndex;
        RenderTabs(categories);
        RenderItems(categories, snap: _appearing || categoryChanged, crossfade: categoryChanged && _wantVisible);
        // 首次显示时 ActualHeight 可能还是 0；等布局完成后再应用渐变遮罩。
        Dispatcher.BeginInvoke(new Action(UpdateItemsClip), DispatcherPriority.Loaded);
    }

    private void RenderTabs(List<SystemMenuCategory> categories)
    {
        var animateSelection = _renderedCategoryIndex >= 0 && _renderedCategoryIndex != _categoryIndex;
        TabPanel.Children.Clear();
        _tabBorders.Clear();
        _tabOffsets.Clear();
        _tabTotalWidth = 0;
        if (categories.Count == 0)
        {
            _renderedCategoryIndex = -1;
            TabPanel.Children.Add(new TextBlock
            {
                Text = "尚未配置菜单栏",
                Foreground = Muted,
                FontSize = 11.5,
                Margin = new Thickness(4, 4, 0, 4),
            });
            return;
        }

        for (var i = 0; i < categories.Count; i++)
        {
            var selected = i == _categoryIndex;
            var labelText = categories[i].Name;
            var label = new TextBlock
            {
                Text = labelText,
                Foreground = selected ? MenuGoldBright : TabBlueText,
                FontSize = 11,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(15.6, 3, 15.6, 3),
                Effect = selected ? SelectedTextGlow : TextBlueGlow,
            };
            // 发光只作用在胶囊边框上，文字单独绘制，避免文字被边框发光糊掉。
            var frame = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = selected ? Gold : TabBorderIdle,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Effect = selected ? TabGoldGlow : TabBlueGlow,
            };
            var innerGlow = new Border
            {
                BorderBrush = InnerWhiteLine,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(9),
                Effect = TabInnerWhiteGlow,
                IsHitTestVisible = false,
            };
            var slide = new TranslateTransform(0, selected && !animateSelection ? -10 : 0);
            var tab = new Grid
            {
                MinWidth = 101,
                Margin = new Thickness(0, 0, 8, 0),
                RenderTransform = slide,
            };
            var edge = new TextBlock
            {
                Text = labelText,
                Foreground = selected ? GoldEdgeFill : BlueEdgeFill,
                FontSize = 11,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(15.6, 3, 15.6, 3),
                Effect = SoftEdgeBlur,
                IsHitTestVisible = false,
            };
            Panel.SetZIndex(frame, 0);
            Panel.SetZIndex(innerGlow, 1);
            Panel.SetZIndex(edge, 2);
            tab.Children.Add(frame);
            tab.Children.Add(innerGlow);
            tab.Children.Add(edge);
            Panel.SetZIndex(label, 3);
            tab.Children.Add(label);
            if (selected && animateSelection)
            {
                slide.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(-10, TimeSpan.FromMilliseconds(100))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                    });
            }
            _tabBorders.Add(tab);
            TabPanel.Children.Add(tab);
            tab.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _tabOffsets.Add(_tabTotalWidth);
            _tabTotalWidth += tab.DesiredSize.Width + tab.Margin.Left + tab.Margin.Right;
        }
        _renderedCategoryIndex = _categoryIndex;

        if (_categoryIndex < _tabBorders.Count)
        {
            var tab = _tabBorders[_categoryIndex];
            Dispatcher.BeginInvoke(new Action(() => tab.BringIntoView()));
        }
    }

    private void RenderItems(List<SystemMenuCategory> categories, bool snap = false, bool crossfade = false)
    {
        // 项目上下切换不再滑动：新容器直接按目标位置落位，避免先滑一下再换。
        _snapListOnce = true;
        _buildingSpare = true;
        ItemsCanvas.Children.Clear();
        SelectionLayer.Children.Clear();
        _itemTexts.Clear();
        _selectionBar = null;
        _selectionHost = null;
        _selectionScale = null;
        ItemsMaskHost.RenderTransform = null;
        ItemsMaskHost.RenderTransformOrigin = new Point(0.5, 0.5);

        if (categories.Count == 0)
        {
            ItemsHost.Visibility = Visibility.Collapsed;
            ItemsCanvas.Height = 0;
            EmptyText.Text = "尚未配置菜单栏，请到「幻想模式」页编辑系统菜单。";
            EmptyText.Visibility = Visibility.Visible;
            FinishRender(crossfade);
            return;
        }

        var category = categories[_categoryIndex];
        var items = category.Items;
        if (items.Count == 0)
        {
            _itemIndex = 0;
            ItemsHost.Visibility = Visibility.Collapsed;
            ItemsCanvas.Height = 0;
            EmptyText.Text = "此菜单栏还没有项目";
            EmptyText.Visibility = Visibility.Visible;
            FinishRender(crossfade);
            return;
        }

        _itemIndex = Math.Clamp(_itemIndex, 0, items.Count - 1);
        EmptyText.Visibility = Visibility.Collapsed;
        ItemsHost.Visibility = Visibility.Visible;
        // 列表起点跟随蓝条高度（唯一来源是 ListTop），避免蓝条改高后项目与选中框错位。
        Canvas.SetTop(ItemsHost, ListTop);

        // 按项目文字测量宽度；太长时截断，太短时保持可点击的横向条。
        var maxWidth = 0.0;
        foreach (var item in items)
        {
            var probe = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.Name) ? "未命名项目" : item.Name,
                FontSize = 11.5,
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            maxWidth = Math.Max(maxWidth, probe.DesiredSize.Width);
        }
        _itemWidth = Math.Clamp(maxWidth + 34, 190, 320);
        ItemsHost.Width = _itemWidth;
        ItemsCanvas.Width = _itemWidth;

        var totalHeight = items.Count * RowStep;
        ItemsCanvas.Height = totalHeight;
        ItemsHost.Height = totalHeight;

        // 选中底框固定在项目列表的第二行位置；列表滚动时框不动。
        // 宽度取项目宽度的 3/4，边缘更圆润，右侧由渐变自然淡出。
        var highlightWidth = Math.Clamp(_itemWidth * 0.85, 160, 300);
        _selectionBar = new Border
        {
            Width = highlightWidth,
            Height = RowHeight,
            Background = SelectedItem,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            OpacityMask = SelectionMask,
            IsHitTestVisible = false,
            Effect = ItemShadow,
        };
        _selectionHost = new Grid
        {
            Width = highlightWidth,
            Height = RowHeight,
            IsHitTestVisible = false,
        };
        _selectionHost.Children.Add(_selectionBar);
        _selectionScale = new ScaleTransform(_appearing || snap ? 1 : 0, 1);
        _selectionHost.RenderTransform = _selectionScale;
        Canvas.SetLeft(_selectionHost, 0);
        Canvas.SetTop(_selectionHost, ListTop + RowStep);
        Panel.SetZIndex(_selectionHost, 3);
        SelectionLayer.Children.Add(_selectionHost);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var itemText = string.IsNullOrWhiteSpace(item.Name) ? "未命名项目" : item.Name;
            var text = new TextBlock
            {
                Text = itemText,
                FontSize = 11.5,
                Width = _itemWidth - 20,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = TextGlow,
            };
            // 用固定高度的 Grid 做垂直居中，避免 TextBlock 贴顶。
            var cell = new Grid
            {
                Width = _itemWidth - 20,
                Height = RowHeight,
            };
            var itemEdge = new TextBlock
            {
                Text = itemText,
                FontSize = 11.5,
                Width = _itemWidth - 20,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = GoldEdgeFill,
                Effect = SoftEdgeBlur,
                IsHitTestVisible = false,
            };
            cell.Children.Add(itemEdge);
            cell.Children.Add(text);
            Canvas.SetLeft(cell, 18);
            Canvas.SetTop(cell, i * RowStep);
            Panel.SetZIndex(cell, 1);
            ItemsCanvas.Children.Add(cell);
            _itemTexts.Add(text);
        }

        UpdateItemStyles();

        // 选中底框从左往右延展；左侧实、右侧淡。比上一版慢一些。
        // 首次呼出时直接给满宽，避免淡入时同时出现"框展开 + 列表滑动"两种位移。
        if (!_appearing && !snap)
        {
            _selectionScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
        }

        FinishRender(crossfade);
        Dispatcher.BeginInvoke(new Action(UpdateItemsPosition));
    }

    /// <summary>渲染完成后交换 A/B 容器；切菜单栏时让旧组淡出的后半段与新组淡入的前半段重叠。</summary>
    private void FinishRender(bool crossfade)
    {
        _buildingSpare = false;
        SwapHosts(crossfade);
    }

    private void SwapHosts(bool crossfade)
    {
        // 连续切换时旧的 Completed 回调可能晚到，用 token 作废，避免把正在淡入的容器又藏起来。
        var token = ++_hostSwapToken;
        var oldHost = _live;
        var fresh = _build;
        _live = fresh;
        _build = oldHost;

        fresh.Group.Visibility = Visibility.Visible;
        if (!crossfade)
        {
            // 先把新容器摆到目标位置，再让它可见，切换过程不会出现“先滑一下”。
            oldHost.Group.BeginAnimation(OpacityProperty, null);
            fresh.Group.BeginAnimation(OpacityProperty, null);
            fresh.Group.Opacity = 0;
            UpdateLayout();
            UpdateItemsPosition();
            oldHost.Group.Visibility = Visibility.Collapsed;
            oldHost.Group.Opacity = 1;
            fresh.Group.Visibility = Visibility.Visible;
            fresh.Group.Opacity = 1;
            return;
        }

        // 旧组 100ms 淡出；新组从 50ms 开始 100ms 淡入，后半段与前半段重叠。
        var from = oldHost.Group.Opacity;
        oldHost.Group.BeginAnimation(OpacityProperty, null);
        oldHost.Group.Visibility = Visibility.Visible;
        oldHost.Group.Opacity = from;
        fresh.Group.BeginAnimation(OpacityProperty, null);
        fresh.Group.Visibility = Visibility.Visible;
        fresh.Group.Opacity = 0;
        Panel.SetZIndex(oldHost.Group, 0);
        Panel.SetZIndex(fresh.Group, 1);

        RestoreEffectsFrom(_categoryEffects);
        SuspendEffectsInto(oldHost.Group, _categoryEffects);
        SuspendEffectsInto(fresh.Group, _categoryEffects);
        _effectRestoreTimer.Stop();
        _effectRestoreTimer.Interval = TimeSpan.FromMilliseconds(130);
        _effectRestoreTimer.Start();

        UpdateLayout();
        UpdateItemsPosition();

        var fadeOut = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(CategoryFadeMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(CategoryFadeMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(CategoryFadeMs / 2.0),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        fadeIn.Completed += (_, _) =>
        {
            if (token != _hostSwapToken) return;
            oldHost.Group.Visibility = Visibility.Collapsed;
            oldHost.Group.Opacity = 1;
            fresh.Group.Visibility = Visibility.Visible;
            fresh.Group.Opacity = 1;
            Panel.SetZIndex(fresh.Group, 0);
            RestoreEffectsFrom(_categoryEffects);
        };
        oldHost.Group.BeginAnimation(OpacityProperty, fadeOut);
        fresh.Group.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void UpdateItemStyles()
    {
        for (var i = 0; i < _itemTexts.Count; i++)
        {
            var selected = i == _itemIndex;
            _itemTexts[i].Foreground = selected ? MenuGoldBright : MenuGold;
            _itemTexts[i].FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            _itemTexts[i].Effect = selected ? SelectedTextGlow : TextGlow;
        }
    }

    /// <summary>项目列表左侧对齐到当前选中菜单栏的左边缘（截图中的版式）。</summary>
    private void UpdateItemsPosition()
    {
        if (_fantasy == null || _tabBorders.Count == 0 || ItemsHost.Visibility != Visibility.Visible) return;
        var index = Math.Clamp(_categoryIndex, 0, _tabBorders.Count - 1);
        if (Root.ActualWidth <= 0 || _tabOffsets.Count <= index) return;

        var centeredLeft = (Root.ActualWidth - _tabTotalWidth) / 2 + _tabOffsets[index];
        var tab = _tabBorders[index];
        var left = centeredLeft;
        if (tab.ActualWidth > 0)
        {
            var actual = tab.TranslatePoint(new Point(0, 0), MenuBody).X;
            if (!double.IsNaN(actual) && actual > 0) left = actual;
        }
        var maxLeft = Math.Max(8, MenuBody.ActualWidth - _itemWidth - 14);
        left = Math.Clamp(left, 8, maxLeft);
        Canvas.SetLeft(ItemsHost, left);
        if (_selectionHost != null) Canvas.SetLeft(_selectionHost, left);
        UpdateItemsClip();
        UpdateListScroll();
    }

    /// <summary>项目可以穿过半透明蓝条，但不能越过蓝条上边缘/上方黑色阴影。</summary>
    private void UpdateItemsClip()
    {
        var width = ItemsMaskHost.ActualWidth > 0 ? ItemsMaskHost.ActualWidth : MenuBody.ActualWidth;
        var height = ItemsMaskHost.ActualHeight > 0 ? ItemsMaskHost.ActualHeight : MenuBody.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var titleBottom = 0d;
        if (TitleText.ActualHeight > 0)
        {
            var point = TitleText.TranslatePoint(new Point(0, TitleText.ActualHeight), MenuBody);
            titleBottom = Math.Max(0, point.Y);
        }
        var clipTop = Math.Min(titleBottom, Math.Max(0, BandTop - 4));
        var fadeEnd = BandTop + BandHeight / 2; // 蓝条中心：完全不透明
        ItemsMaskHost.Clip = new RectangleGeometry(new Rect(0, clipTop, width, Math.Max(0, height - clipTop)));

        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        var fadeStartOffset = clipTop / height;
        var fadeSpan = (fadeEnd - clipTop) / height;
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), fadeStartOffset));
        AddFadeStop(0.15, 0x08);
        AddFadeStop(0.30, 0x1A);
        AddFadeStop(0.50, 0x4D);
        AddFadeStop(0.70, 0x9E);
        AddFadeStop(0.85, 0xDB);
        AddFadeStop(1.00, 0xFF);
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 1));
        mask.Freeze();
        ItemsMaskHost.OpacityMask = mask;

        void AddFadeStop(double progress, byte alpha)
        {
            mask.GradientStops.Add(new GradientStop(
                Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF), fadeStartOffset + fadeSpan * progress));
        }
    }

    /// <summary>
    /// 选中框固定在第二行；向下选择时项目列表整体上滑，选中框本身不移动。
    /// 列表在 ItemsLayer 的裁剪边界（横条下方黑色阴影）处隐去。
    /// </summary>
    private void UpdateListScroll()
    {
        if (ItemsCanvas.Height <= 0) return;
        var target = RowStep - _itemIndex * RowStep;
        if (_appearing || _snapListOnce)
        {
            // 重新打开/切换菜单栏时直接落位，不从上次位置滑动。
            _snapListOnce = false;
            _itemsTransform.BeginAnimation(TranslateTransform.YProperty, null);
            _itemsTransform.Y = target;
            return;
        }
        if (Math.Abs(_itemsTransform.Y - target) < 0.5) return;
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _itemsTransform.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    /// <summary>
    /// 配置了进程且幻想模式/系统菜单启用时，把菜单窗口放进该进程的客户区；
    /// 同时把窗口向上延伸一段（不超过屏幕/进程窗口上方可用空间），让黑影在蓝条上方有更长的渐隐。
    /// 内容整体下移同样的距离，蓝条/标题的屏幕位置保持不变。
    /// </summary>
    private void PositionWindow()
    {
        if (TryGetTargetBounds(out var target, out _))
        {
            // 指定了进程时，悬浮窗严格限制在进程窗口客户区内，不能向上/向外延伸。
            ApplyWindowBounds(new Rect(target.X, target.Y, target.Width, target.Height), 0);
            return;
        }

        var work = SystemParameters.WorkArea;
        var baseTop = work.Top + Math.Max(18, work.Height * 0.025) + 60;
        var fallback = Math.Clamp(baseTop - work.Top, 0, MaxShadowTopExtension);
        var height = Math.Clamp(work.Height * 0.82, 360, 820);
        ApplyWindowBounds(new Rect(work.Left, baseTop - fallback, work.Width, height + fallback), fallback);
    }

    private void ApplyWindowBounds(Rect bounds, double extend)
    {
        _shadowTopExtension = extend;
        _windowBounds = bounds;
        Left = bounds.X;
        Top = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;

        // 内容整体下移 extend，黑影用负 margin 抵消，仍然从窗口最顶端开始。
        Root.Margin = new Thickness(0, extend, 0, 0);
        ShadowBorder.Margin = new Thickness(0, -(24 + extend), 0, 0);
        ShadowBorder.Height = 520 + extend;
        ShadowBorder.Background = BuildShadowBrush(extend);
    }

    /// <summary>
    /// 蓝条在窗口内的位置固定为 74..152；窗口向上延伸 extend 后，
    /// 顶部渐隐长度变成 74+extend，底部保持 520-152=368 的长度。
    /// </summary>
    private static LinearGradientBrush BuildShadowBrush(double extend)
    {
        var height = 520 + extend;
        var bandTop = extend + 74;
        var bandBottom = extend + 152;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };

        // 窗口没有向上延伸时（指定进程），顶部边缘留一点不透明度再向上渐隐，
        // 避免 0% 起步导致最外侧几十像素看不见、随后突然变黑。
        (double T, byte A)[] top = extend < 0.5
            ? [
                (0.00, 0x14), (0.10, 0x1F), (0.20, 0x2E), (0.32, 0x44), (0.44, 0x5C),
                (0.56, 0x73), (0.68, 0x89), (0.80, 0x99), (0.90, 0xA1), (1.00, 0xA6),
              ]
            : [
                (0.00, 0x00), (0.12, 0x0A), (0.24, 0x1A), (0.36, 0x2E), (0.48, 0x47),
                (0.60, 0x63), (0.72, 0x7E), (0.84, 0x96), (0.94, 0xA2), (1.00, 0xA6),
              ];
        foreach (var (t, a) in top)
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(a, 0, 0, 0), t * bandTop / height));

        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xA6, 0, 0, 0), bandBottom / height));

        (double T, byte A)[] bottom =
        [
            (0.00, 0xA6), (0.12, 0x94), (0.25, 0x7C), (0.38, 0x63), (0.52, 0x4A),
            (0.66, 0x33), (0.80, 0x1F), (0.90, 0x10), (0.96, 0x06), (1.00, 0x00),
        ];
        foreach (var (t, a) in bottom)
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(a, 0, 0, 0),
                (bandBottom + t * (height - bandBottom)) / height));

        return brush;
    }

    private bool TryGetTargetBounds(out Rect bounds, out double monitorTop)
    {
        bounds = default;
        monitorTop = 0;
        if (_fantasy is not { Enabled: true, SystemMenuEnabled: true }) return false;

        var process = (Application.Current as App)?.Engine?.Profile?.ProcessName;
        if (string.IsNullOrWhiteSpace(process)) return false;

        var handle = GameWindowTarget.FindProcessWindow(process);
        if (handle == IntPtr.Zero) return false;

        var target = GameWindowTarget.Create(handle);
        if (target is not { IsValid: true }) return false;

        bounds = new Rect(target.X, target.Y, target.Width, target.Height);
        monitorTop = ScreenTarget.GetMonitorAt(target.X + target.Width / 2, target.Y + target.Height / 2)?.Y ?? 0;
        return true;
    }

    /// <summary>进程窗口可能是另一个 DPI 的显示器，用 Win32 物理坐标再精确落一次。</summary>
    private void ApplyTargetBounds()
    {
        if (_windowBounds is not { } bounds || _appliedBounds == _windowBounds) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y),
            (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), SwpNoActivate | SwpShowWindow);
        _appliedBounds = _windowBounds;
    }

    private void SetInteractive(bool interactive)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        // 状态没变就不要反复改 ex-style / SetWindowRgn：窗口可见时这两步会触发重绘，容易闪一下。
        if (_styleApplied && _interactive == interactive) return;
        _styleApplied = true;
        _interactive = interactive;
        var style = GetWindowLong(handle, GwlExStyle) | WsExToolWindow | WsExNoActivate;
        style = interactive ? style & ~WsExTransparent : style | WsExTransparent;
        SetWindowLong(handle, GwlExStyle, style);
        Root.IsHitTestVisible = interactive;
        ApplyInputRegion();
    }

    /// <summary>
    /// 从窗口可点击区域中挖掉双十字热键栏的矩形，让那部分点击交给热键栏，
    /// 其余区域仍由本窗口接收并退出菜单。
    /// </summary>
    private void ApplyInputRegion()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!_interactive)
        {
            SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }

        var origin = PointToScreen(new Point(0, 0));
        var end = PointToScreen(new Point(ActualWidth, ActualHeight));
        var width = Math.Max(1, (int)Math.Round(end.X - origin.X));
        var height = Math.Max(1, (int)Math.Round(end.Y - origin.Y));
        var full = CreateRectRgn(0, 0, width, height);

        if (ClickThroughHole is Rect hole)
        {
            var left = (int)Math.Round(hole.Left - origin.X);
            var top = (int)Math.Round(hole.Top - origin.Y);
            var right = (int)Math.Round(hole.Right - origin.X);
            var bottom = (int)Math.Round(hole.Bottom - origin.Y);
            left = Math.Clamp(left, 0, width);
            right = Math.Clamp(right, 0, width);
            top = Math.Clamp(top, 0, height);
            bottom = Math.Clamp(bottom, 0, height);
            if (right > left && bottom > top)
            {
                var cut = CreateRectRgn(left, top, right, bottom);
                CombineRgn(full, full, cut, RgnDiff);
                DeleteObject(cut);
            }
        }

        SetWindowRgn(hwnd, full, true);
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateSelectedGradient()
    {
        // V0.6.2 的横向金色渐变；只在最左端加一小段透明度过渡。
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x9C, 0x70, 0x1C), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xE6, 0x9C, 0x70, 0x1C), 0.10));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xA6, 0x7A, 0x55, 0x12), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x0A, 0x7A, 0x55, 0x12), 1));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateSelectionMask()
    {
        // 回到 V0.6.9 的单层径向柔化。
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.42, 0.5),
            GradientOrigin = new Point(0.42, 0.5),
            RadiusX = 0.88,
            RadiusY = 0.62,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.38));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
        brush.Freeze();
        return brush;
    }
    private static BlurEffect CreateBlur(double radius)
    {
        var effect = new BlurEffect { Radius = radius, RenderingBias = RenderingBias.Performance };
        effect.Freeze();
        return effect;
    }

    private static DropShadowEffect CreateGlow(string hex, double blur, double opacity)
    {
        var effect = new DropShadowEffect
        {
            BlurRadius = blur,
            ShadowDepth = 0,
            Direction = 0,
            Opacity = opacity,
            Color = (Color)ColorConverter.ConvertFromString(hex),
            RenderingBias = RenderingBias.Performance,
        };
        effect.Freeze();
        return effect;
    }

    private static DropShadowEffect CreateShadow(double blur, double depth, double direction, double opacity)
    {
        var effect = new DropShadowEffect
        {
            BlurRadius = blur,
            ShadowDepth = depth,
            Direction = direction,
            Opacity = opacity,
            Color = Colors.Black,
            RenderingBias = RenderingBias.Performance,
        };
        effect.Freeze();
        return effect;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, int flags);

    private const int RgnDiff = 4;
}



































