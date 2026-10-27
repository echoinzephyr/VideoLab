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
        var form = Form(); var root = Page("设置", "配置媒体引擎、下载依赖、默认输出目录与应用外观。");
        form.Path("ffmpeg_path", "FFmpeg 可执行文件或所在目录", studio.Settings.Get("ffmpeg_path"));
        form.Path("download_path", "默认输出文件夹", studio.Settings.Get("download_path"), directory: true);
        form.Path("yt_dlp_path", "yt-dlp 可执行文件", studio.Settings.Get("yt_dlp_path")); form.Path("aria2_path", "Aria2 可执行文件（可选）", studio.Settings.Get("aria2_path")); form.Toggle("use_aria2", "使用 Aria2 多线程下载", studio.Settings.Flag("use_aria2"));
        form.Select("theme", "外观", ["system", "light", "dark"], studio.Settings.Get("theme", "system"));
        var status = Note(""); root.Children.Add(Card("应用与工具", form.Panel, status));
        async Task SaveAsync()
        {
            var changed = studio.Settings.Get("ffmpeg_path") != form.Get("ffmpeg_path");
            foreach (var p in form.Values()) if (p.Key is "gpu_acceleration" or "use_aria2") studio.Settings.Set(p.Key, p.Value == "true"); else studio.Settings.Set(p.Key, p.Value);
            if (changed) { studio.Settings.Set("gpu_encoders", "[]"); studio.Settings.Set("gpu_acceleration", false); foreach (var family in new[] { "h264", "hevc", "av1", "vp9" }) studio.Settings.Set("gpu_" + family, ""); }
            await studio.Settings.SaveAsync(); window.ApplyTheme(form.Get("theme")); status.Text = "设置已保存。";
        }
        root.Children.Add(Row(form.Button("保存设置", SaveAsync, true), form.Button("保存并检测 FFmpeg", async () => { await SaveAsync(); var result = await ToolRunner.RunAsync(studio.Ffmpeg, ["-version"]); if (result.ExitCode != 0) throw new InvalidOperationException(result.Text); var ffprobe = Path.Combine(Path.GetDirectoryName(studio.Ffmpeg) ?? "", "ffprobe.exe"); var probeResult = await ToolRunner.RunAsync(ffprobe, ["-version"]); if (probeResult.ExitCode != 0) throw new InvalidOperationException(probeResult.Text); status.Text = result.Text.Split('\n')[0] + "\n" + probeResult.Text.Split('\n')[0]; })));
        root.Children.Add(form.Button("安装 / 更新 yt-dlp", async () =>
        {
            studio.Plugins.EnsureIdle("yt-dlp"); var path = await DependencyInstaller.YtDlpAsync(Path.Combine(studio.Settings.ExtensionsRoot, "tools"), CancellationToken.None);
            form.Apply(new Dictionary<string, string> { ["yt_dlp_path"] = path }); studio.Settings.Set("yt_dlp_path", path); await studio.Settings.SaveAsync(); status.Text = "yt-dlp 已下载并校验：" + path;
        }));
        var gpu = Form(); gpu.Toggle("gpu_acceleration", "使用通过本机试编码的硬件编码器", studio.Settings.Flag("gpu_acceleration"));
        foreach (var family in new[] { "h264", "hevc", "av1", "vp9" }) gpu.Select("gpu_" + family, family.ToUpperInvariant() + " 编码器（留空为 CPU）", new[] { "" }.Concat(UsableGpu().Where(e => e.StartsWith(family + "_"))), studio.Settings.Get("gpu_" + family));
        var gpuStatus = Note(string.Join("、", UsableGpu()));
        root.Children.Add(Card("硬件加速", gpu.Panel, gpuStatus, Row(form.Button("检测并试编码", async () =>
        {
            await SaveAsync(); await studio.Queue.AddAsync("硬件编码检测", studio.Ffmpeg, async (item, token) => { await studio.DetectHardwareAsync(token); return new([], []); });
            gpuStatus.Text = "检测完成：" + string.Join("、", UsableGpu()); window.ShowMessage("硬件检测完成。重新打开设置可选择检测通过的编码器。");
        }), form.Button("保存 GPU 选择", async () =>
        {
            var selected = gpu.Values().Where(p => p.Key != "gpu_acceleration" && p.Value.Length > 0).ToArray();
            if (selected.Any(p => !UsableGpu().Contains(p.Value))) throw new InvalidOperationException("编码器尚未通过本机检测。");
            foreach (var p in selected) studio.Settings.Set(p.Key, p.Value);
            foreach (var p in gpu.Values().Where(p => p.Key.StartsWith("gpu_") && p.Value.Length == 0)) studio.Settings.Set(p.Key, "");
            studio.Settings.Set("gpu_acceleration", gpu.Flag("gpu_acceleration") && selected.Length > 0); await studio.Settings.SaveAsync(); window.ShowMessage("硬件编码选择已保存。");
        }))));
        root.Children.Add(Card("数据与兼容", Note("WinUI 版数据目录：" + studio.Settings.Root), Note(studio.Settings.MigrationWarning.Length == 0 ? "旧版设置、Cookie 与预设保留原文件，迁移副本供本版使用。" : studio.Settings.MigrationWarning), Row(form.Button("打开数据文件夹", () => { MainWindow.Open(studio.Settings.Root); return Task.CompletedTask; }), form.Button("重试旧版导入", async () => { studio.Settings.Set("legacy_imported_v1", false); await studio.Settings.LoadAsync(); window.ShowMessage(studio.Settings.MigrationWarning.Length == 0 ? "导入完成。" : studio.Settings.MigrationWarning); }))));
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
        var root = Page("任务与结果", "页面切换不会中断任务。取消队列后保留已经完成的输出。"); var form = Form(); var list = new ListView { ItemsSource = studio.Queue.Items, DisplayMemberPath = "Summary", SelectionMode = ListViewSelectionMode.Single, MaxHeight = 420 };
        var details = new StackPanel { Spacing = 10 };
        void ShowItem()
        {
            details.Children.Clear(); if (list.SelectedItem is not WorkItem item) return;
            details.Children.Add(Note(item.Input));
            foreach (var error in item.Errors) details.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = error });
            foreach (var file in item.Paths)
            {
                var output = new StackPanel { Spacing = 8 }; output.Children.Add(new TextBox { Text = file, IsReadOnly = true });
                output.Children.Add(Row(form.Button("打开", () => { MainWindow.Open(file); return Task.CompletedTask; }), form.Button("文件夹", () => { MainWindow.Open(Path.GetDirectoryName(Path.GetFullPath(file))!); return Task.CompletedTask; }), form.Button("复制路径", () => { MainWindow.Copy(file); return Task.CompletedTask; }))); details.Children.Add(output);
            }
        }
        WorkItem? selected = null; System.ComponentModel.PropertyChangedEventHandler changed = (_, _) => window.DispatcherQueue.TryEnqueue(ShowItem);
        list.SelectionChanged += (_, _) => { if (selected is not null) selected.PropertyChanged -= changed; selected = list.SelectedItem as WorkItem; if (selected is not null) selected.PropertyChanged += changed; ShowItem(); };
        root.Children.Add(Card("处理队列", list, Row(form.Button("取消所选", () => { if (list.SelectedItem is WorkItem item && !item.Finished) item.Cancellation.Cancel(); return Task.CompletedTask; }), form.Button("取消全部", () => { studio.Queue.CancelAll(); return Task.CompletedTask; }), form.Button("复制全部日志", () => { MainWindow.Copy(string.Join(Environment.NewLine, studio.Queue.Logs)); return Task.CompletedTask; }))));
        root.Children.Add(Card("输出与诊断", details)); return root;
    }
}
