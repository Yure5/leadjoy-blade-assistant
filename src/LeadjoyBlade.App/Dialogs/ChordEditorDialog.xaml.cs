using System.ComponentModel;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using LeadjoyBlade.Core.Mapping;
using LeadjoyBlade.Core.Protocol;

namespace LeadjoyBlade.App.Dialogs;

public partial class ChordEditorDialog : Window
{
    private readonly ChordBinding _chord;
    private readonly List<SourceCheck> _sourceChecks = [];

    public ChordBinding Result => _chord;

    public ChordEditorDialog(ChordBinding chord)
    {
        InitializeComponent();
        _chord = chord;
        NameBox.Text = chord.Name;

        foreach (var source in MappingSources.All)
        {
            var check = new SourceCheck(source.Id, source.DisplayName)
            {
                IsChecked = chord.Sources.Contains(source.Id),
            };
            _sourceChecks.Add(check);
        }
        SourceList.ItemsSource = _sourceChecks;
        Editor.LoadAction(chord.Action);
    }

    private void Title_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private async void Capture_OnClick(object sender, RoutedEventArgs e)
    {
        if (!App.Current.Devices.IsConnected)
        {
            CaptureHint.Text = "手柄未连接，请先连接或手动勾选按键";
            return;
        }

        CaptureHint.Text = "请同时按住组合键（3 秒）…";
        var tcs = new TaskCompletionSource<BladeButtons>();
        void Handler(object? s, BladeReport report)
        {
            if (BitOperations.PopCount((uint)report.Buttons) >= 2)
                tcs.TrySetResult(report.Buttons);
        }

        App.Current.Devices.ReportReceived += Handler;
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(3000));
            if (completed == tcs.Task)
            {
                var buttons = await tcs.Task;
                foreach (var check in _sourceChecks)
                    check.IsChecked = Enum.TryParse<BladeButtons>(check.Id, out var button) && buttons.HasFlag(button);
                CaptureHint.Text = "已捕获：" + string.Join(" + ",
                    BladeButtonsExtensions.All.Where(b => buttons.HasFlag(b)).Select(b => b.ToShortName()));
            }
            else
            {
                CaptureHint.Text = "未捕获到组合键，请重试";
            }
        }
        finally
        {
            App.Current.Devices.ReportReceived -= Handler;
        }
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = _sourceChecks.Where(c => c.IsChecked).Select(c => c.Id).ToList();
        if (selected.Count < 2)
        {
            CaptureHint.Text = "组合键至少需要 2 个按键";
            return;
        }

        _chord.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? string.Join("+", selected) : NameBox.Text.Trim();
        _chord.Sources = selected;
        _chord.Action = Editor.BuildAction();
        DialogResult = true;
        Close();
    }

    private sealed class SourceCheck : INotifyPropertyChanged
    {
        private bool _isChecked;
        public string Id { get; }
        public string DisplayName { get; }

        public SourceCheck(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
