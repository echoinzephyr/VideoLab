using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using VideoLab.Core;

namespace VideoLab.App.Pages;

internal sealed partial class StudioPages
{
    private async Task<FrameworkElement> SettingsAsync()
    {
        var form = Form(); var root = Page("设置", "按类别管理常规偏好、外部工具、硬件加速与本机数据。");
        form.Path("download_path", "默认保存位置", studio.Settings.Get("download_path"), directory: true);
        form.Select("theme", "应用外观", ["system", "light", "dark"], studio.Settings.Get("theme", "system"), labels: new Dictionary<string, string> { ["system"] = "跟随系统", ["light"] = "浅色", ["dark"] = "深色" });
        form.Path("ffmpeg_path", "FFmpeg 可执行文件或目录", studio.Settings.Get("ffmpeg_path"));
        form.Path("yt_dlp_path", "yt-dlp 可执行文件", studio.Settings.Get("yt_dlp_path"));
        form.Path("aria2_path", "Aria2 可执行文件（可选）", studio.Settings.Get("aria2_path"));
        form.Check("use_aria2", "使用 Aria2 多线程下载", studio.Settings.Flag("use_aria2"));
        var general = new StackPanel { Spacing = 18 }; general.Children.Add(form.Take("download_path")); general.Children.Add(form.Take("theme"));
        var tools = new StackPanel { Spacing = 16 }; tools.Children.Add(Note("可选择可执行文件、工具目录或含 bin 的解压目录。必需工具留空时从 PATH 查找。"));
        foreach (var key in new[] { "ffmpeg_path", "yt_dlp_path", "aria2_path", "use_aria2" }) tools.Children.Add(form.Take(key));
        var status = Note("更改后点击“保存设置”。"); var engineStatus = Note(studio.Engine.Summary); var engineDetail = Note(studio.Engine.Detail);
        var diagnostic = new Expander { Header = "查看检测详情", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = engineDetail };
        void UpdateEngine() => window.DispatcherQueue.TryEnqueue(() => { engineStatus.Text = studio.Engine.State == EngineState.Ready ? "FFmpeg 与 ffprobe 已就绪。" : studio.Engine.Summary; engineDetail.Text = studio.Engine.Detail; });
        engineStatus.Loaded += (_, _) => { studio.Engine.Changed += UpdateEngine; UpdateEngine(); }; engineStatus.Unloaded += (_, _) => studio.Engine.Changed -= UpdateEngine;
        var revision = 0; form.Changed += () => { ++revision; status.Text = "设置已修改，保存后生效。"; };
        async Task SaveAsync(bool verifyEngine = true)
        {
            var changed = studio.Settings.Get("ffmpeg_path") != form.Get("ffmpeg_path");
            foreach (var pair in form.Values()) if (pair.Key == "use_aria2") studio.Settings.Set(pair.Key, pair.Value == "true"); else studio.Settings.Set(pair.Key, pair.Value);
            await studio.Settings.SaveAsync(); window.ApplyTheme(form.Get("theme")); status.Text = "设置已保存。";
            if (changed && verifyEngine) await studio.Engine.VerifyAsync(studio.Ffmpeg);
        }
        root.Children.Add(Row(form.Button("保存设置", () => SaveAsync(), true))); root.Children.Add(status);
        tools.Children.Add(Row(form.Button("保存并检测依赖", async () =>
        {
            var current = ++revision; await SaveAsync(false); engineStatus.Text = "正在检测依赖…";
            var ytDlp = studio.Settings.Get("yt_dlp_path"); var aria2 = studio.Settings.Get("aria2_path"); var useAria2 = studio.Settings.Flag("use_aria2");
            await studio.Engine.VerifyAsync(studio.Ffmpeg); var checks = studio.Engine.Results.ToList();
            checks.Add(await DependencyChecker.ExecutableAsync("yt-dlp", ytDlp, "yt-dlp", true)); checks.Add(await DependencyChecker.ExecutableAsync("aria2", aria2, "aria2c", useAria2));
            if (current != revision) return;
            var failures = checks.Count(check => !check.CanRun); engineStatus.Text = failures == 0 ? "工具检测通过。" : $"有 {failures} 项依赖需要处理。";
            engineDetail.Text = DependencyChecker.Describe(checks); diagnostic.IsExpanded = failures > 0;
        }), form.Button("安装 / 更新 yt-dlp", async () =>
        {
            studio.Plugins.EnsureIdle("yt-dlp"); var path = await DependencyInstaller.YtDlpAsync(Path.Combine(studio.Settings.ExtensionsRoot, "tools"), CancellationToken.None);
            form.Apply(new Dictionary<string, string> { ["yt_dlp_path"] = path }); studio.Settings.Set("yt_dlp_path", path); await studio.Settings.SaveAsync(); status.Text = "yt-dlp 已安装并校验。";
        })));
        tools.Children.Add(engineStatus); tools.Children.Add(diagnostic);
        var gpu = Form(); gpu.Check("gpu_acceleration", "使用检测通过的硬件编码器", studio.Settings.Flag("gpu_acceleration"));
        var gpuChoices = new Dictionary<string, ComboBox>();
        foreach (var family in new[] { "h264", "hevc", "av1", "vp9" })
        {
            var choices = new[] { "" }.Concat(UsableGpu().Where(encoder => encoder.StartsWith(family + "_"))).ToArray();
            gpuChoices[family] = gpu.Select("gpu_" + family, family.ToUpperInvariant() + " 编码器", choices, studio.Settings.Get("gpu_" + family), labels: choices.ToDictionary(value => value, value => value.Length == 0 ? "使用 CPU" : value));
        }
        gpu.Arrange([["gpu_acceleration"], ["gpu_h264", "gpu_hevc"], ["gpu_av1", "gpu_vp9"]]);
        var gpuStatus = Note(UsableGpu().Length == 0 ? "尚未检测可用编码器，请先检测并试编码。" : "可用编码器：" + string.Join("、", UsableGpu()));
        var hardware = new StackPanel { Spacing = 16 }; hardware.Children.Add(Note("先检测本机能力，再选择通过试编码的编码器。留空或选择 CPU 时使用软件编码。")); hardware.Children.Add(gpu.Panel); hardware.Children.Add(gpuStatus);
        hardware.Children.Add(Row(form.Button("检测并试编码", async () =>
        {
            await SaveAsync(); var work = await studio.Queue.AddAsync("硬件编码检测", studio.Ffmpeg, async (_, token) => { await studio.DetectHardwareAsync(token); return new([], []); });
            if (!work.Finished || work.Cancellation.IsCancellationRequested || work.Errors.Count > 0) { gpuStatus.Text = "检测未完成，请在任务详情中查看原因。"; return; }
            foreach (var pair in gpuChoices)
            {
                pair.Value.Items.Clear(); foreach (var value in new[] { "" }.Concat(UsableGpu().Where(encoder => encoder.StartsWith(pair.Key + "_")))) pair.Value.Items.Add(new ComboBoxItem { Tag = value, Content = value.Length == 0 ? "使用 CPU" : value });
                pair.Value.SelectedItem = pair.Value.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == studio.Settings.Get("gpu_" + pair.Key)) ?? pair.Value.Items[0];
            }
            gpuStatus.Text = UsableGpu().Length == 0 ? "本机未检测到通过试编码的硬件编码器。" : "检测通过：" + string.Join("、", UsableGpu());
        }), form.Button("保存硬件选择", async () =>
        {
            var selected = gpu.Values().Where(pair => pair.Key != "gpu_acceleration" && pair.Value.Length > 0).ToArray();
            if (selected.Any(pair => !UsableGpu().Contains(pair.Value))) throw new InvalidOperationException("编码器尚未通过本机检测。");
            foreach (var pair in gpu.Values().Where(pair => pair.Key != "gpu_acceleration")) studio.Settings.Set(pair.Key, pair.Value);
            studio.Settings.Set("gpu_acceleration", gpu.Flag("gpu_acceleration") && selected.Length > 0); await studio.Settings.SaveAsync(); gpuStatus.Text = "硬件编码选择已保存。";
        })));
        var data = new StackPanel { Spacing = 16 }; data.Children.Add(Note("本机数据位置：" + studio.Settings.Root));
        data.Children.Add(Note(studio.Settings.MigrationWarning.Length == 0 ? "旧版导入会保留来源文件，只迁移设置、Cookie 与预设。" : studio.Settings.MigrationWarning));
        data.Children.Add(Row(form.Button("打开数据文件夹", () => { MainWindow.Open(studio.Settings.Root); return Task.CompletedTask; }), form.Button("重新导入旧版数据", async () => { studio.Settings.Set("legacy_imported_v1", false); await studio.Settings.LoadAsync(); var message = studio.Settings.MigrationWarning.Length == 0 ? "旧版数据导入完成。" : studio.Settings.MigrationWarning; await window.NavigateAsync("settings"); window.ShowMessage(message); })));
        root.Children.Add(new Border { Style = (Style)Application.Current.Resources["CardStyle"], Child = PageTabs("settings-tabs", ("general", "常规", general), ("tools", "工具与依赖", tools), ("hardware", "硬件加速", hardware), ("data", "数据管理", data)) });
        await Task.CompletedTask; return root;
    }
    private FrameworkElement About()
    {
        var root = Page("关于 VideoLab", "原生 Windows 音视频工作台 · WinUI 3 + Fluent Design"); var form = Form();
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "26.10.27-winui.1+8";
        var releaseText = Note("VideoLab 是独立的原生 Windows 音视频工作台。"); ReleaseInfo? latest = null;
        root.Children.Add(Card("VideoLab", new TextBlock { Text = "FFmpeg Studio", FontSize = 22 }, Note("应用版本：" + version), Note("媒体核心：" + studio.Native.Version), Note("GPL-3.0-only · 本机媒体处理 · 外部 FFmpeg / ffprobe"), releaseText));
        root.Children.Add(Row(form.Button("检查正式版更新", async () => { latest = await UpdateService.LatestAsync(); releaseText.Text = "最新正式发行版：" + latest.Version + "\n" + (UpdateService.CompareVersions(latest.Version, version.Split('+')[0]) > 0 ? "有新的正式版可下载。" : "当前无需更新。") + "\n更新仅来自 VideoLab 仓库。"; }), form.Button("打开发行页面", () => { MainWindow.Open(latest?.Url ?? UpdateService.Repository + "/releases"); return Task.CompletedTask; }), form.Button("下载 Windows 正式包", async () =>
        {
            latest ??= await UpdateService.LatestAsync(); var asset = latest.Assets.FirstOrDefault(n => n!["name"]!.GetValue<string>().EndsWith("windows-x64.zip"))?.AsObject() ?? throw new InvalidOperationException("该版本没有 Windows x64 包。");
            var digest = asset["digest"]?.GetValue<string>(); if (digest is null || !digest.StartsWith("sha256:")) throw new InvalidDataException("发布文件未提供可验证的 SHA-256。");
            var folder = await window.PickDirectoryAsync(); if (folder is null) return;
            var bytes = await UpdateService.VerifiedDownloadAsync(asset["browser_download_url"]!.GetValue<string>(), asset["size"]!.GetValue<long>(), digest[7..], CancellationToken.None);
            var path = Path.Combine(folder, Path.GetFileName(asset["name"]!.GetValue<string>())); if (File.Exists(path)) throw new IOException("目标文件已存在。"); await File.WriteAllBytesAsync(path, bytes); window.ShowMessage("下载并校验完成：" + path);
        })));
        root.Children.Add(Card("项目与许可", Row(form.Button("GitHub", () => { MainWindow.Open(UpdateService.Repository); return Task.CompletedTask; }), form.Button("源代码", () => { MainWindow.Open(UpdateService.Repository + "/tree/main"); return Task.CompletedTask; }), form.Button("LICENSE", () => { MainWindow.Open(Path.Combine(AppContext.BaseDirectory, "LICENSE")); return Task.CompletedTask; })))); return root;
    }
    private FrameworkElement Tasks()
    {
        var root = Page("任务与结果", "页面切换不会中断任务。选择一项任务可查看输出与失败原因。"); var form = Form(); var list = new ListView { ItemsSource = studio.Queue.Items, SelectionMode = ListViewSelectionMode.Single, MaxHeight = 420 };
        list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Spacing="4" Margin="4,8">
                <TextBlock Text="{Binding Name}" FontWeight="SemiBold"/>
                <TextBlock Text="{Binding Status}" TextWrapping="Wrap" MaxLines="2" TextTrimming="CharacterEllipsis" FontSize="12"/>
                <TextBlock Text="{Binding Input}" TextTrimming="CharacterEllipsis" MaxLines="1" Opacity="0.65" FontSize="12"/>
              </StackPanel>
            </DataTemplate>
            """);
        list.ItemContainerStyle = new Style { TargetType = typeof(ListViewItem) };
        list.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var empty = Note("还没有任务。配置好素材或下载链接后，任务会显示在这里。");
        Button? cancelSelected = null; Button? cancelAll = null;
        var details = new StackPanel { Spacing = 10 };
        void ShowItem()
        {
            details.Children.Clear(); if (list.SelectedItem is not WorkItem item) { details.Children.Add(Note("从队列中选择任务，查看输出文件与诊断信息。")); return; }
            details.Children.Add(Note(item.Input));
            var state = Note(""); state.SetBinding(TextBlock.TextProperty, new Binding { Source = item, Path = new PropertyPath("Status") }); details.Children.Add(state);
            if (item.Finished) details.Children.Add(Note($"耗时 {item.Elapsed.TotalSeconds:N1} 秒 · 输出 {item.Bytes / 1024d:N1} KB"));
            if (item.Paths.Count == 0 && item.Errors.Count == 0) details.Children.Add(Note(item.Finished ? "任务已结束，未返回输出文件。" : "任务进行中，完成后将在这里显示输出。"));
            foreach (var error in item.Errors) details.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = error });
            if (item.Paths.Count > 0)
            {
                var outputs = new ComboBox { Header = $"输出文件（{item.Paths.Count}）", HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var path in item.Paths) outputs.Items.Add(new ComboBoxItem { Content = Path.GetFileName(path).Length > 0 ? Path.GetFileName(path) : path, Tag = path });
                outputs.SelectedIndex = 0;
                string CurrentPath() => (outputs.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
                var outputPath = new TextBox { Text = CurrentPath(), IsReadOnly = true };
                outputs.SelectionChanged += (_, _) => outputPath.Text = CurrentPath();
                details.Children.Add(outputs); details.Children.Add(outputPath);
                details.Children.Add(Row(form.Button("打开文件", () => { MainWindow.Open(CurrentPath()); return Task.CompletedTask; }), form.Button("打开所在文件夹", () => { MainWindow.Open(Path.GetDirectoryName(Path.GetFullPath(CurrentPath()))!); return Task.CompletedTask; }), form.Button("复制路径", () => { MainWindow.Copy(CurrentPath()); return Task.CompletedTask; })));
            }
        }
        WorkItem? selected = null; System.ComponentModel.PropertyChangedEventHandler changed = (_, args) => { if (args.PropertyName is nameof(WorkItem.Finished) or nameof(WorkItem.Paths)) window.DispatcherQueue.TryEnqueue(ShowItem); };
        list.SelectionChanged += (_, _) => { if (selected is not null) selected.PropertyChanged -= changed; selected = list.SelectedItem as WorkItem; if (selected is not null) selected.PropertyChanged += changed; ShowItem(); UpdateEmpty(); };
        void UpdateEmpty()
        {
            list.Visibility = studio.Queue.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible; empty.Visibility = studio.Queue.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (cancelSelected is not null) cancelSelected.IsEnabled = list.SelectedItem is WorkItem { Finished: false };
            if (cancelAll is not null) cancelAll.IsEnabled = studio.Queue.Busy;
        }
        Action updated = () => window.DispatcherQueue.TryEnqueue(UpdateEmpty);
        root.Loaded += (_, _) => { studio.Queue.Updated += updated; UpdateEmpty(); };
        root.Unloaded += (_, _) => { studio.Queue.Updated -= updated; if (selected is not null) selected.PropertyChanged -= changed; };
        cancelSelected = form.Button("取消所选", () => { if (list.SelectedItem is WorkItem item && !item.Finished) item.Cancellation.Cancel(); return Task.CompletedTask; });
        cancelAll = form.Button("取消全部", () => { studio.Queue.CancelAll(); return Task.CompletedTask; });
        root.Children.Add(Row(cancelSelected, cancelAll, form.Button("复制日志", () => { MainWindow.Copy(string.Join(Environment.NewLine, studio.Queue.Logs)); return Task.CompletedTask; })));
        root.Children.Add(Card("处理队列", empty, list)); ShowItem();
        root.Children.Add(Card("任务详情", details)); return root;
    }
}
