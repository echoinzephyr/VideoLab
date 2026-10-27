using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
using Microsoft.UI.Xaml.Media.Imaging;
using VideoLab.App.Pages;
using VideoLab.App.ViewModels;

namespace VideoLab.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel Model { get; }
    private StudioPages? pages;
    private bool allowClose;
    private bool selecting;
    private int navigationVersion;
    private readonly Dictionary<string, FrameworkElement> cached = [];
    public MainWindow()
    {
        InitializeComponent(); ExtendsContentIntoTitleBar = true; SetTitleBar(TitleBar);
        PageScroll.SizeChanged += (_, _) => UpdatePageWidth();
        AppWindow.Resize(new(1280, 900)); Model = new();
        LogList.ItemsSource = Model.Studio.Queue.Logs;
        Model.Studio.Queue.Updated += RefreshStatus;
        Model.Studio.Queue.Items.CollectionChanged += (_, e) => { if (e.NewItems is not null) foreach (Core.WorkItem item in e.NewItems) item.PropertyChanged += (_, _) => RefreshStatus(); };
        AppWindow.Closing += async (_, e) =>
        {
            if (allowClose) return; e.Cancel = true;
            if (Model.Studio.Queue.Busy && !await ConfirmAsync("仍有任务运行", "关闭将取消等待及运行中的任务，已完成文件会保留。", "取消任务并关闭")) return;
            if (pages is not null) await pages.DisposeAsync(); await Model.Studio.DisposeAsync(); allowClose = true; Close();
        };
    }
    public async Task InitializeAsync()
    {
        try
        {
            await Model.Studio.InitializeAsync(); pages = new(this, Model.Studio); ApplyTheme(Model.Studio.Settings.Get("theme", "system"));
            RebuildNavigation(); RefreshStatus();
            if (Environment.GetCommandLineArgs().Contains("--smoke-test"))
            {
                foreach (var route in new[] { "convert", "audio", "compress", "trim", "expert", "image", "utilities", "extensions", "download", "settings", "tasks", "about" })
                {
                    await NavigateAsync(route); Root.UpdateLayout();
                    if (route == "expert")
                    {
                        var mode = Descendants(PageHost).OfType<ComboBox>().First(c => c.Header?.ToString() == "工作区");
                        mode.SelectedItem = "音频"; mode.SelectedItem = "视频";
                        var format = Descendants(PageHost).OfType<ComboBox>().First(c => c.Header?.ToString() == "输出格式");
                        format.SelectedItem = "webm"; format.SelectedItem = "mp4";
                    }
                    if (Environment.GetCommandLineArgs().Contains("--capture-ui")) { await Task.Delay(200); await CaptureAsync(route); }
                }
                if (Environment.GetCommandLineArgs().Contains("--capture-ui") || Environment.GetCommandLineArgs().Contains("--layout-probe")) await VerifyPageLayoutsAsync();
                if (Environment.GetCommandLineArgs().Contains("--capture-ui")) { ApplyTheme("light"); await NavigateAsync("convert"); await Task.Delay(250); await CaptureAsync("convert-light"); }
                if (Environment.GetCommandLineArgs().Contains("--capture-ui"))
                {
                    ApplyTheme("dark"); AppWindow.Resize(new(900, 720)); await NavigateAsync("convert"); await Task.Delay(300); await CaptureAsync("convert-dark-compact");
                }
                var modules = Process.GetCurrentProcess().Modules.OfType<ProcessModule>().Where(m => m.ModuleName is "coreclr.dll" or "Microsoft.UI.Xaml.dll").Select(m => m.FileName).ToArray();
                var report = new { started = true, native = Model.Studio.Native.Version, pages = cached.Count, plugins = Model.Studio.Plugins.Store.Installed.Keys.ToArray(), framework = "WinUI 3", runtimeModules = modules, selfContained = modules.Length == 2 && modules.All(p => p.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)) };
                await File.WriteAllTextAsync(Path.Combine(Model.Studio.Settings.Root, "smoke-test.json"), JsonSerializer.Serialize(report));
                await pages.DisposeAsync(); await Model.Studio.DisposeAsync(); allowClose = true; Close();
            }
        }
        catch (Exception error)
        {
            ShowError(error.Message);
            if (Environment.GetCommandLineArgs().Contains("--smoke-test")) { Directory.CreateDirectory(Model.Studio.Settings.Root); await File.WriteAllTextAsync(Path.Combine(Model.Studio.Settings.Root, "smoke-error.log"), error.ToString()); Environment.ExitCode = 1; allowClose = true; Close(); }
        }
    }

    private void UpdatePageWidth()
    {
        // Give the presenter a measured width, not just MaxWidth: WinUI can
        // otherwise center using the smaller intrinsic content width and then
        // stretch the page beyond the right edge when the viewport is wide.
        var available = Math.Max(0, PageScroll.ActualWidth - PageScroll.Padding.Left - PageScroll.Padding.Right);
        PageHost.Width = Math.Min(PageHost.MaxWidth, available);
    }

    private async Task VerifyPageLayoutsAsync()
    {
        var samples = new List<object>(); var failures = new List<string>();
        var routes = new[] { "convert", "audio", "compress", "trim", "expert", "image", "utilities", "extensions", "download", "settings", "tasks", "about" };
        foreach (var size in new[] { new Windows.Graphics.SizeInt32(900, 720), new Windows.Graphics.SizeInt32(1280, 900), new Windows.Graphics.SizeInt32(1920, 1080), new Windows.Graphics.SizeInt32(2560, 1380) })
        {
            AppWindow.Resize(size); await Task.Delay(200);
            foreach (var route in routes)
            {
                await NavigateAsync(route); await Task.Delay(50); Root.UpdateLayout();
                void Check(string state)
                {
                    var viewport = PageScroll.TransformToVisual(Root).TransformPoint(new(0, 0));
                    var host = PageHost.TransformToVisual(Root).TransformPoint(new(0, 0));
                    var left = viewport.X + PageScroll.Padding.Left; var right = viewport.X + PageScroll.ActualWidth - PageScroll.Padding.Right;
                    if (host.X < left - 1 || host.X + PageHost.ActualWidth > right + 1 || PageHost.ActualWidth > PageHost.MaxWidth + 1)
                        failures.Add($"{route}/{state}/{size.Width}: page [{host.X:F1}, {host.X + PageHost.ActualWidth:F1}] outside viewport [{left:F1}, {right:F1}]");
                    foreach (var control in Descendants(PageHost).OfType<FrameworkElement>().Where(e => (e is TextBox or ComboBox or Expander) && e.ActualWidth > 0))
                    {
                        bool visible = true;
                        for (DependencyObject? ancestor = control; ancestor is not null && !ReferenceEquals(ancestor, PageHost); ancestor = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(ancestor))
                            if (ancestor is UIElement { Visibility: Visibility.Collapsed }) { visible = false; break; }
                        if (!visible) continue;
                        var point = control.TransformToVisual(Root).TransformPoint(new(0, 0));
                        if (point.X < left - 1 || point.X + control.ActualWidth > right + 1)
                            failures.Add($"{route}/{state}/{size.Width}: {control.GetType().Name} outside viewport");
                    }
                    samples.Add(new { route, state, window = size.Width, viewportLeft = left, viewportRight = right, pageLeft = host.X, pageRight = host.X + PageHost.ActualWidth });
                }
                Check("normal");
                if (route == "download")
                {
                    var expanders = Descendants(PageHost).OfType<Expander>().ToArray(); foreach (var expander in expanders) expander.IsExpanded = true;
                    var links = Descendants(PageHost).OfType<TextBox>().First(e => e.Header?.ToString()?.StartsWith("媒体链接") == true);
                    links.Text = "https://example.com/" + new string('x', 512);
                    await Task.Delay(400); Root.UpdateLayout(); Check("expanded-long-link");
                    if (Environment.GetCommandLineArgs().Contains("--capture-ui")) await CaptureAsync("download-expanded-" + size.Width);
                    foreach (var expander in expanders) expander.IsExpanded = false; links.Text = ""; PageScroll.ChangeView(0, 0, null);
                    await Task.Delay(400); Root.UpdateLayout();
                    if (Environment.GetCommandLineArgs().Contains("--capture-ui")) await CaptureAsync("download-fixed-" + size.Width);
                }
            }
        }
        var report = new { cases = samples.Count, failures, samples };
        await File.WriteAllTextAsync(Path.Combine(Model.Studio.Settings.Root, "layout-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        if (failures.Count > 0) throw new InvalidOperationException("页面宽度验证失败：" + string.Join("; ", failures.Take(8)));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private async Task CaptureAsync(string name)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(Root);
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) throw new InvalidOperationException("页面未完成布局。");
        var pixels = await bitmap.GetPixelsAsync(); byte[] bytes = new byte[pixels.Length]; DataReader.FromBuffer(pixels).ReadBytes(bytes);
        var directory = Path.Combine(Model.Studio.Settings.Root, "screenshots"); Directory.CreateDirectory(directory);
        var folder = await StorageFolder.GetFolderFromPathAsync(directory); var file = await folder.CreateFileAsync(name + ".png", CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes); await encoder.FlushAsync();
    }
    public void RebuildNavigation()
    {
        var previous = (Navigation.SelectedItem as NavigationViewItem)?.Tag as string;
        Navigation.MenuItems.Clear(); Navigation.FooterMenuItems.Clear();
        foreach (var (tag, label, glyph) in new[] { ("convert", "格式转换", "\uE8AB"), ("audio", "提取音频", "\uE8D6"), ("compress", "视频压缩", "\uE8B8"), ("trim", "视频剪切", "\uE8C6"), ("expert", "专业工作台", "\uE756"), ("image", "图像处理", "\uEB9F"), ("utilities", "更多工具", "\uE90F") }) Add(Navigation.MenuItems, tag, label, glyph);
        Navigation.MenuItems.Add(new NavigationViewItemSeparator()); Add(Navigation.MenuItems, "extensions", "扩展中心", "\uEA86");
        foreach (var plugin in Model.Studio.Plugins.Store.Installed.Values.Where(p => Model.Studio.Plugins.Enabled(p.Manifest.Id)))
            Add(Navigation.MenuItems, plugin.Manifest.Id == "yt-dlp" ? "download" : "plugin:" + plugin.Manifest.Id, plugin.Manifest.Name, "\uE896");
        Add(Navigation.FooterMenuItems, "tasks", "任务与结果", "\uE9D9"); Add(Navigation.FooterMenuItems, "settings", "设置", "\uE713"); Add(Navigation.FooterMenuItems, "about", "关于", "\uE946");
        Navigation.SelectedItem = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == previous) ?? Navigation.MenuItems[0];
        static void Add(IList<object> list, string tag, string label, string glyph) => list.Add(new NavigationViewItem { Tag = tag, Content = label, Icon = new FontIcon { Glyph = glyph } });
    }
    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    { if (!selecting && args.SelectedItem is NavigationViewItem { Tag: string route }) await RunActionAsync(() => NavigateAsync(route)); }
    public async Task NavigateAsync(string route)
    {
        if (pages is null) return; ShowError("", false);
        var version = ++navigationVersion;
        var selected = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == route);
        if (selected is not null && !ReferenceEquals(Navigation.SelectedItem, selected)) { selecting = true; try { Navigation.SelectedItem = selected; } finally { selecting = false; } }
        if (route is "extensions" or "tasks" or "settings" || route.StartsWith("plugin:") || route.StartsWith("config:")) cached.Remove(route);
        if (!cached.TryGetValue(route, out var page)) cached[route] = page = await pages.BuildAsync(route);
        if (version != navigationVersion) return;
        PageHost.Children.Clear(); PageHost.Children.Add(page); PageScroll.ChangeView(null, 0, null);
    }
    public void ApplyTheme(string theme) => Root.RequestedTheme = theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    public void ShowError(string message, bool open = true) { Notice.Severity = InfoBarSeverity.Error; Notice.Title = "操作未完成"; Notice.Message = message; Notice.IsOpen = open; }
    public void ShowMessage(string message) { Notice.Severity = InfoBarSeverity.Informational; Notice.Title = "VideoLab"; Notice.Message = message; Notice.IsOpen = true; }
    public async Task RunActionAsync(Func<Task> action) { try { await action(); } catch (OperationCanceledException) { ShowMessage("已取消。"); } catch (Exception error) { Model.Studio.Queue.Log(error.Message); ShowError(error.Message); } }
    public async Task<bool> ConfirmAsync(string title, string message, string primary = "确认")
    { var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = message, PrimaryButtonText = primary, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }; return await dialog.ShowAsync() == ContentDialogResult.Primary; }
    public async Task<string[]> PickFilesAsync(bool multiple = false)
    {
        var picker = new FileOpenPicker(); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this)); picker.FileTypeFilter.Add("*");
        if (multiple) return (await picker.PickMultipleFilesAsync()).Select(f => f.Path).ToArray();
        var file = await picker.PickSingleFileAsync(); return file is null ? [] : [file.Path];
    }
    public async Task<string?> PickDirectoryAsync()
    {
        var picker = new FolderPicker(); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this)); picker.FileTypeFilter.Add("*"); return (await picker.PickSingleFolderAsync())?.Path;
    }
    public async Task<string?> SaveFileAsync(string extension, string name)
    {
        var picker = new FileSavePicker { SuggestedFileName = name }; picker.FileTypeChoices.Add(extension, ["." + extension]); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this)); return (await picker.PickSaveFileAsync())?.Path;
    }
    public static void Copy(string value) { var data = new DataPackage(); data.SetText(value); Clipboard.SetContent(data); }
    public static void Open(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme == "https") { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); return; }
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("路径已不存在。", path);
        Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true });
    }
    private void RefreshStatus()
    {
        DispatcherQueue.TryEnqueue(() => { StatusTitle.Text = Model.Title; StatusDetail.Text = Model.Detail; Progress.Value = Model.Progress; Progress.IsIndeterminate = Model.Studio.Queue.Busy && Model.Progress == 0; CancelButton.IsEnabled = Model.Studio.Queue.Busy; if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]); });
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Model.Studio.Queue.CancelAll();
    private void Logs_Click(object sender, RoutedEventArgs e) => LogColumn.Width = LogsToggle.IsChecked == true ? new GridLength(AppWindow.Size.Width < 1000 ? 250 : 330) : new GridLength(0);
    private void CopyLogs_Click(object sender, RoutedEventArgs e) => Copy(string.Join(Environment.NewLine, Model.Studio.Queue.Logs));
    private void ClearLogs_Click(object sender, RoutedEventArgs e) => Model.Studio.Queue.Logs.Clear();
}
