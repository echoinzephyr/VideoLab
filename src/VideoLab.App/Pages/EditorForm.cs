using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace VideoLab.App.Pages;

internal sealed class EditorForm
{
    private readonly MainWindow window;
    private readonly Dictionary<string, Control> fields = [];
    private readonly Dictionary<string, FrameworkElement> containers = [];
    private readonly List<Action> layouts = [];
    public StackPanel Panel { get; } = new() { Spacing = 14 };
    public event Action? Changed;
    public EditorForm(MainWindow window) => this.window = window;
    public TextBox Text(string key, string label, string value = "", bool multiline = false, string? placeholder = null)
    {
        var box = new TextBox { Header = label, Text = value, PlaceholderText = placeholder ?? "", AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 88 : 0, MaxHeight = multiline ? 180 : double.PositiveInfinity, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (multiline) ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        box.TextChanged += (_, _) => Changed?.Invoke(); fields[key] = box; containers[key] = box; Panel.Children.Add(box); return box;
    }
    public PasswordBox Secret(string key, string label, string value = "")
    { var box = new PasswordBox { Header = label, Password = value }; box.PasswordChanged += (_, _) => Changed?.Invoke(); fields[key] = box; containers[key] = box; Panel.Children.Add(box); return box; }
    public ComboBox Select(string key, string label, IEnumerable<string> choices, string? selected = null, bool editable = false, IReadOnlyDictionary<string, string>? labels = null)
    {
        var box = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch, IsEditable = editable };
        foreach (var item in choices) box.Items.Add(labels is null ? item : new ComboBoxItem { Content = labels.GetValueOrDefault(item, item), Tag = item }); box.SelectedIndex = editable ? -1 : 0;
        if (selected is not null) { var choice = box.Items.FirstOrDefault(item => item is ComboBoxItem mapped ? mapped.Tag?.ToString() == selected : item?.ToString() == selected); if (choice is not null) box.SelectedItem = choice; else if (editable) box.Text = selected; }
        box.SelectionChanged += (_, _) => Changed?.Invoke();
        if (editable)
        {
            box.TextSubmitted += (_, args) => { args.Handled = true; Changed?.Invoke(); };
            box.RegisterPropertyChangedCallback(ComboBox.TextProperty, (sender, _) => { var combo = (ComboBox)sender; if (combo.Text.Length == 0 && combo.SelectedIndex >= 0) combo.SelectedIndex = -1; Changed?.Invoke(); });
        }
        fields[key] = box; containers[key] = box; Panel.Children.Add(box); return box;
    }
    public ToggleSwitch Toggle(string key, string label, bool value = false)
    { var box = new ToggleSwitch { Header = label, IsOn = value, OnContent = "开启", OffContent = "关闭" }; box.Toggled += (_, _) => Changed?.Invoke(); fields[key] = box; containers[key] = box; Panel.Children.Add(box); return box; }
    public CheckBox Check(string key, string label, bool value = false)
    {
        var box = new CheckBox { Content = label, IsChecked = value, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.Checked += (_, _) => Changed?.Invoke(); box.Unchecked += (_, _) => Changed?.Invoke();
        fields[key] = box; containers[key] = box; Panel.Children.Add(box); return box;
    }
    public TextBox Path(string key, string label, string value = "", bool directory = false, bool multiple = false)
    {
        var box = Text(key, label, value, multiple); Panel.Children.Remove(box); var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.Children.Add(box);
        var browse = Button(directory ? "浏览" : multiple ? "添加文件" : "浏览", async () =>
        {
            if (directory) { var folder = await window.PickDirectoryAsync(); if (folder is not null) box.Text = folder; }
            else { var paths = await window.PickFilesAsync(multiple); if (paths.Length > 0) box.Text = string.Join(Environment.NewLine, paths); }
        }); browse.VerticalAlignment = VerticalAlignment.Bottom; Grid.SetColumn(browse, 1); row.Children.Add(browse);
        if (!directory)
        {
            box.AllowDrop = true;
            box.DragOver += (_, e) => { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; };
            box.Drop += async (_, e) => await window.RunActionAsync(async () => { if (e.DataView.Contains(StandardDataFormats.StorageItems)) { var paths = (await e.DataView.GetStorageItemsAsync()).Select(f => f.Path); box.Text = string.Join(Environment.NewLine, multiple ? paths : paths.Take(1)); } });
        }
        containers[key] = row; Panel.Children.Add(row); return box;
    }
    public string Get(string key) => fields.GetValueOrDefault(key) switch { TextBox t => t.Text.Trim(), PasswordBox p => p.Password, ComboBox c => c.IsEditable ? c.Text.Trim() : c.SelectedItem is ComboBoxItem mapped ? mapped.Tag?.ToString() ?? "" : c.SelectedItem?.ToString() ?? "", ToggleSwitch t => t.IsOn ? "true" : "false", CheckBox c => c.IsChecked == true ? "true" : "false", _ => "" };
    public int Index(string key) => fields[key] is ComboBox c ? c.SelectedIndex : 0;
    public bool Flag(string key) => Get(key) == "true";
    public int Int(string key, int fallback = 0) => string.IsNullOrWhiteSpace(Get(key)) ? fallback : int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new FormatException($"{key} 需要有效整数。");
    public double Number(string key, double fallback = 0) => string.IsNullOrWhiteSpace(Get(key)) ? fallback : double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : throw new FormatException($"{key} 需要有效数字。");
    public string[] Lines(string key) => Get(key).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public Dictionary<string, string> Values() => fields.Keys.ToDictionary(k => k, Get);
    public void Apply(IReadOnlyDictionary<string, string> values)
    {
        foreach (var p in values)
        {
            switch (fields.GetValueOrDefault(p.Key))
            {
                case TextBox t: t.Text = p.Value; break;
                case PasswordBox t: t.Password = p.Value; break;
                case ToggleSwitch t: t.IsOn = p.Value == "true"; break;
                case CheckBox c: c.IsChecked = p.Value == "true"; break;
                case ComboBox c: var choice = c.Items.FirstOrDefault(item => item is ComboBoxItem mapped ? mapped.Tag?.ToString() == p.Value : item?.ToString() == p.Value); if (choice is not null) c.SelectedItem = choice; else if (c.IsEditable) c.Text = p.Value; break;
            }
        }
        Changed?.Invoke();
    }
    public Button Button(string text, Func<Task> action, bool accent = false)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += async (_, _) => await window.RunActionAsync(action); return button;
    }
    public void Enable(string key, bool enabled) { if (fields.TryGetValue(key, out var c)) c.IsEnabled = enabled; }
    public void Visible(string key, bool visible)
    {
        if (!containers.TryGetValue(key, out var container)) return;
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed; if (container.Visibility == visibility) return;
        container.Visibility = visibility; foreach (var layout in layouts) layout();
    }
    public FrameworkElement Take(string key)
    {
        var container = containers[key]; Panel.Children.Remove(container); return container;
    }
    private Grid FieldGrid(string[][] rows)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 14 };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        foreach (var key in rows.SelectMany(row => row)) grid.Children.Add(containers[key]);
        string? previous = null;
        void Layout()
        {
            var columns = grid.ActualWidth >= 520 ? 2 : 1;
            var visibleRows = rows.Select(row => row.Where(key => containers[key].Visibility == Visibility.Visible).ToArray()).Where(row => row.Length > 0).ToArray();
            var signature = columns + ":" + string.Join(";", visibleRows.Select(row => string.Join(",", row)));
            if (signature == previous) return; previous = signature;
            grid.Visibility = visibleRows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            grid.ColumnSpacing = columns == 2 ? 16 : 0;
            grid.ColumnDefinitions[1].Width = columns == 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            grid.RowDefinitions.Clear();
            foreach (var row in visibleRows)
            {
                var position = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                for (var i = 0; i < row.Length; i++)
                {
                    var child = containers[row[i]];
                    if (columns == 1 && i > 0) { position++; grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); }
                    Grid.SetRow(child, position); Grid.SetColumn(child, columns == 1 ? 0 : i); Grid.SetColumnSpan(child, columns == 2 && row.Length == 1 ? 2 : 1);
                }
            }
        }
        layouts.Add(Layout); grid.SizeChanged += (_, _) => Layout(); Layout(); return grid;
    }
    private Pivot FieldTabs(params (string Key, string Title, string Description, string[][] Rows)[] sections)
    {
        var pivot = new Pivot { HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "form-tabs" };
        var entries = new List<(PivotItem Item, string[] Keys)>();
        foreach (var section in sections)
        {
            var body = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
            body.Children.Add(new TextBlock { Text = section.Description, FontSize = 12, Opacity = .65, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(FieldGrid(section.Rows));
            var item = new PivotItem { Header = new TextBlock { Text = section.Title, FontSize = 14 }, Tag = section.Key, Content = body, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0) };
            item.SizeChanged += (_, args) => { if (args.NewSize.Width > 32) body.Width = Math.Min(860, args.NewSize.Width - 32); };
            entries.Add((item, section.Rows.SelectMany(row => row).ToArray())); pivot.Items.Add(item);
        }
        void SelectActive()
        {
            if (pivot.SelectedItem is PivotItem { IsEnabled: true }) return;
            var next = entries.Select(entry => entry.Item).FirstOrDefault(item => item.IsEnabled);
            if (next is not null && !ReferenceEquals(next, pivot.SelectedItem)) pivot.SelectedItem = next;
        }
        void UpdateTabs()
        {
            foreach (var entry in entries)
            {
                entry.Item.IsEnabled = entry.Keys.Any(key => containers[key].Visibility == Visibility.Visible);
                if (entry.Item.Header is TextBlock header) header.Opacity = entry.Item.IsEnabled ? 1 : .35;
            }
            pivot.DispatcherQueue.TryEnqueue(() => SelectActive());
        }
        pivot.SelectionChanged += (_, _) => pivot.DispatcherQueue.TryEnqueue(() => SelectActive()); layouts.Add(UpdateTabs); UpdateTabs(); return pivot;
    }
    public Pivot ArrangeTabs(params (string Key, string Title, string Description, string[][] Rows)[] sections)
    {
        Panel.Children.Clear(); var pivot = FieldTabs(sections); Panel.Children.Add(pivot); return pivot;
    }
    public Pivot ArrangeWithTabs(string[][] rows, params (string Key, string Title, string Description, string[][] Rows)[] sections)
    {
        Panel.Children.Clear(); Panel.Children.Add(FieldGrid(rows)); var pivot = FieldTabs(sections);
        Panel.Children.Add(new Expander { Header = "编码、滤镜与其他参数", Tag = "form-extra", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = pivot }); return pivot;
    }
    public void Arrange(string[][] rows, string? advancedTitle = null, string[]? advanced = null)
    {
        Panel.Children.Clear(); var used = rows.SelectMany(row => row).ToHashSet(); Panel.Children.Add(FieldGrid(rows));
        if (advanced is not null)
        {
            foreach (var key in advanced) used.Add(key);
            Panel.Children.Add(new Expander { Header = advancedTitle, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = FieldGrid(advanced.Chunk(2).Select(row => row.ToArray()).ToArray()) });
        }
        foreach (var pair in containers.Where(pair => !used.Contains(pair.Key))) Panel.Children.Add(pair.Value);
    }
}
