using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VideoLab.Core;
using Windows.ApplicationModel.DataTransfer;

namespace VideoLab.App.Pages;

internal sealed partial class StudioPages
{
    private FrameworkElement Download()
    {
        var root = Page("网络下载", "添加链接，选择下载内容。需要账号权限的网站可单独设置登录信息。"); var form = Form();
        if (!studio.Plugins.Store.Installed.ContainsKey("yt-dlp")) { root.Children.Add(Note("请先从扩展中心安装并启用 yt-dlp。")); root.Children.Add(form.Button("打开扩展中心", () => window.NavigateAsync("extensions"))); return root; }
        var linkBox = form.Text("links", "媒体链接", multiline: true, placeholder: "粘贴视频链接或分享文本，多条链接每行一个"); linkBox.MinHeight = 120; linkBox.MaxHeight = 160;
        ScrollViewer.SetVerticalScrollBarVisibility(linkBox, ScrollBarVisibility.Auto);
        form.Path("directory", "保存位置", studio.Settings.Get("download_path"), directory: true);
        form.Select("format", "下载内容", ["视频与音频", "仅音频 MP3", "仅封面"]); form.Select("height", "最高画质", ["最佳", "2160", "1440", "1080", "720", "480", "360"]);
        var linkPanel = new StackPanel { Spacing = 10 }; linkPanel.Children.Add(form.Take("links"));
        linkPanel.Children.Add(Row(form.Button("从剪贴板粘贴", async () => { var clipboard = Clipboard.GetContent(); if (clipboard.Contains(StandardDataFormats.Text)) linkBox.Text = await clipboard.GetTextAsync(); }), form.Button("清空", () => { linkBox.Text = ""; return Task.CompletedTask; })));
        var settingsPanel = new StackPanel { Spacing = 14 };
        var outputChoices = new Grid { ColumnSpacing = 12 }; outputChoices.ColumnDefinitions.Add(new()); outputChoices.ColumnDefinitions.Add(new());
        var formatChoice = form.Take("format"); var heightChoice = form.Take("height"); outputChoices.Children.Add(formatChoice); Grid.SetColumn(heightChoice, 1); outputChoices.Children.Add(heightChoice);
        settingsPanel.Children.Add(outputChoices); settingsPanel.Children.Add(form.Take("directory"));
        var taskLayout = new Grid { ColumnSpacing = 24, RowSpacing = 18 }; taskLayout.ColumnDefinitions.Add(new()); taskLayout.ColumnDefinitions.Add(new()); taskLayout.RowDefinitions.Add(new() { Height = GridLength.Auto }); taskLayout.RowDefinitions.Add(new() { Height = GridLength.Auto }); taskLayout.Children.Add(linkPanel); taskLayout.Children.Add(settingsPanel);
        bool? sideBySide = null;
        taskLayout.SizeChanged += (_, _) =>
        {
            var wide = taskLayout.ActualWidth >= 660; if (sideBySide == wide) return; sideBySide = wide;
            taskLayout.ColumnDefinitions[0].Width = new GridLength(wide ? 3 : 1, GridUnitType.Star); taskLayout.ColumnDefinitions[1].Width = wide ? new GridLength(2, GridUnitType.Star) : new GridLength(0); taskLayout.ColumnSpacing = wide ? 24 : 0; taskLayout.RowSpacing = wide ? 0 : 18;
            Grid.SetColumn(settingsPanel, wide ? 1 : 0); Grid.SetRow(settingsPanel, wide ? 0 : 1);
        };
        var options = Form(); Dictionary<string, string> applied;
        try { applied = JsonSerializer.Deserialize<Dictionary<string, string>>(studio.Settings.Get("yt_dlp_options", "{}")) ?? []; } catch (JsonException) { applied = []; }
        ComboBox OptionChoice(string key, string title, string[] choices, string placeholder)
        {
            var value = applied.GetValueOrDefault(key, ""); var choice = options.Select(key, title, choices, value, editable: true);
            choice.PlaceholderText = placeholder; if (string.IsNullOrEmpty(value)) choice.SelectedIndex = -1; choice.Text = value ?? ""; return choice;
        }
        OptionChoice("merge-output-format", "合并容器", ["mkv", "mp4", "webm", "mov"], "跟随下载内容");
        OptionChoice("audio-format", "音频格式", ["mp3", "m4a", "flac", "wav", "opus", "vorbis"], "跟随下载内容");
        OptionChoice("audio-quality", "音频质量", ["0", "2", "5", "7", "9"], "自动，可输入质量值或码率");
        options.Text("output", "文件命名模板", applied.GetValueOrDefault("output", ""), placeholder: "默认使用视频标题");
        options.Text("sub-langs", "字幕语言", applied.GetValueOrDefault("sub-langs", ""), placeholder: "例如 zh,en；留空使用默认值");
        options.Text("playlist-items", "下载范围", applied.GetValueOrDefault("playlist-items", ""), placeholder: "例如 1,3,5；留空使用默认范围");
        options.Path("download-archive", "下载记录文件", applied.GetValueOrDefault("download-archive", "")).PlaceholderText = "可选，用于跳过已经下载的内容";
        options.Text("limit-rate", "下载限速", applied.GetValueOrDefault("limit-rate", ""), placeholder: "可选，例如 5M");
        options.Text("concurrent-fragments", "并发片段数", applied.GetValueOrDefault("concurrent-fragments", ""), placeholder: "下载器默认值");
        OptionChoice("retries", "重试次数", ["3", "5", "10", "infinite"], "默认值；infinite 表示持续重试");
        options.Text("socket-timeout", "连接超时（秒）", applied.GetValueOrDefault("socket-timeout", ""), placeholder: "下载器默认值");
        options.Text("proxy", "代理地址", applied.GetValueOrDefault("proxy", ""), placeholder: "可选，例如 http://127.0.0.1:7890");
        options.Text("referer", "来源页面（Referer）", applied.GetValueOrDefault("referer", ""), placeholder: "可选，填写网页地址");
        options.Text("user-agent", "User-Agent", applied.GetValueOrDefault("user-agent", ""), placeholder: "使用下载器默认值");
        options.Text("format", "自定义格式选择", applied.GetValueOrDefault("format", ""), placeholder: "例如 bv*+ba/b；留空跟随上方下载内容");
        options.Text("custom", "附加命令参数", applied.GetValueOrDefault("custom", ""), multiline: true, placeholder: "仅填写额外参数，不包含 yt-dlp 程序路径").MaxHeight = 180;
        var toggles = new Dictionary<string, string> { ["yes-playlist"] = "下载整个播放列表", ["write-subs"] = "保存字幕文件", ["write-auto-subs"] = "包含自动生成的字幕", ["embed-subs"] = "嵌入字幕", ["embed-metadata"] = "嵌入媒体信息", ["write-thumbnail"] = "保存封面图片", ["embed-thumbnail"] = "嵌入封面", ["write-info-json"] = "保存信息 JSON" };
        foreach (var pair in toggles) options.Check(pair.Key, pair.Value, applied.GetValueOrDefault(pair.Key) == "true");
        async Task SaveCookieOptionsAsync(Dictionary<string, string> values)
        {
            foreach (var pair in values) applied[pair.Key] = pair.Value;
            studio.Settings.Set("yt_dlp_options", JsonSerializer.Serialize(applied)); await studio.Settings.SaveAsync();
        }
        var cookieSetup = BuildCookieSetup(applied, SaveCookieOptionsAsync, () => form.Get("links"));
        form.Changed += cookieSetup.Refresh;
        var optionTabs = options.ArrangeTabs(
            ("format", "格式输出", "只需修改要覆盖的选项，留空时跟随默认下载设置。", [["merge-output-format", "audio-format"], ["audio-quality"], ["output"]]),
            ("subtitles", "字幕附件", "选择随视频保存的字幕、封面和媒体信息。", [["sub-langs"], ["write-subs", "write-auto-subs"], ["embed-subs", "embed-metadata"], ["write-thumbnail", "embed-thumbnail"], ["write-info-json"]]),
            ("network", "网络", "按需要设置速度、重试和代理。", [["limit-rate", "concurrent-fragments"], ["retries", "socket-timeout"], ["proxy"], ["referer", "user-agent"]]),
            ("playlist", "播放列表", "设置列表下载范围，使用下载记录避免重复下载。", [["yes-playlist"], ["playlist-items"], ["download-archive"]]),
            ("custom", "自定义", "仅在需要自行编写下载规则时使用。", [["format"], ["custom"]]));
        optionTabs.Tag = "download-options-tabs";
        var optionStatus = Note("修改会在下一批下载任务中自动使用。");
        async Task SaveDownloadOptionsAsync()
        {
            foreach (var key in new[] { "concurrent-fragments", "retries", "socket-timeout" }) { var value = options.Get(key); if (value.Length > 0 && !(key == "retries" && value == "infinite") && (!int.TryParse(value, out var n) || n < (key == "retries" ? 0 : 1))) throw new FormatException("并发数、重试次数或超时无效。"); }
            _ = CookieService.DownloadOptions(cookieSetup.Values());
            _ = studio.Native.ParseArguments(options.Get("custom")); applied = options.Values(); foreach (var pair in cookieSetup.Values()) applied[pair.Key] = pair.Value;
            studio.Settings.Set("yt_dlp_options", JsonSerializer.Serialize(applied)); await studio.Settings.SaveAsync(); optionStatus.Text = "高级参数已保存。";
        }
        var advanced = new StackPanel { Spacing = 12 }; advanced.Children.Add(options.Panel); advanced.Children.Add(optionStatus);
        var advancedExpander = new Expander { Header = "高级下载参数", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = advanced };
        string[] ReadLinks() => Regex.Matches(form.Get("links"), @"https?://[^\s<>\""'，。；]+", RegexOptions.IgnoreCase).Select(m => m.Value.TrimEnd(')', ']', '）')).Distinct().ToArray();
        var feedback = Note("添加链接后即可加入下载队列。");
        async Task ObserveBatchAsync(Task<WorkItem>[] tasks)
        { try { await Task.WhenAll(tasks); } catch (Exception error) { studio.Queue.Log("下载队列：" + error.Message); } }
        var submitting = false;
        var start = form.Button("加入下载队列", async () =>
        {
            if (submitting) return;
            submitting = true;
            try
            {
            var links = ReadLinks(); if (links.Length == 0) throw new FormatException("请填写有效的下载链接。");
            var directory = DirectoryFor(form, studio); if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("请先选择有效的保存位置。");
            await cookieSetup.PrepareAsync();
            await SaveDownloadOptionsAsync();
            var format = form.Index("format"); var height = form.Get("height") == "最佳" ? 0 : form.Int("height"); var values = new Dictionary<string, string>(applied);
            var tasks = links.Select(url => studio.Queue.AddAsync("yt-dlp 下载", url, (item, token) => studio.DownloadAsync(url, directory, format, height, values, token, item))).ToArray();
            _ = ObserveBatchAsync(tasks); linkBox.Text = ""; feedback.Text = $"已添加 {links.Length} 个任务，可以继续添加链接。";
            }
            finally { submitting = false; }
        }, true);
        start.IsEnabled = false;
        form.Changed += () => { var count = ReadLinks().Length; start.IsEnabled = count > 0; form.Enable("height", form.Index("format") == 0); feedback.Text = count > 0 ? $"已识别 {count} 条链接 · 自动去重" : linkBox.Text.Length > 0 ? "还没有识别到有效链接" : "添加链接后即可加入下载队列。"; };
        var heading = new StackPanel { Spacing = 4 }; heading.Children.Add(new TextBlock { Text = "新建下载", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }); heading.Children.Add(feedback);
        var actions = new Grid { ColumnSpacing = 16 }; actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); actions.Children.Add(heading); Grid.SetColumn(start, 1); start.VerticalAlignment = VerticalAlignment.Center; actions.Children.Add(start);
        var taskCard = new StackPanel { Spacing = 16 }; taskCard.Children.Add(actions); taskCard.Children.Add(taskLayout); taskCard.Children.Add(cookieSetup.View);
        root.Children.Add(new Border { Style = (Style)Application.Current.Resources["CardStyle"], Child = taskCard });
        root.Children.Add(advancedExpander);
        root.Children.Add(Row(form.Button("查看下载任务", () => window.NavigateAsync("tasks")), form.Button("工具配置", () => window.NavigateToSettingsAsync("tools"))));
        return root;
    }
}
