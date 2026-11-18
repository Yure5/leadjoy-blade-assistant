using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LeadjoyBlade.App.Dialogs;
using LeadjoyBlade.Core.Mapping;

namespace LeadjoyBlade.App.Views;

public partial class FantasyPage : UserControl
{
    private FantasyItem? _dragItem;
    private int _dragSlot = -1;
    private int _selectedSlot = -1;
    private int _lastRenderedSet = -1;
    private Point _dragStart;
    private bool _loading;
    private bool _lockHooked;
    private bool _syncingOverlay;
    private bool _slotDropHandled;
    private Border? _pressedBorder;
    private int _pressedSlot = -1;
    private readonly List<ClipboardEntry> _clipboard = [];
    private readonly List<HistoryState> _undo = [];
    private readonly List<HistoryState> _redo = [];
    private bool _bandSelecting;
    private Point _bandStart;
    private List<FantasyItem> _bandBase = [];
    private bool _menuOnItem;
    private const string ItemDragFormat = "LeadjoyFantasyItemIds";

    private sealed class ClipboardEntry
    {
        public string SourceId { get; set; } = "";
        public FantasyItem Item { get; set; } = new();
        public bool Cut { get; set; }
    }

    private sealed class HistoryState
    {
        public string Library { get; set; } = "";
        public string Slots { get; set; } = "";
    }

    private static readonly (string Id, string Name, string Description)[] BaseKeyDefs =
    [
        ("A", "A", "确认"), ("B", "B", "取消"), ("X", "X", "对话"), ("Y", "Y", "跳跃"),
        ("Up", "↑", "上"), ("Down", "↓", "下"), ("Left", "←", "左"), ("Right", "→", "右"),
        ("LB", "LB", "拾取"), ("L4", "L4", "左键"), ("R4", "R4", "右键"),
        ("Select", "SELECT", "商城"), ("Start", "START", "坐骑"), ("Capture", "CAPTURE", "截图"),
        ("C", "C", "自定义"),
        ("L3", "L3", "下压"), ("Fn", "Fn", "功能"),
    ];

    public FantasyPage()
    {
        _loading = true;
        InitializeComponent();
        Hotbar.SlotSize = 47;
        Hotbar.KeyFontSize = 9;
        Hotbar.NameFontSize = 10.5;
        Hotbar.FooterFontSize = 13;
        Hotbar.IdleOpacity = 1.0;
        Hotbar.ShowSlotKeys = true;
        Hotbar.ShowSetBadge = true;
        Hotbar.ShowLock = true;
        Hotbar.StrongEmptySlots = true;
        Hotbar.LockClicked += Hotbar_OnLockClicked;
        _loading = false;
        Loaded += (_, _) =>
        {
            if (!_lockHooked)
            {
                App.Current.Fantasy.OverlayLockChanged += Page_OverlayLockChanged;
                App.Current.Fantasy.OverlayVisibilityChanged += Page_OverlayVisibilityChanged;
                App.Current.Fantasy.SlotTriggered += Page_SlotTriggered;
                App.Current.Fantasy.SlotsChanged += Page_SlotsChanged;
                _lockHooked = true;
            }
            Reload();
        };
        Unloaded += (_, _) =>
        {
            if (!_lockHooked) return;
            App.Current.Fantasy.OverlayLockChanged -= Page_OverlayLockChanged;
            App.Current.Fantasy.OverlayVisibilityChanged -= Page_OverlayVisibilityChanged;
            App.Current.Fantasy.SlotTriggered -= Page_SlotTriggered;
            App.Current.Fantasy.SlotsChanged -= Page_SlotsChanged;
            _lockHooked = false;
        };
    }

