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
        Model.Studio.Engine.Changed += () => DispatcherQueue.TryEnqueue(UpdateEngineIndicator);
        Navigation.PaneOpening += (_, _) => EngineIndicator.Visibility = Visibility.Visible;
        Navigation.PaneClosing += (_, _) => EngineIndicator.Visibility = Visibility.Collapsed;
        Navigation.SizeChanged += (_, _) => EngineIndicator.Visibility = Navigation.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        Model.Studio.Queue.Items.CollectionChanged += (_, e) => { if (e.NewItems is not null) foreach (Core.WorkItem item in e.NewItems) item.PropertyChanged += (_, _) => RefreshStatus(); };
        AppWindow.Closing += async (_, e) =>
        {
            if (allowClose) return; e.Cancel = true;
            if (Model.Studio.Queue.Busy && !await ConfirmAsync("仍有任务运行", "关闭将取消等待及运行中的任务，已完成文件会保留。", "取消任务并关闭")) return;
            if (pages is not null) await pages.DisposeAsync(); await Model.Studio.DisposeAsync(); allowClose = true; Close();
        };
    }
    private void UpdateEngineIndicator()
    {
        var engine = Model.Studio.Engine;
        var raw = engine.Results.FirstOrDefault(r => r.Id == "ffmpeg")?.Version;
        var version = raw is null ? null : Core.FfmpegVersion.Parse("ffmpeg version " + raw);
        EngineLabel.Text = version?.Release is Version release ? "FFmpeg " + release.ToString(release.Build == 0 ? 2 : 3) : version is not null ? "FFmpeg · 开发版" : "FFmpeg";
        EngineStatus.Text = engine.State switch { Core.EngineState.Ready => "已就绪", Core.EngineState.Checking => "检测中", Core.EngineState.Failed => "不可用", _ => "待检测" };
        var color = engine.State switch
        {
            Core.EngineState.Ready => Windows.UI.Color.FromArgb(255, 42, 168, 118),
            Core.EngineState.Checking => Windows.UI.Color.FromArgb(255, 80, 144, 224),
            Core.EngineState.Failed => Windows.UI.Color.FromArgb(255, 221, 147, 49),
            _ => Windows.UI.Color.FromArgb(255, 140, 140, 140)
        };
        EngineDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        ToolTipService.SetToolTip(EngineIndicator, engine.Detail);
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
        var sizes = Environment.GetCommandLineArgs().Contains("--visual-review") ? new[] { new Windows.Graphics.SizeInt32(1280, 900), new Windows.Graphics.SizeInt32(2560, 1380) } : new[] { new Windows.Graphics.SizeInt32(900, 720), new Windows.Graphics.SizeInt32(1280, 900), new Windows.Graphics.SizeInt32(1920, 1080), new Windows.Graphics.SizeInt32(2560, 1380) };
        foreach (var size in sizes)
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
                    var selectedTabs = Descendants(PageHost).OfType<Pivot>().Select(pivot => pivot.SelectedItem).ToHashSet();
                    foreach (var control in Descendants(PageHost).OfType<FrameworkElement>().Where(e => (e is TextBox or ComboBox or Expander or CheckBox) && e.ActualWidth > 0))
                    {
                        bool visible = true;
                        for (DependencyObject? ancestor = control; ancestor is not null && !ReferenceEquals(ancestor, PageHost); ancestor = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(ancestor))
                            if (ancestor is UIElement { Visibility: Visibility.Collapsed } || ancestor is PivotItem item && !selectedTabs.Contains(item)) { visible = false; break; }
                        if (!visible) continue;
                        var point = control.TransformToVisual(Root).TransformPoint(new(0, 0));
                        if (point.X < left - 1 || point.X + control.ActualWidth > right + 1)
                            failures.Add($"{route}/{state}/{size.Width}: {control.GetType().Name} outside viewport");
                    }
                    samples.Add(new { route, state, window = size.Width, viewportLeft = left, viewportRight = right, pageLeft = host.X, pageRight = host.X + PageHost.ActualWidth });
                }
                Check("normal");
                if (route is "settings" or "extensions")
                {
                    var tabs = Descendants(PageHost).OfType<Pivot>().First();
                    foreach (var item in tabs.Items.OfType<PivotItem>().ToArray())
                    {
                        tabs.SelectedItem = item; await Task.Delay(150); Root.UpdateLayout(); Check("category-" + item.Tag);
                        if (Environment.GetCommandLineArgs().Contains("--capture-ui") && size.Width is 1280 or 2560) await CaptureAsync(route + "-" + item.Tag + "-" + size.Width);
                    }
                    tabs.SelectedIndex = 0; await Task.Delay(250);
                }
                if (route == "expert")
                {
                    var workspace = Descendants(PageHost).OfType<Pivot>().First(pivot => pivot.Tag?.ToString() == "expert-workspace"); workspace.SelectedIndex = 0;
                    var extras = Descendants(PageHost).OfType<Expander>().First(expander => expander.Tag?.ToString() == "form-extra"); extras.IsExpanded = true;
                    var mode = Descendants(PageHost).OfType<ComboBox>().First(combo => combo.Header?.ToString() == "工作区");
                    foreach (var value in new[] { "视频", "音频" })
                    {
                        mode.SelectedItem = value; await Task.Delay(100);
                        var parameters = Descendants(PageHost).OfType<Pivot>().First(pivot => pivot.Tag?.ToString() == "form-tabs");
                        foreach (var item in parameters.Items.OfType<PivotItem>().Where(item => item.IsEnabled).ToArray())
                        {
                            parameters.SelectedItem = item; await Task.Delay(150); Root.UpdateLayout(); Check("parameters-" + value + "-" + item.Tag);
                            if (Environment.GetCommandLineArgs().Contains("--capture-ui") && size.Width == 1280) await CaptureAsync("expert-" + value + "-" + item.Tag);
                        }
                    }
                    mode.SelectedItem = "视频"; extras.IsExpanded = false; workspace.SelectedIndex = 1; await Task.Delay(150); Root.UpdateLayout(); Check("command-editor");
                    if (Environment.GetCommandLineArgs().Contains("--capture-ui") && size.Width == 1280) await CaptureAsync("expert-command"); workspace.SelectedIndex = 0; await Task.Delay(250);
                }
                if (route is "image" or "utilities")
                {
                    var operation = Descendants(PageHost).OfType<ComboBox>().First(combo => combo.Header?.ToString() is "操作" or "工具");
                    for (var index = 0; index < operation.Items.Count; index++)
                    {
                        operation.SelectedIndex = index; await Task.Delay(60); Root.UpdateLayout(); Check("operation-" + index);
                        if (Environment.GetCommandLineArgs().Contains("--capture-ui") && size.Width == 1280 && (route == "image" && index == 2 || route == "utilities" && index is 3 or 7)) await CaptureAsync(route + "-operation-" + index);
                    }
                    operation.SelectedIndex = 0;
                }
                if (route == "tasks" && size.Width == 1280 && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VIDEOLAB_DATA_ROOT")))
                {
                    var firstOutput = Path.Combine(Model.Studio.Settings.Root, "ui-output-1.txt"); var secondOutput = Path.Combine(Model.Studio.Settings.Root, "ui-output-2.txt");
                    await File.WriteAllTextAsync(firstOutput, "UI fixture"); await File.WriteAllTextAsync(secondOutput, "UI fixture");
                    var complete = new TaskCompletionSource<Core.WorkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var running = Model.Studio.Queue.AddAsync("界面检查示例", "示例素材.mp4", (_, _) => complete.Task);
                    var fixture = Model.Studio.Queue.Items.Last(); var list = Descendants(PageHost).OfType<ListView>().First(); list.SelectedItem = fixture; await Task.Delay(100);
                    complete.SetResult(new Core.WorkResult([firstOutput, secondOutput], [])); await running; await Task.Delay(150); Root.UpdateLayout(); Check("selected-task");
                    if (!Descendants(list).OfType<TextBlock>().Any(text => text.Text == "完成")) failures.Add("tasks: the selected row did not update its completed status");
                    var output = Descendants(PageHost).OfType<ComboBox>().First(combo => combo.Header?.ToString()?.StartsWith("输出文件") == true); output.SelectedIndex = 1; await Task.Delay(100);
                    if (!Descendants(PageHost).OfType<TextBox>().Any(box => box.IsReadOnly && box.Text == secondOutput)) failures.Add("tasks: selecting an output did not update its path");
                    if (Environment.GetCommandLineArgs().Contains("--capture-ui")) await CaptureAsync("tasks-selected"); Model.Studio.Queue.Items.Remove(fixture); File.Delete(firstOutput); File.Delete(secondOutput);
                }
                if (route == "download")
                {
                    var primary = Descendants(PageHost).OfType<Button>().First(b => b.Content?.ToString() == "加入下载队列");
                    var primaryPosition = primary.TransformToVisual(PageScroll).TransformPoint(new(0, 0));
                    if (size.Height >= 900 && (primaryPosition.Y < 0 || primaryPosition.Y + primary.ActualHeight > PageScroll.ActualHeight)) failures.Add($"download/{size.Width}: primary action is outside the initial viewport");
                    if (primary.IsEnabled) failures.Add($"download/{size.Width}: empty input enables queue submission");
                    var expanders = Descendants(PageHost).OfType<Expander>().ToArray(); foreach (var expander in expanders) expander.IsExpanded = true;
                    var links = Descendants(PageHost).OfType<TextBox>().First(e => e.Header?.ToString()?.StartsWith("媒体链接") == true);
                    links.Text = "https://example.com/" + new string('x', 512);
                    await Task.Delay(400); Root.UpdateLayout(); Check("expanded-long-link");
                    if (!primary.IsEnabled) failures.Add($"download/{size.Width}: valid input does not enable queue submission");
                    if (Environment.GetCommandLineArgs().Contains("--capture-ui")) await CaptureAsync("download-expanded-" + size.Width);
                    var optionTabs = Descendants(PageHost).OfType<Pivot>().First(pivot => pivot.Tag?.ToString() == "download-options-tabs");
                    foreach (var tab in optionTabs.Items.OfType<PivotItem>().ToArray())
                    {
                        optionTabs.SelectedItem = tab; await Task.Delay(200); Root.UpdateLayout(); Check("advanced-" + tab.Tag);
                        if (tab.Tag?.ToString() == "custom")
                        {
                            var formatOption = Descendants(PageHost).OfType<TextBox>().First(e => e.Header?.ToString() == "自定义格式选择");
                            if (formatOption.ActualWidth < 240) failures.Add($"download/{size.Width}: custom format field is too narrow ({formatOption.ActualWidth:F1})");
                        }
                        if (!Environment.GetCommandLineArgs().Contains("--capture-ui")) continue;
                        var optionsTop = expanders.First(e => e.Header?.ToString() == "高级下载参数").TransformToVisual(PageHost).TransformPoint(new(0, 0)).Y;
                        PageScroll.ChangeView(null, optionsTop, null, true); await Task.Delay(200); Root.UpdateLayout();
                        await CaptureAsync("download-options-" + tab.Tag + "-" + size.Width);
                        if (tab.Tag?.ToString() == "format") await CaptureAsync("download-options-" + size.Width);
                    }
                    optionTabs.SelectedIndex = 0; await Task.Delay(100);
                    var audioQuality = Descendants(PageHost).OfType<ComboBox>().First(combo => combo.Header?.ToString() == "音频质量");
                    var originalQuality = audioQuality.Text; audioQuality.Text = "128K"; optionTabs.SelectedIndex = 2; await Task.Delay(100); optionTabs.SelectedIndex = 0; await Task.Delay(100);
                    if (audioQuality.Text != "128K") failures.Add("download: changing categories lost the custom audio quality"); audioQuality.SelectedIndex = -1; audioQuality.Text = originalQuality;
                    optionTabs.SelectedIndex = 2; await Task.Delay(100); optionTabs.SelectedIndex = 0; await Task.Delay(100);
                    if (audioQuality.Text != originalQuality) failures.Add("download: clearing custom audio quality did not restore the default");
                    var showingLogin = pages!.ShowCookieSettingsAsync();
                    await Task.Delay(300);
                    var loginDialog = pages.CookieSettingsDialog!;
                    var loginContent = (FrameworkElement)loginDialog.Content;
                    void CheckLogin(string state)
                    {
                        foreach (var control in Descendants(loginContent).OfType<FrameworkElement>().Where(e => e is TextBox or ComboBox or Expander))
                        {
                            bool visible = control.ActualWidth > 0;
                            for (DependencyObject? ancestor = control; ancestor is not null && !ReferenceEquals(ancestor, loginContent); ancestor = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(ancestor))
                                if (ancestor is UIElement { Visibility: Visibility.Collapsed }) { visible = false; break; }
                            if (visible && control.ActualWidth > loginContent.ActualWidth + 1) failures.Add($"download/{state}/{size.Width}: login control exceeds dialog width");
                        }
                        samples.Add(new { route, state, window = size.Width, dialogWidth = loginContent.ActualWidth });
                    }
                    var loginMode = Descendants(loginContent).OfType<ComboBox>().First(c => c.Header?.ToString() == "登录方式");
                    var originalLoginMode = loginMode.SelectedItem;
                    await pages.ShowCookieSettingsAsync();
                    loginMode.SelectedItem = loginMode.Items.OfType<ComboBoxItem>().First(item => item.Tag?.ToString() == "files");
                    await Task.Delay(200); CheckLogin("cookie-import");
                    if (Environment.GetCommandLineArgs().Contains("--capture-ui")) await CaptureAsync("download-cookies-" + size.Width, loginDialog);
                    var importMethod = Descendants(loginContent).OfType<ComboBox>().First(c => c.Header?.ToString() == "如何导入？");
                    importMethod.SelectedIndex = 1; await Task.Delay(200); CheckLogin("cookie-paste");
                    if (Environment.GetCommandLineArgs().Contains("--capture-ui")) await CaptureAsync("download-cookie-paste-" + size.Width, loginDialog);
                    loginMode.SelectedItem = loginMode.Items.OfType<ComboBoxItem>().First(item => item.Tag?.ToString() == "browser");
                    var source = Descendants(loginContent).OfType<ComboBox>().First(c => c.Header?.ToString() == "使用哪个浏览器？");
                    source.SelectedItem = source.Items.OfType<ComboBoxItem>().First(item => item.Tag?.ToString() == "firefox");
                    foreach (var expander in Descendants(loginContent).OfType<Expander>()) expander.IsExpanded = true;
                    await Task.Delay(200); CheckLogin("firefox-profile");
                    if (Environment.GetCommandLineArgs().Contains("--capture-ui")) await CaptureAsync("download-firefox-" + size.Width, loginDialog);
                    loginDialog.Hide(); await showingLogin;
                    if (!ReferenceEquals(loginMode.SelectedItem, originalLoginMode)) failures.Add($"download/{size.Width}: cancelling login settings kept the edited mode");
                    if (size.Width == 1280 && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VIDEOLAB_DATA_ROOT")))
                    {
                        void Invoke(Button button)
                        {
                            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
                            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
                        }
                        var importing = pages.ShowCookieSettingsAsync(); await Task.Delay(200);
                        loginMode.SelectedItem = loginMode.Items.OfType<ComboBoxItem>().First(item => item.Tag?.ToString() == "files");
                        importMethod.SelectedIndex = 1;
                        Descendants(loginContent).OfType<TextBox>().First(t => t.Header?.ToString() == "这个登录信息用于哪个网站？").Text = "";
                        Descendants(loginContent).OfType<TextBox>().First(t => t.Header?.ToString() == "粘贴 Cookie 内容").Text = "[{\"domain\":\".ui-cookie-test.invalid\",\"name\":\"fixture\",\"value\":\"synthetic\"}]";
                        Invoke(Descendants(loginContent).OfType<Button>().First(b => b.Content?.ToString() == "导入并使用"));
                        await importing.WaitAsync(TimeSpan.FromSeconds(5));
                        if (Core.CookieService.Resolve(Model.Studio.Settings, "https://ui-cookie-test.invalid/video").Length == 0 || System.Text.Json.Nodes.JsonNode.Parse(Model.Studio.Settings.Get("yt_dlp_options"))?["cookie-browser"]?.ToString() != "files") failures.Add("download: importing did not close the dialog and activate the saved Cookie");
                        samples.Add(new { route, state = "cookie-import-completes", window = size.Width });
                        await Core.CookieService.DeleteAsync(Model.Studio.Settings, "ui-cookie-test.invalid");
                        var saving = pages.ShowCookieSettingsAsync(); await Task.Delay(200);
                        loginMode.SelectedItem = loginMode.Items.OfType<ComboBoxItem>().First(item => item.Tag?.ToString() == "none");
                        Invoke(Descendants(loginDialog).OfType<Button>().First(b => b.Content?.ToString() == "保存设置"));
                        await saving.WaitAsync(TimeSpan.FromSeconds(5));
                        if (System.Text.Json.Nodes.JsonNode.Parse(Model.Studio.Settings.Get("yt_dlp_options"))?["cookie-browser"]?.ToString() != "none") failures.Add("download: saving login settings did not activate the selected mode");
                        samples.Add(new { route, state = "cookie-save-completes", window = size.Width });
                    }
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
    private async Task CaptureAsync(string name, FrameworkElement? target = null)
    {
        if (Environment.GetCommandLineArgs().Contains("--visual-review")) await Task.Delay(250);
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(target ?? Root);
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
    public async Task NavigateToSettingsAsync(string category)
    {
        await NavigateAsync("settings");
        var tabs = Descendants(PageHost).OfType<Pivot>().FirstOrDefault(pivot => pivot.Tag?.ToString() == "settings-tabs");
        var item = tabs?.Items.OfType<PivotItem>().FirstOrDefault(item => item.Tag?.ToString() == category);
        if (tabs is not null && item is not null) tabs.SelectedItem = item;
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
