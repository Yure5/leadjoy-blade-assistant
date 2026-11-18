using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LeadjoyBlade.Core.Mapping;

namespace LeadjoyBlade.App.Dialogs;

public partial class SystemMenuEditorDialog : Window
{
    private sealed class CategoryClipboardEntry
    {
        public required string SourceId { get; init; }
        public required SystemMenuCategory Category { get; init; }
        public bool Cut { get; init; }
    }

    private sealed class ItemClipboardEntry
    {
        public required string SourceId { get; init; }
        public required SystemMenuItem Item { get; init; }
        public bool Cut { get; init; }
    }

    private readonly List<SystemMenuCategory> _categories;
    private readonly List<CategoryClipboardEntry> _categoryClipboard = [];
    private readonly List<ItemClipboardEntry> _itemClipboard = [];
    private bool _loading;
    private SystemMenuCategory? _category;
    private bool _categoryMenuOnItem;
    private bool _itemMenuOnItem;

    public List<SystemMenuCategory> Categories { get; private set; } = [];

    public SystemMenuEditorDialog(IEnumerable<SystemMenuCategory> categories)
    {
        InitializeComponent();
        _categories = categories.Select(c => c.Clone()).ToList();
        if (_categories.Count == 0)
            _categories.Add(new SystemMenuCategory { Name = "常用" });
        _loading = true;
        CategoryList.ItemsSource = _categories;
        _loading = false;
        CategoryList.SelectedIndex = 0;
    }