    private void Page_OverlayLockChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded) return;
        Hotbar.SetLocked(Fantasy.Overlay.Locked);
        BuildSlots();
    }

    private void Page_OverlayVisibilityChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded) return;
        SyncOverlayState();
    }

    public void SyncOverlayState()
    {
        _syncingOverlay = true;
        OverlayToggle.IsChecked = Fantasy.Overlay.Visible;
        OverlayToggle.IsEnabled = Fantasy.Enabled;
        OverlayVisibleCheck.IsChecked = Fantasy.Overlay.Visible;
        OverlayVisibleCheck.IsEnabled = Fantasy.Enabled;
        _syncingOverlay = false;
    }

    private void Page_SlotTriggered(int index)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            Hotbar.PlayTrigger(index);
        }));
    }

    private void Page_SlotsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            BuildSlots();
        }));
    }

    private FantasyProfile Fantasy => App.Current.Engine.Profile.Fantasy;
    private FantasyLibrary Library => App.Current.Store.FantasyLibrary;

    public void Reload()
    {
        if (SetCombo == null) return;
        _loading = true;
        var fantasy = Fantasy;
        EnableSwitch.IsChecked = fantasy.Enabled;
        EnableText.Text = fantasy.Enabled ? "已启用" : "关闭";

        SetCombo.Items.Clear();
        for (var i = 0; i < fantasy.Sets.Count; i++) SetCombo.Items.Add($"热键栏 {i + 1}");
        SetCombo.SelectedIndex = Math.Clamp(0, 0, Math.Max(0, fantasy.Sets.Count - 1));

        GroupCombo.Items.Clear();
        foreach (var group in Library.Groups) GroupCombo.Items.Add(group.Name);
        if (GroupCombo.Items.Count > 0) GroupCombo.SelectedIndex = 0;

        RefreshVisibleSets();
        ExtensionCheck.IsChecked = fantasy.ExtensionEnabled;
        LeftExtensionCombo.Items.Clear();
        RightExtensionCombo.Items.Clear();
        foreach (var set in fantasy.Sets)
        {
            var index = fantasy.Sets.IndexOf(set);
            LeftExtensionCombo.Items.Add($"热键栏 {index + 1}");
            RightExtensionCombo.Items.Add($"热键栏 {index + 1}");
        }
        LeftExtensionCombo.SelectedIndex = Math.Clamp(fantasy.LeftExtensionSet, 0, fantasy.Sets.Count - 1);
        RightExtensionCombo.SelectedIndex = Math.Clamp(fantasy.RightExtensionSet, 0, fantasy.Sets.Count - 1);
        SensitivityBox.Text = string.Join(", ", fantasy.SensitivityLevels);
        SelectComboByTag(SensitivityKeyCombo, fantasy.SensitivitySwitchKey);
        if (LeftStickCombo.Items.Count == 0)
        {
            LeftStickCombo.Items.Add("方向键");
            LeftStickCombo.Items.Add("WASD");
        }
        LeftStickCombo.SelectedIndex = fantasy.LeftStickMode == "wasd" ? 1 : 0;
        OverlayToggle.IsChecked = fantasy.Overlay.Visible;
        OverlayToggle.IsEnabled = fantasy.Enabled;
        OverlayVisibleCheck.IsChecked = fantasy.Overlay.Visible;
        OverlayVisibleCheck.IsEnabled = fantasy.Enabled;
        ClickThroughCheck.IsChecked = fantasy.Overlay.ClickThrough;
        OverlayScaleSlider.Value = Math.Clamp(fantasy.Overlay.Scale, 0.1, 1.5);
        OverlayOpacitySlider.Value = Math.Clamp(fantasy.Overlay.Opacity, 0, 1);
        OverlayScaleText.Text = $"{OverlayScaleSlider.Value * 100:0}%";
        OverlayOpacityText.Text = $"{OverlayOpacitySlider.Value * 100:0}%";
        SystemMenuCheck.IsChecked = fantasy.SystemMenuEnabled;
        SelectComboByTag(SystemMenuHotkeyCombo, fantasy.SystemMenuHotkey);
        SelectComboByTag(SystemMenuConfirmCombo, fantasy.SystemMenuConfirmButton);
        SelectComboByTag(SystemMenuCancelCombo, fantasy.SystemMenuCancelButton);
        _lastRenderedSet = -1;
        BuildBaseKeys();
        BuildSlots();
        RefreshItems();
        _loading = false;
        UpdateHistoryButtons();
    }

    private string BaseDescription(string id, string fallback)
    {
        if (Fantasy.BaseKeyDescriptions.TryGetValue(id, out var description)
            && !string.IsNullOrWhiteSpace(description))
            return description;
        if (Fantasy.BaseKeyLabels.TryGetValue(id, out var old) && !string.IsNullOrWhiteSpace(old))
        {
            var keyName = BaseKeyDefs.FirstOrDefault(x => x.Id == id).Name ?? id;
            if (old.StartsWith(keyName + " ", StringComparison.Ordinal))
                old = old[(keyName.Length + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(old)) return old;
        }
        return fallback;
    }

    private void OpenBaseKey(string id)
    {
        var def = BaseKeyDefs.FirstOrDefault(x => x.Id == id);
        var keyName = string.IsNullOrWhiteSpace(def.Name) ? id : def.Name;
        var description = BaseDescription(id, def.Description);
        var action = Fantasy.BaseActions.TryGetValue(id, out var existing)
            ? CloneAction(existing)
            : new BindingAction { Type = ActionType.Disabled };
        var dialog = new BaseActionDialog(keyName, description, action) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        Fantasy.BaseActions[id] = dialog.Result;
        Fantasy.BaseKeyDescriptions[id] = dialog.ResultDescription;
        Save();
        BuildBaseKeys();
    }

    private void BuildBaseKeys()
    {
        BaseKeysPanel.Children.Clear();
        var actions = Fantasy.BaseActions;
        // 灵敏度切换键不参与基础键映射/显示；另一个键作为普通基础键出现。
        var sensitivityKey = string.Equals(Fantasy.SensitivitySwitchKey, "C", StringComparison.OrdinalIgnoreCase)
            ? "C"
            : "Capture";
        // 启用系统菜单时，呼出键只显示但置灰且不生效；灵敏度切换键同理。
        var menuHotkey = Fantasy.SystemMenuEnabled ? Fantasy.SystemMenuHotkey : null;
        // 当前配置启用了手柄输入法：它的唤醒键也不能再做基础键映射。
        var ime = App.Current.Engine?.Profile?.Ime;
        var imeActive = ime?.Enabled == true || App.Current.ImeRunning;
        var imeKeys = imeActive
            ? ImeWakeConflicts.WakeBaseKeys(ime?.WakeSpec ?? "")
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, keyName, defaultDescription) in BaseKeyDefs)
        {
            var disabledReason = id == sensitivityKey ? "灵敏度切换键"
                : !string.IsNullOrEmpty(menuHotkey) && id == menuHotkey ? "系统菜单呼出键"
                : imeKeys.Contains(id) ? "手柄输入法唤醒键"
                : null;
            var disabled = disabledReason != null;
            var action = actions.TryGetValue(id, out var existing)
                ? existing
                : new BindingAction { Type = ActionType.Disabled };
            var description = BaseDescription(id, defaultDescription);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(CreateBaseKeyGlyph(id));

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            var nameText = new TextBlock
            {
                Text = keyName,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                MaxWidth = 108,
            };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            text.Children.Add(nameText);
            var descText = new TextBlock
            {
                Text = description,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 108,
                Margin = new Thickness(0, 1, 0, 0),
            };
            descText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            text.Children.Add(descText);
            var actionText = new TextBlock
            {
                Text = disabled
                    ? disabledReason!
                    : action.Type == ActionType.Disabled ? "未映射" : action.Describe(),
                FontSize = 8.5,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 108,
                Margin = new Thickness(0, 1, 0, 0),
            };
            actionText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            text.Children.Add(actionText);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var card = new Border
            {
                Width = 166,
                MinHeight = 80,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                Cursor = disabled ? Cursors.Arrow : Cursors.Hand,
                Tag = id,
                ToolTip = disabled ? $"该键当前由「{disabledReason}」使用，不能修改" : "点击修改键位说明和键位动作",
                Opacity = disabled ? 0.38 : 1,
                IsHitTestVisible = !disabled,
                Child = grid,
            };
            card.SetResourceReference(Border.BackgroundProperty, "Surface");
            card.SetResourceReference(Border.BorderBrushProperty, "Border");
            if (!disabled)
            {
                card.MouseLeftButtonUp += BaseKey_OnClick;
                card.MouseEnter += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "Accent");
                card.MouseLeave += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "Border");
            }
            BaseKeysPanel.Children.Add(card);
        }
    }

    /// <summary>输入法开关/唤醒键变化后，外部只刷新基础键置灰状态。</summary>
    public void RefreshBaseKeys() => BuildBaseKeys();

    private FrameworkElement CreateBaseKeyGlyph(string id)    {
        var host = new Grid { Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Left };
        if (id is "A" or "B" or "X" or "Y")
        {
            var color = id switch
            {
                "A" => Color.FromRgb(0x2E, 0x8B, 0x57),
                "B" => Color.FromRgb(0xB0, 0x3A, 0x2E),
                "X" => Color.FromRgb(0x2E, 0x6D, 0xA4),
                _ => Color.FromRgb(0xC9, 0x97, 0x1A),
            };
            host.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Fill = new SolidColorBrush(color),
                Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0xF3, 0xE6, 0xD0)),
                StrokeThickness = 1.5,
            });
            host.Children.Add(new TextBlock
            {
                Text = id,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xE6, 0xD0)),
                FontWeight = FontWeights.Bold,
                FontSize = 17,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return host;
        }

        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x48, 0x51, 0x61), 0),
                new GradientStop(Color.FromRgb(0x1A, 0x20, 0x2B), 1),
            },
        };
        var stroke = new SolidColorBrush(Color.FromRgb(0x7D, 0x88, 0x9B));
        var cream = new SolidColorBrush(Color.FromRgb(0xF3, 0xE6, 0xD0));

        if (id is "Up" or "Down" or "Left" or "Right")
        {
            var canvas = new Canvas { Width = 36, Height = 36 };
            canvas.Children.Add(new System.Windows.Shapes.Polygon
            {
                Points = new PointCollection
                {
                    new(13, 3), new(23, 3), new(23, 13), new(33, 13),
                    new(33, 23), new(23, 23), new(23, 33), new(13, 33),
                    new(13, 23), new(3, 23), new(3, 13), new(13, 13),
                },
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1,
            });
            var triangle = id switch
            {
                "Up" => new PointCollection { new(18, 5), new(15, 9.5), new(21, 9.5) },
                "Down" => new PointCollection { new(18, 31), new(15, 26.5), new(21, 26.5) },
                "Left" => new PointCollection { new(5, 18), new(9.5, 15), new(9.5, 21) },
                _ => new PointCollection { new(31, 18), new(26.5, 15), new(26.5, 21) },
            };
            canvas.Children.Add(new System.Windows.Shapes.Polygon
            {
                Points = triangle,
                Fill = cream,
            });
            host.Children.Add(canvas);
            return host;
        }

        if (id == "L3")
        {
            var canvas = new Canvas { Width = 36, Height = 36 };

            // 摇杆横截面：底座、连杆、蘑菇头，上方一个下压箭头。
            var basePlate = new System.Windows.Shapes.Ellipse
            {
                Width = 30,
                Height = 10,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1,
            };
            Canvas.SetLeft(basePlate, 3);
            Canvas.SetTop(basePlate, 25);
            canvas.Children.Add(basePlate);

            var stem = new System.Windows.Shapes.Rectangle
            {
                Width = 6,
                Height = 10,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1,
            };
            Canvas.SetLeft(stem, 15);
            Canvas.SetTop(stem, 18);
            canvas.Children.Add(stem);

            var cap = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M6,15 Q6,11 10,11 L26,11 Q30,11 30,15 L30,16 Q30,19 27,19 L9,19 Q6,19 6,16 Z"),
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1,
            };
            canvas.Children.Add(cap);

            var arrowLine = new System.Windows.Shapes.Line
            {
                X1 = 18, Y1 = 1, X2 = 18, Y2 = 7,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            arrowLine.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Text");
            canvas.Children.Add(arrowLine);
            var arrowHead = new System.Windows.Shapes.Polygon
            {
                Points = new PointCollection { new(14, 7), new(22, 7), new(18, 11) },
            };
            arrowHead.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Text");
            canvas.Children.Add(arrowHead);
            host.Children.Add(canvas);
            return host;
        }

        if (id == "LB")
        {
            var canvas = new Canvas { Width = 36, Height = 36 };
            var bumper = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M3,9 Q3,4 8,4 L28,4 Q33,4 33,9 L33,16 L26,16 L26,22 L10,22 L10,16 L3,16 Z"),
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1,
            };
            canvas.Children.Add(bumper);
            host.Children.Add(canvas);
            host.Children.Add(new TextBlock
            {
                Text = "LB",
                Foreground = cream,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 5, 0, 0),
            });
            return host;
        }

        if (id is "L4" or "R4")
        {
            var canvas = new Canvas { Width = 36, Height = 36 };
            var paddle = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M11,4 Q11,2 13,2 L23,2 Q25,2 25,4 L28,13 L27,30 Q27,33 24,33 L12,33 Q9,33 9,30 L8,13 Z"),
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1,
                RenderTransform = new RotateTransform(id == "L4" ? -7 : 7, 18, 18),
            };
            canvas.Children.Add(paddle);
            host.Children.Add(canvas);
            host.Children.Add(new TextBlock
            {
                Text = id,
                Foreground = cream,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            });
            return host;
        }

        if (id is "Select" or "Start" or "Fn")
        {
            var oval = new Border
            {
                Width = 18,
                Height = 28,
                CornerRadius = new CornerRadius(9),
                Background = fill,
                BorderBrush = stroke,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(9, 4, 9, 4),
            };
            host.Children.Add(oval);
            if (id == "Fn")
            {
                host.Children.Add(new Border
                {
                    // 竖椭圆 18×28：竖线 5×15，左右 6.5、上下 6.5，线宽略小于留白。
                    Width = 5,
                    Height = 15,
                    CornerRadius = new CornerRadius(2),
                    Background = cream,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            else
            {
                host.Children.Add(new TextBlock
                {
                    Text = id == "Select" ? "−" : "+",
                    Foreground = cream,
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            return host;
        }

        if (id == "C")
        {
            var canvas = new Canvas { Width = 36, Height = 36 };
            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = 28,
                Height = 28,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1.0,
            };
            Canvas.SetLeft(ring, 4);
            Canvas.SetTop(ring, 4);
            canvas.Children.Add(ring);

            // 参考图的 C 是 8×8 点阵，逐行照排（# = 方点）。
            string[] cMatrix =
            [
                "..####..",
                ".######.",
                "##....##",
                "##......",
                "##......",
                "##....##",
                ".######.",
                "..####..",
            ];
            const double pitch = 2.2;
            const double dot = 1.55;
            const double start = 18 - 8 * pitch / 2 + (pitch - dot) / 2;
            for (var row = 0; row < cMatrix.Length; row++)
            {
                for (var col = 0; col < cMatrix[row].Length; col++)
                {
                    if (cMatrix[row][col] != '#') continue;
                    var point = new System.Windows.Shapes.Rectangle
                    {
                        Width = dot,
                        Height = dot,
                        Fill = cream,
                        RadiusX = 0.35,
                        RadiusY = 0.35,
                    };
                    Canvas.SetLeft(point, start + col * pitch);
                    Canvas.SetTop(point, start + row * pitch);
                    canvas.Children.Add(point);
                }
            }

            host.Children.Add(canvas);
            return host;
        }

        if (id == "Capture")
        {
            var canvas = new Canvas { Width = 36, Height = 36 };
            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = 28,
                Height = 28,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1.0,
            };
            Canvas.SetLeft(ring, 4);
            Canvas.SetTop(ring, 4);
            canvas.Children.Add(ring);

            // 实心直角方块挖掉中心圆和十字线，挖空处直接透出卡片底色。
            Geometry shape = new RectangleGeometry(new Rect(11.5, 11.5, 13, 13));
            shape = Geometry.Combine(shape, new EllipseGeometry(new Point(18, 18), 4.5, 4.5),
                GeometryCombineMode.Exclude, null);
            shape = Geometry.Combine(shape, new RectangleGeometry(new Rect(11.5, 17, 13, 2)),
                GeometryCombineMode.Exclude, null);
            shape = Geometry.Combine(shape, new RectangleGeometry(new Rect(17, 11.5, 2, 13)),
                GeometryCombineMode.Exclude, null);
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = shape,
                Fill = cream,
            });
            host.Children.Add(canvas);
            return host;
        }

        var label = id;
        var button = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = fill,
            BorderBrush = stroke,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(7),
        };
        host.Children.Add(button);
        host.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = cream,
            FontSize = 8,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return host;
    }

    private void BaseKey_OnClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border card && card.Tag is string id) OpenBaseKey(id);
    }

    private void BuildSlots(bool animate = false, bool fromRight = false)
    {
        var fantasy = Fantasy;
        if (fantasy.Sets.Count == 0) return;
        var setIndex = Math.Clamp(SetCombo.SelectedIndex, 0, fantasy.Sets.Count - 1);
        var set = fantasy.Sets[setIndex];
        var locked = fantasy.Overlay.Locked;
        if (locked) _selectedSlot = -1;
        Hotbar.Render(set, Library, setIndex, null, locked, animate, fromRight);
        Hotbar.AllowDrop = true;
        foreach (var border in Hotbar.SlotBorders)
        {
            border.AllowDrop = true;
            border.Cursor = locked ? Cursors.Hand : Cursors.Arrow;
            border.MouseLeftButtonDown += Slot_OnActivate;
            border.MouseLeftButtonUp += Slot_OnRelease;
            border.MouseLeftButtonDown += Slot_OnMouseDown;
            border.MouseMove += Slot_OnMouseMove;
            border.Drop += Slot_OnDrop;
            border.DragOver += Slot_OnDragOver;
        }
        Hotbar.SetActive(false, false, false, false);
        Hotbar.Select(locked ? -1 : _selectedSlot);
        PreviewSetText.Text = $"热键栏 {setIndex + 1}";
        _lastRenderedSet = setIndex;
        UpdateLockHint();
        App.Current.Fantasy.NotifyContentChanged();
    }

    private void Slot_OnActivate(object sender, MouseButtonEventArgs e)
    {
        if (!Fantasy.Overlay.Locked) return;
        if (sender is Border border && border.Tag is int index)
        {
            EndSlotPress();
            _pressedBorder = border;
            _pressedSlot = index;
            border.CaptureMouse();
            border.MouseMove += Slot_OnMouseMoveHold;
            border.MouseLeave += Slot_OnMouseLeaveHold;
            App.Current.Fantasy.PressSlot(index, SetCombo.SelectedIndex);
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
        if (!Fantasy.Overlay.Locked) return;
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
        var overlay = Fantasy.Overlay;
        overlay.Locked = !overlay.Locked;
        if (!overlay.Locked)
        {
            overlay.ClickThrough = false;
            ClickThroughCheck.IsChecked = false;
        }
        Save();
        Hotbar.SetLocked(overlay.Locked);
        BuildSlots();
        App.Current.Fantasy.ApplyOverlayChrome();
    }

    private void UpdateLockHint()
    {
        LockHint.Text = Fantasy.Overlay.Locked
            ? "已锁定：格子不可拖动编辑，点击格子等同于按手柄组合键；点击分隔线锁解锁。"
            : "已解锁：拖动左侧映射到格子或交换位置；拖动悬浮窗顶部条移动悬浮窗。";
    }

    private FantasySet CurrentSet()
    {
        var fantasy = Fantasy;
        var set = fantasy.Sets[Math.Clamp(SetCombo.SelectedIndex, 0, fantasy.Sets.Count - 1)];
        while (set.Slots.Count < 16) set.Slots.Add(null);
        return set;
    }

    private void Slot_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Fantasy.Overlay.Locked) return;
        if (sender is not Border border || border.Tag is not int index) return;
        _selectedSlot = index;
        _dragSlot = index;
        _dragStart = e.GetPosition(this);
        Hotbar.Select(index);
        RevealMappedItem(CurrentSet(), index, fallbackToFirst: false);
    }

    private void Slot_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (Fantasy.Overlay.Locked) return;
        if (e.LeftButton != MouseButtonState.Pressed || sender is not Border border) return;
        if (_dragSlot < 0 || border.Tag is not int index || index != _dragSlot) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < 6 && Math.Abs(position.Y - _dragStart.Y) < 6) return;
        _dragSlot = -1;
        _slotDropHandled = false;
        var effect = DragDrop.DoDragDrop(border, $"slot:{index}", DragDropEffects.Move);
        if (!_slotDropHandled && effect == DragDropEffects.None && Mouse.LeftButton == MouseButtonState.Released)
            RemoveSlotMapping(index);
    }

    /// <summary>把格子拖到键位外：只清空该格，不删除映射库里的映射。</summary>
    private void RemoveSlotMapping(int index)
    {
        if (Fantasy.Overlay.Locked) return;
        var set = CurrentSet();
        if (index < 0 || index >= set.Slots.Count) return;
        set.Slots[index] = null;
        _selectedSlot = -1;
        Hotbar.Select(-1);
        SaveAndRefresh();
    }

    private void Slot_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !Fantasy.Overlay.Locked && e.Data.GetDataPresent(DataFormats.StringFormat)
            ? DragDropEffects.Copy | DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Slot_OnDrop(object sender, DragEventArgs e)
    {
        if (Fantasy.Overlay.Locked) return;
        if (sender is not Border target || target.Tag is not int targetIndex) return;
        _slotDropHandled = true;
        var set = CurrentSet();
        var value = e.Data.GetData(DataFormats.StringFormat) as string;
        if (value != null && value.StartsWith("slot:", StringComparison.Ordinal))
        {
            if (!int.TryParse(value[5..], out var sourceIndex) || sourceIndex == targetIndex) return;
            (set.Slots[sourceIndex], set.Slots[targetIndex]) = (set.Slots[targetIndex], set.Slots[sourceIndex]);
        }
        else if (value != null && Library.Items.Any(x => x.Id == value))
        {
            set.Slots[targetIndex] = value;
        }
        else
        {
            return;
        }
        SaveAndRefresh();
    }

    private void ItemList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Fantasy.Overlay.Locked)
        {
            _dragItem = null;
            return;
        }
        var container = ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container == null)
        {
            if (IsInScrollBar(e.OriginalSource as DependencyObject)) return;
            _dragItem = null;
            _bandSelecting = true;
            _bandStart = e.GetPosition(ItemList);
            _bandBase = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? SelectedItems() : [];
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) ItemList.UnselectAll();
            ItemList.Focus();
            ItemList.CaptureMouse();
            BandBorder.Visibility = Visibility.Visible;
            UpdateBand(_bandStart);
            e.Handled = true;
            return;
        }
        _dragItem = container.DataContext as FantasyItem;
    }

    private void ItemList_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_bandSelecting)
        {
            if (e.LeftButton != MouseButtonState.Pressed) EndBandSelection();
            else
            {
                UpdateBand(e.GetPosition(ItemList));
                e.Handled = true;
            }
            return;
        }
        if (Fantasy.Overlay.Locked || e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;
        var moving = SelectedItems();
        if (!moving.Contains(_dragItem)) moving = [_dragItem];
        if (moving.Count == 0) moving = [_dragItem];
        var data = new DataObject();
        data.SetData(DataFormats.StringFormat, _dragItem.Id);
        data.SetData(ItemDragFormat, string.Join("|", moving.Select(x => x.Id)));
        DragDrop.DoDragDrop(ItemList, data, DragDropEffects.Copy | DragDropEffects.Move);
        _dragItem = null;
        if (!IsLoaded) return;
        // 松手后同步刷新：可能落在悬浮窗上，页内预览也要更新。
        BuildSlots();
        App.Current.Fantasy.NotifyContentChanged();
    }

    private void ItemList_OnDragOver(object sender, DragEventArgs e)
    {
        var valid = !Fantasy.Overlay.Locked && e.Data.GetDataPresent(ItemDragFormat);
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        if (valid) ShowDropLine(e);
        else HideDropLine();
        e.Handled = true;
    }

    private void ItemList_OnDragLeave(object sender, DragEventArgs e) => HideDropLine();

    private (FantasyItem? Target, bool After, double Y) FindDropTarget(DragEventArgs e)
    {
        var p = e.GetPosition(ItemList);
        var items = ItemList.Items.OfType<FantasyItem>().ToList();
        if (items.Count == 0) return (null, false, 0);
        var lastBottom = 0.0;
        foreach (var item in items)
        {
            if (ItemList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container) continue;
            var origin = container.TranslatePoint(new Point(0, 0), ItemList);
            var bottom = origin.Y + container.ActualHeight;
            lastBottom = bottom;
            if (p.Y < origin.Y) return (item, false, origin.Y);
            if (p.Y <= bottom)
                return (item, p.Y > origin.Y + container.ActualHeight / 2, p.Y > origin.Y + container.ActualHeight / 2 ? bottom : origin.Y);
        }
        return (null, false, lastBottom);
    }

    private void ShowDropLine(DragEventArgs e)
    {
        var y = FindDropLineY(e);
        DropLine.Margin = new Thickness(0, Math.Max(0, y - 1), 0, 0);
        DropLine.Visibility = Visibility.Visible;
    }

    private double FindDropLineY(DragEventArgs e)
    {
        var p = e.GetPosition(ItemList);
        var rows = new List<(double Top, double Bottom)>();
        foreach (var item in ItemList.Items.OfType<FantasyItem>())
        {
            if (ItemList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container) continue;
            var origin = container.TranslatePoint(new Point(0, 0), ItemList);
            rows.Add((origin.Y, origin.Y + container.ActualHeight));
        }
        if (rows.Count == 0) return 0;

        for (var i = 0; i < rows.Count; i++)
        {
            if (p.Y >= (rows[i].Top + rows[i].Bottom) / 2) continue;
            return i == 0 ? rows[0].Top : (rows[i - 1].Bottom + rows[i].Top) / 2;
        }
        return rows[^1].Bottom;
    }

    private void HideDropLine() => DropLine.Visibility = Visibility.Collapsed;

    private void ItemList_OnDrop(object sender, DragEventArgs e)
    {
        HideDropLine();
        if (Fantasy.Overlay.Locked) return;
        var raw = e.Data.GetData(ItemDragFormat) as string;
        if (string.IsNullOrWhiteSpace(raw)) return;
        var ids = raw.Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var moving = Library.Items.Where(x => ids.Contains(x.Id)).ToList();
        if (moving.Count == 0) return;
        moving.Sort((a, b) => Library.Items.IndexOf(a).CompareTo(Library.Items.IndexOf(b)));

        var (target, after, _) = FindDropTarget(e);
        if (target != null && ids.Contains(target.Id)) return;
        var before = CaptureHistory();
        var groupId = moving[0].GroupId;
        Library.Items.RemoveAll(x => ids.Contains(x.Id));
        int index;
        if (target != null)
        {
            index = Library.Items.IndexOf(target) + (after ? 1 : 0);
        }
        else
        {
            var last = Library.Items.LastOrDefault(x => x.GroupId == groupId);
            index = last == null ? Library.Items.Count : Library.Items.IndexOf(last) + 1;
        }
        Library.Items.InsertRange(Math.Clamp(index, 0, Library.Items.Count), moving);
        App.Current.Store.SaveFantasyLibrary();
        RefreshItems();
        SelectItems(moving);
        RecordHistory(before);
        e.Handled = true;
    }

    private void ItemList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_bandSelecting) EndBandSelection();
    }

    private void UpdateBand(Point current)
    {
        var w = Math.Max(1, ItemList.ActualWidth);
        var h = Math.Max(1, ItemList.ActualHeight);
        current.X = Math.Clamp(current.X, 0, w);
        current.Y = Math.Clamp(current.Y, 0, h);
        var x = Math.Min(_bandStart.X, current.X);
        var y = Math.Min(_bandStart.Y, current.Y);
        var rw = Math.Abs(current.X - _bandStart.X);
        var rh = Math.Abs(current.Y - _bandStart.Y);
        Canvas.SetLeft(BandBorder, x);
        Canvas.SetTop(BandBorder, y);
        BandBorder.Width = rw;
        BandBorder.Height = rh;
        if (rw < 1 && rh < 1) return;

        var rect = new Rect(x, y, rw, rh);
        var selected = new HashSet<FantasyItem>(_bandBase);
        foreach (var item in ItemList.Items.OfType<FantasyItem>())
        {
            if (ItemList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container) continue;
            var p = container.TranslatePoint(new Point(0, 0), ItemList);
            var bounds = new Rect(p, new Size(container.ActualWidth, container.ActualHeight));
            if (rect.IntersectsWith(bounds)) selected.Add(item);
        }
        ItemList.SelectedItems.Clear();
        foreach (var item in selected) ItemList.SelectedItems.Add(item);
    }

    private void EndBandSelection()
    {
        _bandSelecting = false;
        BandBorder.Visibility = Visibility.Collapsed;
        if (Mouse.Captured == ItemList) ItemList.ReleaseMouseCapture();
    }

    private static bool IsInScrollBar(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ScrollBar) return true;
            source = source switch
            {
                Visual => VisualTreeHelper.GetParent(source),
                FrameworkContentElement content => content.Parent,
                _ => null,
            };
        }
        return false;
    }

    private void ItemList_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _menuOnItem = false;
        var container = ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is FantasyItem item)
        {
            _menuOnItem = true;
            if (!ItemList.SelectedItems.Contains(item))
            {
                ItemList.SelectedItems.Clear();
                ItemList.SelectedItem = item;
            }
            ItemList.Focus();
        }
        else if (container == null && !IsInScrollBar(e.OriginalSource as DependencyObject))
        {
            _menuOnItem = false;
            ItemList.SelectedItems.Clear();
            ItemList.Focus();
        }
    }

    private void ItemList_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            switch (e.Key)
            {
                case Key.A:
                    ItemList.SelectAll();
                    e.Handled = true;
                    return;
                case Key.C:
                    CopySelected();
                    e.Handled = true;
                    return;
                case Key.X:
                    CutItem_OnClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.V:
                    PasteClipboard();
                    e.Handled = true;
                    return;
                case Key.Z:
                    Undo();
                    e.Handled = true;
                    return;
                case Key.Y:
                    Redo();
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Delete)
        {
            DeleteSelected(confirm: true);
            e.Handled = true;
        }
    }

    private void ItemList_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemList.SelectedItem is FantasyItem item) EditItem(item);
    }

    private void EditItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is FantasyItem item) EditItem(item);
    }

    private void EditItem(FantasyItem item)
    {
        var before = CaptureHistory();
        var dialog = new FantasyItemDialog(item) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        App.Current.Store.SaveFantasyLibrary();
        RefreshItems();
        BuildSlots();
        RecordHistory(before);
    }

    private void AddGroup_OnClick(object sender, RoutedEventArgs e)
    {
        var before = CaptureHistory();
        var name = $"标签组 {Library.Groups.Count + 1}";
        var group = new FantasyGroup { Name = name };
        Library.Groups.Add(group);
        App.Current.Store.SaveFantasyLibrary();
        Reload();
        GroupCombo.SelectedItem = group.Name;
        RecordHistory(before);
    }

    private void RenameGroup_OnClick(object sender, RoutedEventArgs e)
    {
        var index = GroupCombo.SelectedIndex;
        if (index < 0 || index >= Library.Groups.Count) return;
        var group = Library.Groups[index];
        var dialog = new TextPromptDialog("重命名标签组", group.Name) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        if (string.IsNullOrWhiteSpace(dialog.Result) || dialog.Result == group.Name) return;
        group.Name = dialog.Result;
        App.Current.Store.SaveFantasyLibrary();
        _loading = true;
        GroupCombo.Items[index] = group.Name;
        GroupCombo.SelectedIndex = index;
        _loading = false;
    }

    private void DeleteGroup_OnClick(object sender, RoutedEventArgs e)
    {
        if (GroupCombo.SelectedIndex < 0) return;
        var group = Library.Groups[GroupCombo.SelectedIndex];
        if (MessageBox.Show($"删除标签组“{group.Name}”及其映射？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var before = CaptureHistory();
        var ids = Library.Items.Where(i => i.GroupId == group.Id).Select(i => i.Id).ToHashSet();
        Library.Items.RemoveAll(i => ids.Contains(i.Id));
        Library.Groups.Remove(group);
        foreach (var profile in App.Current.Store.Profiles)
            foreach (var set in profile.Fantasy.Sets)
                for (var i = 0; i < set.Slots.Count; i++)
                    if (set.Slots[i] != null && ids.Contains(set.Slots[i]!)) set.Slots[i] = null;
        App.Current.Store.SaveFantasyLibrary();
        App.Current.Store.Save(App.Current.Engine.Profile);
        Reload();
        RecordHistory(before);
    }

    private void AddItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (GroupCombo.SelectedIndex < 0) return;
        var group = Library.Groups[GroupCombo.SelectedIndex];
        var item = new FantasyItem { GroupId = group.Id, Name = "新映射" };
        var before = CaptureHistory();
        var dialog = new FantasyItemDialog(item) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        Library.Items.Add(item);
        App.Current.Store.SaveFantasyLibrary();
        RefreshItems();
        SelectItems([item]);
        RecordHistory(before);
    }

    private void CopyItem_OnClick(object sender, RoutedEventArgs e) => CopySelected();

    private void CutItem_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedItems();
        if (selected.Count == 0) return;
        foreach (var item in Library.Items) item.IsCut = false;
        foreach (var item in selected) item.IsCut = true;
        _clipboard.Clear();
        foreach (var item in selected)
            _clipboard.Add(new ClipboardEntry { SourceId = item.Id, Item = CloneItem(item, item.GroupId), Cut = true });
        ItemList.Items.Refresh();
        SelectItems(selected);
    }

    private void PasteItem_OnClick(object sender, RoutedEventArgs e) => PasteClipboard();

    private void DeleteItem_OnClick(object sender, RoutedEventArgs e) => DeleteSelected(confirm: true);

    private void ItemMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        var count = SelectedItems().Count;
        var onItem = _menuOnItem;
        EditMenuItem.Visibility = onItem && count == 1 ? Visibility.Visible : Visibility.Collapsed;
        CutMenuItem.Visibility = onItem ? Visibility.Visible : Visibility.Collapsed;
        CopyMenuItem.Visibility = onItem ? Visibility.Visible : Visibility.Collapsed;
        DeleteMenuItem.Visibility = onItem ? Visibility.Visible : Visibility.Collapsed;
        MenuSep1.Visibility = onItem && count == 1 ? Visibility.Visible : Visibility.Collapsed;
        var canPaste = _clipboard.Count > 0 && (!onItem || count == 1);
        PasteMenuItem.Visibility = canPaste ? Visibility.Visible : Visibility.Collapsed;
        MenuSep2.Visibility = onItem && canPaste ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<FantasyItem> SelectedItems() =>
        ItemList.SelectedItems.Cast<FantasyItem>().ToList();

    private void SelectItems(IEnumerable<FantasyItem> items)
    {
        ItemList.SelectedItems.Clear();
        FantasyItem? first = null;
        foreach (var item in items)
        {
            ItemList.SelectedItems.Add(item);
            first ??= item;
        }
        if (first != null) ItemList.ScrollIntoView(first);
    }

    private void RevealMappedItem(FantasySet set, int preferredSlot, bool fallbackToFirst)
    {
        FantasyItem? Find(int slot)
        {
            if (slot < 0 || slot >= set.Slots.Count) return null;
            var id = set.Slots[slot];
            return string.IsNullOrWhiteSpace(id) ? null : Library.Items.FirstOrDefault(x => x.Id == id);
        }

        var slotIndex = preferredSlot;
        var item = Find(slotIndex);
        if (item == null && fallbackToFirst)
        {
            for (var i = 0; i < set.Slots.Count; i++)
            {
                item = Find(i);
                if (item != null)
                {
                    slotIndex = i;
                    break;
                }
            }
        }
        if (item == null) return;

        _selectedSlot = slotIndex;
        Hotbar.Select(slotIndex);
        var groupIndex = Library.Groups.FindIndex(g => g.Id == item.GroupId);
        if (groupIndex < 0) return;
        if (GroupCombo.SelectedIndex != groupIndex) GroupCombo.SelectedIndex = groupIndex;
        else RefreshItems();
        SelectItems([item]);
    }

    private bool CopySelected()
    {
        var selected = SelectedItems();
        if (selected.Count == 0) return false;
        _clipboard.Clear();
        foreach (var item in selected)
            _clipboard.Add(new ClipboardEntry { SourceId = item.Id, Item = CloneItem(item, item.GroupId), Cut = false });
        var hadCut = Library.Items.Any(x => x.IsCut);
        foreach (var item in Library.Items) item.IsCut = false;
        if (hadCut)
        {
            ItemList.Items.Refresh();
            SelectItems(selected);
        }
        return true;
    }

    private void PasteClipboard()
    {
        if (_clipboard.Count == 0 || GroupCombo.SelectedIndex < 0) return;
        var group = Library.Groups[GroupCombo.SelectedIndex];
        var before = CaptureHistory();
        var toInsert = new List<FantasyItem>();
        var movingIds = new HashSet<string>();
        foreach (var entry in _clipboard)
        {
            if (entry.Cut)
            {
                var existing = Library.Items.FirstOrDefault(x => x.Id == entry.SourceId);
                if (existing != null)
                {
                    existing.GroupId = group.Id;
                    toInsert.Add(existing);
                    movingIds.Add(existing.Id);
                    continue;
                }
            }
            toInsert.Add(CloneItem(entry.Item, group.Id));
        }

        foreach (var moving in toInsert.Where(x => movingIds.Contains(x.Id)).ToArray())
            Library.Items.Remove(moving);

        var anchor = SelectedItems().LastOrDefault(x => !movingIds.Contains(x.Id));
        var index = anchor != null ? Library.Items.IndexOf(anchor) + 1 : -1;
        if (index < 0)
        {
            var last = Library.Items.LastOrDefault(x => x.GroupId == group.Id);
            index = last == null ? Library.Items.Count : Library.Items.IndexOf(last) + 1;
        }

        foreach (var item in Library.Items) item.IsCut = false;
        foreach (var item in toInsert) item.IsCut = false;
        Library.Items.InsertRange(Math.Clamp(index, 0, Library.Items.Count), toInsert);
        if (_clipboard.Any(x => x.Cut)) _clipboard.Clear();
        App.Current.Store.SaveFantasyLibrary();
        RefreshItems();
        SelectItems(toInsert);
        RecordHistory(before);
    }

    private void DeleteSelected(bool confirm)
    {
        var selected = SelectedItems();
        if (selected.Count == 0) return;
        if (confirm)
        {
            var message = selected.Count == 1
                ? $"删除映射“{selected[0].DisplayName}”？引用它的格子会被清空。"
                : $"删除选中的 {selected.Count} 个映射？引用它们的格子会被清空。";
            if (MessageBox.Show(message, "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        }

        var before = CaptureHistory();
        var ids = selected.Select(x => x.Id).ToHashSet();
        Library.Items.RemoveAll(x => ids.Contains(x.Id));
        foreach (var profile in App.Current.Store.Profiles)
            foreach (var set in profile.Fantasy.Sets)
                for (var i = 0; i < set.Slots.Count; i++)
                    if (set.Slots[i] != null && ids.Contains(set.Slots[i]!)) set.Slots[i] = null;
        App.Current.Store.SaveFantasyLibrary();
        App.Current.Store.Save(App.Current.Engine.Profile);
        RefreshItems();
        BuildSlots();
        RecordHistory(before);
    }

    private static FantasyItem CloneItem(FantasyItem source, string groupId) => new()
    {
        GroupId = groupId,
        Name = source.Name,
        ImageBase64 = source.ImageBase64,
        IconText = source.IconText,
        IconColor = source.IconColor,
        Action = CloneAction(source.Action),
    };

    private HistoryState CaptureHistory()
    {
        var slots = App.Current.Store.Profiles
            .Select(p => p.Fantasy.Sets.Select(s => s.Slots.ToList()).ToList())
            .ToList();
        return new HistoryState
        {
            Library = JsonSerializer.Serialize(Library),
            Slots = JsonSerializer.Serialize(slots),
        };
    }

    private void RecordHistory(HistoryState before)
    {
        _undo.Add(before);
        if (_undo.Count > 30) _undo.RemoveAt(0);
        _redo.Clear();
        UpdateHistoryButtons();
    }

    private void RestoreHistory(HistoryState state)
    {
        var library = JsonSerializer.Deserialize<FantasyLibrary>(state.Library);
        var slots = JsonSerializer.Deserialize<List<List<List<string?>>>>(state.Slots);
        if (library == null || slots == null) return;

        var groupId = GroupCombo.SelectedIndex >= 0 && GroupCombo.SelectedIndex < Library.Groups.Count
            ? Library.Groups[GroupCombo.SelectedIndex].Id
            : null;
        Library.Groups = library.Groups;
        Library.Items = library.Items;
        var profiles = App.Current.Store.Profiles;
        for (var i = 0; i < profiles.Count && i < slots.Count; i++)
        {
            var sets = profiles[i].Fantasy.Sets;
            for (var j = 0; j < sets.Count && j < slots[i].Count; j++)
                sets[j].Slots = slots[i][j].ToList();
        }

        App.Current.Store.SaveFantasyLibrary();
        foreach (var profile in profiles.ToArray()) App.Current.Store.Save(profile);
        Reload();
        if (groupId != null)
        {
            var index = Library.Groups.FindIndex(g => g.Id == groupId);
            if (index >= 0) GroupCombo.SelectedIndex = index;
        }
        App.Current.Fantasy.NotifyContentChanged();
        UpdateHistoryButtons();
    }

    private void Undo_OnClick(object sender, RoutedEventArgs e) => Undo();
    private void Redo_OnClick(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var current = CaptureHistory();
        var state = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(current);
        RestoreHistory(state);
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        var current = CaptureHistory();
        var state = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(current);
        RestoreHistory(state);
    }

    private void UpdateHistoryButtons()
    {
        if (UndoButton == null || RedoButton == null) return;
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    private void RefreshItems()
    {
        if (GroupCombo.SelectedIndex < 0)
        {
            ItemList.ItemsSource = null;
            return;
        }
        var group = Library.Groups[GroupCombo.SelectedIndex];
        ItemList.ItemsSource = Library.Items.Where(i => i.GroupId == group.Id).ToList();
    }

    private void GroupCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshItems();

    private void SetCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var index = SetCombo.SelectedIndex;
        BuildSlots();
        if (index >= 0)
        {
            App.Current.Fantasy.SelectSet(index);
            RevealMappedItem(Fantasy.Sets[index], _selectedSlot, fallbackToFirst: true);
        }
    }

    private void Enable_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Fantasy.Enabled = EnableSwitch.IsChecked == true;
        EnableText.Text = Fantasy.Enabled ? "已启用" : "关闭";
        App.Current.Store.Save(App.Current.Engine.Profile);
        App.Current.Fantasy.RefreshMode();
        (Window.GetWindow(this) as MainWindow)?.ApplyFantasyMode(Fantasy.Enabled);
    }

    private void RefreshVisibleSets()
    {
        VisibleSetsPanel.Children.Clear();
        for (var i = 0; i < 8; i++)
        {
            var check = new CheckBox
            {
                Content = $"{i + 1}",
                IsChecked = i < Fantasy.VisibleSets.Count && Fantasy.VisibleSets[i],
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 11,
                Tag = i,
            };
            check.Click += (s, _) =>
            {
                if (_loading) return;
                var index = (int)((CheckBox)s!).Tag;
                while (Fantasy.VisibleSets.Count < 8) Fantasy.VisibleSets.Add(true);
                Fantasy.VisibleSets[index] = ((CheckBox)s!).IsChecked == true;
                Save();
            };
            VisibleSetsPanel.Children.Add(check);
        }
    }

    private void Setting_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Fantasy.ExtensionEnabled = ExtensionCheck.IsChecked == true;
        if (LeftExtensionCombo.SelectedIndex >= 0) Fantasy.LeftExtensionSet = LeftExtensionCombo.SelectedIndex;
        if (RightExtensionCombo.SelectedIndex >= 0) Fantasy.RightExtensionSet = RightExtensionCombo.SelectedIndex;
        var levels = SensitivityBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => double.TryParse(x.Trim(), out var v) ? v : 0)
            .Where(v => v > 0)
            .ToList();
        if (levels.Count == 0) levels = [0.5, 1.0, 1.5, 2.2];
        Fantasy.SensitivityLevels = levels;
        Fantasy.SensitivityIndex = Math.Clamp(Fantasy.SensitivityIndex, 0, levels.Count - 1);
        if (LeftStickCombo.SelectedIndex >= 0)
            Fantasy.LeftStickMode = LeftStickCombo.SelectedIndex == 1 ? "wasd" : "arrows";
        Save();
        App.Current.Fantasy.NotifyContentChanged();
    }

    private void SensitivityKey_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Fantasy.SensitivitySwitchKey = SelectedTag(SensitivityKeyCombo, "Capture");
        Save();
        BuildBaseKeys();
        App.Current.Fantasy.NotifyContentChanged();
    }

    private void SystemMenuSetting_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var fantasy = Fantasy;
        fantasy.SystemMenuEnabled = SystemMenuCheck.IsChecked == true;
        fantasy.SystemMenuHotkey = SelectedTag(SystemMenuHotkeyCombo, "Select");

        var confirm = SelectedTag(SystemMenuConfirmCombo, "A");
        var cancel = SelectedTag(SystemMenuCancelCombo, "B");
        if (confirm == cancel)
        {
            cancel = SystemMenuDefaults.FirstOther(confirm);
            SelectComboByTag(SystemMenuCancelCombo, cancel);
        }
        fantasy.SystemMenuConfirmButton = confirm;
        fantasy.SystemMenuCancelButton = cancel;
        Save();
        BuildBaseKeys();
        App.Current.Fantasy.RefreshSystemMenu();
    }

    private void EditSystemMenu_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SystemMenuEditorDialog(Fantasy.SystemMenuCategories) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        Fantasy.SystemMenuCategories = dialog.Categories;
        Save();
        App.Current.Fantasy.RefreshSystemMenu();
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox combo, string fallback) =>
        combo.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() ?? fallback : fallback;

    private void OverlaySetting_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _syncingOverlay) return;
        var visible = OverlayVisibleCheck.IsChecked == true;
        Fantasy.Overlay.ClickThrough = ClickThroughCheck.IsChecked == true;
        App.Current.Fantasy.SetOverlayVisible(visible);
        _syncingOverlay = true;
        OverlayToggle.IsChecked = visible;
        _syncingOverlay = false;
        ClickThroughCheck.IsChecked = Fantasy.Overlay.ClickThrough;
    }

    private void OverlaySlider_OnChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || OverlayScaleText == null || OverlayOpacityText == null) return;
        Fantasy.Overlay.Scale = OverlayScaleSlider.Value;
        Fantasy.Overlay.Opacity = OverlayOpacitySlider.Value;
        OverlayScaleText.Text = $"{OverlayScaleSlider.Value * 100:0}%";
        OverlayOpacityText.Text = $"{OverlayOpacitySlider.Value * 100:0}%";
        Save();
        App.Current.Fantasy.ApplyOverlaySettings();
    }

    private void OverlayToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _syncingOverlay) return;
        var visible = OverlayToggle.IsChecked == true;
        App.Current.Fantasy.SetOverlayVisible(visible);
        _syncingOverlay = true;
        OverlayVisibleCheck.IsChecked = visible;
        _syncingOverlay = false;
        ClickThroughCheck.IsChecked = Fantasy.Overlay.ClickThrough;
    }

    private void SaveAndRefresh()
    {
        Save();
        BuildSlots();
    }

    private void Save() => App.Current.Store.Save(App.Current.Engine.Profile);

    private static BitmapImage? DecodeImage(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static BindingAction CloneAction(BindingAction source) => new()
    {
        Type = source.Type,
        Key = source.Key,
        Text = source.Text,
        Modifiers = [.. source.Modifiers],
        MouseButton = source.MouseButton,
        WheelDelta = source.WheelDelta,
        Turbo = source.Turbo,
        Toggle = source.Toggle,
        TurboIntervalMs = source.TurboIntervalMs,
        MacroId = source.MacroId,
        MacroName = source.MacroName,
        MacroSource = source.MacroSource,
        Macro = source.Macro.Select(s => new MacroStep
        {
            Kind = s.Kind,
            Key = s.Key,
            GamepadSource = s.GamepadSource,
            Modifiers = [.. s.Modifiers],
            HoldMs = s.HoldMs,
            DelayMs = s.DelayMs,
            MouseButton = s.MouseButton,
        }).ToList(),
    };
}

