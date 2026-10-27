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
    public StackPanel Panel { get; } = new() { Spacing = 14 };
    public event Action? Changed;
    public EditorForm(MainWindow window) => this.window = window;
    public TextBox Text(string key, string label, string value = "", bool multiline = false, string? placeholder = null)
    {
        var box = new TextBox { Header = label, Text = value, PlaceholderText = placeholder ?? "", AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 88 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.TextChanged += (_, _) => Changed?.Invoke(); fields[key] = box; containers[key] = box; Panel.Children.Add(box); return box;
    }
    public PasswordBox Secret(string key, string label, string value = "")
    { var box = new PasswordBox { Header = label, Password = value }; box.PasswordChanged += (_, _) => Changed?.Invoke(); fields[key] = box; containers[key] = box; Panel.Children.Add(box); return box; }
    public ComboBox Select(string key, string label, IEnumerable<string> choices, string? selected = null, bool editable = false)
    {
        var box = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch, IsEditable = editable };
        foreach (var item in choices) box.Items.Add(item); box.SelectedIndex = 0;
        if (selected is not null) { if (box.Items.Contains(selected)) box.SelectedItem = selected; else if (editable) box.Text = selected; }
        box.SelectionChanged += (_, _) => Changed?.Invoke(); if (editable) box.TextSubmitted += (_, _) => Changed?.Invoke();
        fields[key] = box; containers[key] = box; Panel.Children.Add(box); return box;
    }
    public ToggleSwitch Toggle(string key, string label, bool value = false)
    { var box = new ToggleSwitch { Header = label, IsOn = value, OnContent = "开启", OffContent = "关闭" }; box.Toggled += (_, _) => Changed?.Invoke(); fields[key] = box; containers[key] = box; Panel.Children.Add(box); return box; }
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
    public string Get(string key) => fields.GetValueOrDefault(key) switch { TextBox t => t.Text.Trim(), PasswordBox p => p.Password, ComboBox c => c.IsEditable && !string.IsNullOrWhiteSpace(c.Text) ? c.Text : c.SelectedItem?.ToString() ?? "", ToggleSwitch t => t.IsOn ? "true" : "false", _ => "" };
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
                case ComboBox c: if (c.Items.Contains(p.Value)) c.SelectedItem = p.Value; else if (c.IsEditable) c.Text = p.Value; break;
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
    public void Visible(string key, bool visible) { if (containers.TryGetValue(key, out var c)) c.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }
    public void Arrange(string[][] rows, string? advancedTitle = null, string[]? advanced = null)
    {
        Panel.Children.Clear(); var used = new HashSet<string>();
        Grid GridFor(IEnumerable<string[]> layout)
        {
            var grid = new Grid { ColumnSpacing = 18, RowSpacing = 14 }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
            foreach (var row in layout)
            {
                var position = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                for (int i = 0; i < row.Length; i++) if (containers.TryGetValue(row[i], out var child)) { used.Add(row[i]); Grid.SetRow(child, position); Grid.SetColumn(child, i); Grid.SetColumnSpan(child, row.Length == 1 ? 2 : 1); grid.Children.Add(child); }
            }
            return grid;
        }
        Panel.Children.Add(GridFor(rows));
        if (advanced is not null) Panel.Children.Add(new Expander { Header = advancedTitle, HorizontalAlignment = HorizontalAlignment.Stretch, Content = GridFor(advanced.Chunk(2).Select(c => c.ToArray())), IsExpanded = false });
        foreach (var pair in containers.Where(p => !used.Contains(p.Key))) Panel.Children.Add(pair.Value);
    }
}