    private void CategoryList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateCategoryUi();
        UpdateEditorButtons();
    }

    private void ItemList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateEditorButtons();
    }

    private void UpdateEditorButtons()
    {
        if (CategoryRenameButton == null || ItemEditButton == null) return;
        var hasCategory = CategoryList.SelectedItem is SystemMenuCategory;
        CategoryRenameButton.IsEnabled = hasCategory;
        CategoryCutButton.IsEnabled = hasCategory;
        CategoryCopyButton.IsEnabled = hasCategory;
        CategoryDeleteButton.IsEnabled = hasCategory;

        var count = SelectedItems().Count;
        ItemEditButton.IsEnabled = count == 1;
        ItemCutButton.IsEnabled = count > 0;
        ItemCopyButton.IsEnabled = count > 0;
        ItemDeleteButton.IsEnabled = count > 0;
    }

    private void UpdateCategoryUi()
    {
        _category = CategoryList.SelectedItem as SystemMenuCategory;
        ItemList.ItemsSource = _category?.Items;
        CategoryHeader.Text = _category == null ? "项目" : _category.Name;
        CategoryMeta.Text = _category == null ? "" : $"{_category.Items.Count} 项";
        CategoryCountText.Text = $"{_categories.Count} 个菜单栏";
        UpdateEditorButtons();
    }

    private void RefreshCategories()
    {
        _loading = true;
        var selected = CategoryList.SelectedIndex;
        CategoryList.ItemsSource = null;
        CategoryList.ItemsSource = _categories;
        CategoryList.SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, _categories.Count - 1));
        _loading = false;
        UpdateCategoryUi();
        CategoryList.Items.Refresh();
    }

    private void RefreshItems()
    {
        if (_category == null) return;
        var selected = ItemList.SelectedIndex;
        ItemList.ItemsSource = null;
        ItemList.ItemsSource = _category.Items;
        if (_category.Items.Count > 0)
            ItemList.SelectedIndex = Math.Clamp(selected, 0, _category.Items.Count - 1);
        CategoryMeta.Text = $"{_category.Items.Count} 项";
        ItemList.Items.Refresh();
        UpdateEditorButtons();
    }

    // ================= 复制 / 剪切 / 粘贴（逻辑同双十字热键栏） =================

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T found) return found;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static SystemMenuItem CloneItemWithNewId(SystemMenuItem source)
    {
        var clone = source.Clone();
        clone.Id = NewId();
        clone.IsCut = false;
        return clone;
    }

    private static SystemMenuCategory CloneCategoryWithNewIds(SystemMenuCategory source)
    {
        var clone = source.Clone();
        clone.Id = NewId();
        clone.IsCut = false;
        foreach (var item in clone.Items)
        {
            item.Id = NewId();
            item.IsCut = false;
        }
        return clone;
    }

    private void ClearCategoryCutMarks()
    {
        foreach (var category in _categories) category.IsCut = false;
    }

    private void ClearItemCutMarks()
    {
        foreach (var category in _categories)
            foreach (var item in category.Items)
                item.IsCut = false;
    }

    private void CategoryList_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _categoryMenuOnItem = row != null;
        if (row != null)
        {
            row.IsSelected = true;
            CategoryList.SelectedItem = row.DataContext;
        }
    }

    private void ItemList_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _itemMenuOnItem = row != null;
        if (row == null) return;
        if (!row.IsSelected)
        {
            ItemList.SelectedItems.Clear();
            row.IsSelected = true;
            ItemList.SelectedItem = row.DataContext;
        }
    }

    private void CategoryMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        var onItem = _categoryMenuOnItem && CategoryList.SelectedItem is SystemMenuCategory;
        CategoryRenameMenuItem.Visibility = onItem ? Visibility.Visible : Visibility.Collapsed;
        CategoryCutMenuItem.Visibility = onItem ? Visibility.Visible : Visibility.Collapsed;
        CategoryCopyMenuItem.Visibility = onItem ? Visibility.Visible : Visibility.Collapsed;
        CategoryDeleteMenuItem.Visibility = onItem ? Visibility.Visible : Visibility.Collapsed;
        CategoryMenuSep1.Visibility = onItem ? Visibility.Visible : Visibility.Collapsed;
        var canPaste = _categoryClipboard.Count > 0;
        CategoryPasteMenuItem.Visibility = canPaste ? Visibility.Visible : Visibility.Collapsed;
        CategoryMenuSep2.Visibility = onItem && canPaste ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ItemMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        var selected = SelectedItems();
        var onItem = _itemMenuOnItem && selected.Count > 0;
        ItemEditMenuItem.Visibility = onItem && selected.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        ItemCutMenuItem.Visibility = onItem ? Visibility.Visible : Visibility.Collapsed;
        ItemCopyMenuItem.Visibility = onItem ? Visibility.Visible : Visibility.Collapsed;
        ItemDeleteMenuItem.Visibility = onItem ? Visibility.Visible : Visibility.Collapsed;
        ItemMenuSep1.Visibility = onItem ? Visibility.Visible : Visibility.Collapsed;
        var canPaste = _itemClipboard.Count > 0 && (!onItem || selected.Count == 1);
        ItemPasteMenuItem.Visibility = canPaste ? Visibility.Visible : Visibility.Collapsed;
        ItemMenuSep2.Visibility = onItem && canPaste ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CategoryList_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { DeleteCategory_OnClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.F2) { RenameCategory_OnClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.C) { CopyCategory_OnClick(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.X) { CutCategory_OnClick(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.V) { PasteCategory_OnClick(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private void ItemList_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { DeleteItem_OnClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.Enter) { EditSelectedItem(); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.C) { CopyItem_OnClick(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.X) { CutItem_OnClick(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.V) { PasteItem_OnClick(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private void CopyCategory_OnClick(object sender, RoutedEventArgs e)
    {
        if (CategoryList.SelectedItem is not SystemMenuCategory category) return;
        ClearCategoryCutMarks();
        _categoryClipboard.Clear();
        _categoryClipboard.Add(new CategoryClipboardEntry
        {
            SourceId = category.Id,
            Category = category.Clone(),
            Cut = false,
        });
        CategoryList.Items.Refresh();
    }

    private void CutCategory_OnClick(object sender, RoutedEventArgs e)
    {
        if (CategoryList.SelectedItem is not SystemMenuCategory category) return;
        ClearCategoryCutMarks();
        category.IsCut = true;
        _categoryClipboard.Clear();
        _categoryClipboard.Add(new CategoryClipboardEntry
        {
            SourceId = category.Id,
            Category = category.Clone(),
            Cut = true,
        });
        CategoryList.Items.Refresh();
    }

    private void PasteCategory_OnClick(object sender, RoutedEventArgs e)
    {
        if (_categoryClipboard.Count == 0) return;
        var insertAt = _categoryMenuOnItem && CategoryList.SelectedIndex >= 0
            ? CategoryList.SelectedIndex + 1
            : _categories.Count;
        SystemMenuCategory? last = null;
        foreach (var entry in _categoryClipboard)
        {
            if (entry.Cut)
            {
                var existing = _categories.FirstOrDefault(x => x.Id == entry.SourceId);
                if (existing != null)
                {
                    var oldIndex = _categories.IndexOf(existing);
                    _categories.RemoveAt(oldIndex);
                    if (oldIndex < insertAt) insertAt--;
                    existing.IsCut = false;
                    _categories.Insert(Math.Clamp(insertAt++, 0, _categories.Count), existing);
                    last = existing;
                }
                else
                {
                    var clone = CloneCategoryWithNewIds(entry.Category);
                    _categories.Insert(Math.Clamp(insertAt++, 0, _categories.Count), clone);
                    last = clone;
                }
            }
            else
            {
                var clone = CloneCategoryWithNewIds(entry.Category);
                _categories.Insert(Math.Clamp(insertAt++, 0, _categories.Count), clone);
                last = clone;
            }
        }
        if (_categoryClipboard.Any(x => x.Cut)) _categoryClipboard.Clear();
        RefreshCategories();
        if (last != null) CategoryList.SelectedItem = last;
    }

    private List<SystemMenuItem> SelectedItems() =>
        ItemList.SelectedItems.Cast<SystemMenuItem>().ToList();

    private void CopyItem_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedItems();
        if (selected.Count == 0) return;
        ClearItemCutMarks();
        _itemClipboard.Clear();
        foreach (var item in selected)
            _itemClipboard.Add(new ItemClipboardEntry
            {
                SourceId = item.Id,
                Item = item.Clone(),
                Cut = false,
            });
        ItemList.Items.Refresh();
    }

    private void CutItem_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedItems();
        if (selected.Count == 0) return;
        ClearItemCutMarks();
        foreach (var item in selected) item.IsCut = true;
        _itemClipboard.Clear();
        foreach (var item in selected)
            _itemClipboard.Add(new ItemClipboardEntry
            {
                SourceId = item.Id,
                Item = item.Clone(),
                Cut = true,
            });
        ItemList.Items.Refresh();
    }

    private void PasteItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_category == null || _itemClipboard.Count == 0) return;
        var insertAt = _category.Items.Count;
        if (_itemMenuOnItem && ItemList.SelectedItems.Count > 0)
        {
            var indexes = ItemList.SelectedItems.Cast<SystemMenuItem>()
                .Select(x => _category.Items.IndexOf(x)).Where(x => x >= 0).ToList();
            if (indexes.Count > 0) insertAt = indexes.Max() + 1;
        }

        var inserted = new List<SystemMenuItem>();
        foreach (var entry in _itemClipboard)
        {
            SystemMenuItem? target = null;
            if (entry.Cut)
            {
                foreach (var owner in _categories)
                {
                    var existing = owner.Items.FirstOrDefault(x => x.Id == entry.SourceId);
                    if (existing == null) continue;
                    var sourceIndex = owner.Items.IndexOf(existing);
                    owner.Items.RemoveAt(sourceIndex);
                    if (owner == _category && sourceIndex < insertAt) insertAt--;
                    existing.IsCut = false;
                    target = existing;
                    break;
                }
                target ??= CloneItemWithNewId(entry.Item);
            }
            else
            {
                target = CloneItemWithNewId(entry.Item);
            }
            _category.Items.Insert(Math.Clamp(insertAt++, 0, _category.Items.Count), target);
            inserted.Add(target);
        }
        if (_itemClipboard.Any(x => x.Cut)) _itemClipboard.Clear();
        ClearItemCutMarks();
        RefreshItems();
        ItemList.SelectedItems.Clear();
        foreach (var item in inserted) ItemList.SelectedItems.Add(item);
        if (inserted.Count > 0) ItemList.ScrollIntoView(inserted[^1]);
    }

    private void AddCategory_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new TextPromptDialog("新建菜单栏", $"菜单栏 {_categories.Count + 1}") { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Result)) return;
        _categories.Add(new SystemMenuCategory { Name = dialog.Result.Trim() });
        RefreshCategories();
        CategoryList.SelectedIndex = _categories.Count - 1;
    }

    private void RenameCategory_OnClick(object sender, RoutedEventArgs e)
    {
        if (_category == null) return;
        var dialog = new TextPromptDialog("重命名菜单栏", _category.Name) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Result)) return;
        _category.Name = dialog.Result.Trim();
        RefreshCategories();
    }

    private void DeleteCategory_OnClick(object sender, RoutedEventArgs e)
    {
        if (_category == null) return;
        if (MessageBox.Show($"删除菜单栏“{_category.Name}”及其中 {_category.Items.Count} 个项目？",
                "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _categories.Remove(_category);
        _category = null;
        RefreshCategories();
    }

    private void MoveCategoryUp_OnClick(object sender, RoutedEventArgs e) => MoveCategory(-1);
    private void MoveCategoryDown_OnClick(object sender, RoutedEventArgs e) => MoveCategory(1);

    private void MoveCategory(int delta)
    {
        if (_category == null) return;
        var index = _categories.IndexOf(_category);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _categories.Count) return;
        (_categories[index], _categories[target]) = (_categories[target], _categories[index]);
        RefreshCategories();
        CategoryList.SelectedIndex = target;
    }

    private void AddItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_category == null) return;
        var dialog = new SystemMenuItemDialog(new SystemMenuItem { Name = "新项目" }) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _category.Items.Add(dialog.Result);
        RefreshItems();
        ItemList.SelectedIndex = _category.Items.Count - 1;
    }

    private void EditItem_OnClick(object sender, RoutedEventArgs e) => EditSelectedItem();

    private void ItemList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedItem();

    private void EditSelectedItem()
    {
        if (_category == null || ItemList.SelectedItem is not SystemMenuItem item) return;
        var dialog = new SystemMenuItemDialog(item) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var index = _category.Items.IndexOf(item);
        if (index < 0) return;
        _category.Items[index] = dialog.Result;
        dialog.Result.IsCut = false;
        RefreshItems();
        ItemList.SelectedIndex = index;
    }

    private void DeleteItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_category == null) return;
        var selected = SelectedItems();
        if (selected.Count == 0) return;
        var message = selected.Count == 1
            ? $"删除项目“{selected[0].Name}”？"
            : $"删除选中的 {selected.Count} 个项目？";
        if (MessageBox.Show(message, "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        foreach (var item in selected) _category.Items.Remove(item);
        RefreshItems();
        UpdateCategoryUi();
    }

    private void MoveItemUp_OnClick(object sender, RoutedEventArgs e) => MoveItem(-1);
    private void MoveItemDown_OnClick(object sender, RoutedEventArgs e) => MoveItem(1);

    private void MoveItem(int delta)
    {
        if (_category == null || ItemList.SelectedItem is not SystemMenuItem item) return;
        var index = _category.Items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _category.Items.Count) return;
        (_category.Items[index], _category.Items[target]) = (_category.Items[target], _category.Items[index]);
        RefreshItems();
        ItemList.SelectedIndex = target;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        Categories = _categories.Select(c => c.Clone()).ToList();
        DialogResult = true;
        Close();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Title_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
