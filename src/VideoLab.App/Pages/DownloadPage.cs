using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VideoLab.Core;

namespace VideoLab.App.Pages;

internal sealed partial class StudioPages
{
    private FrameworkElement Download()
    {
        var root = Page("网络媒体下载", "使用 yt-dlp 独立扩展，支持多行链接、画质、音频、封面及浏览器 Cookie。"); var form = Form();
        if (!studio.Plugins.Store.Installed.ContainsKey("yt-dlp")) { root.Children.Add(Note("请先从扩展中心安装并启用 yt-dlp。")); root.Children.Add(form.Button("打开扩展中心", () => window.NavigateAsync("extensions"))); return root; }
        form.Text("links", "媒体链接（每行一个，也支持粘贴分享文本）", multiline: true); form.Path("directory", "保存目录（留空沿用默认目录）", studio.Settings.Get("download_path"), directory: true);
        form.Select("format", "下载内容", ["视频与音频", "仅音频 MP3", "仅封面"]); form.Select("height", "最高画质", ["最佳", "2160", "1440", "1080", "720", "480", "360"]);
        root.Children.Add(Card("下载任务", form.Panel));
        var options = Form(); Dictionary<string, string> applied;
        try { applied = JsonSerializer.Deserialize<Dictionary<string, string>>(studio.Settings.Get("yt_dlp_options", "{}")) ?? []; } catch (JsonException) { applied = []; }
        var optionFields = new Dictionary<string, string> { ["format"] = "自定义格式选择（如 bv*+ba/b）", ["merge-output-format"] = "合并容器", ["audio-format"] = "音频格式", ["audio-quality"] = "音频质量", ["sub-langs"] = "字幕语言", ["playlist-items"] = "播放列表范围", ["limit-rate"] = "限速（如 5M）", ["concurrent-fragments"] = "并发片段数", ["retries"] = "重试次数（或 infinite）", ["socket-timeout"] = "网络超时秒数", ["proxy"] = "代理地址", ["referer"] = "Referer", ["user-agent"] = "User-Agent", ["output"] = "文件命名模板", ["download-archive"] = "下载历史文件", ["custom"] = "自定义参数" };
        foreach (var p in optionFields) options.Text(p.Key, p.Value, applied.GetValueOrDefault(p.Key, ""));
        var toggles = new Dictionary<string, string> { ["yes-playlist"] = "下载整个播放列表", ["write-subs"] = "保存字幕", ["write-auto-subs"] = "包含自动字幕", ["embed-subs"] = "嵌入字幕", ["embed-metadata"] = "嵌入媒体信息", ["write-thumbnail"] = "保存封面", ["embed-thumbnail"] = "嵌入封面", ["write-info-json"] = "保存信息 JSON" };
        foreach (var p in toggles) options.Toggle(p.Key, p.Value, applied.GetValueOrDefault(p.Key) == "true");
        options.Select("cookie-browser", "Cookie 来源", ["files", "firefox", "edge", "chrome", "brave", "chromium", "opera", "vivaldi"], applied.GetValueOrDefault("cookie-browser", "files")); options.Text("cookie-profile", "浏览器配置文件（可选）", applied.GetValueOrDefault("cookie-profile", ""));
        var optionStatus = Note("高级参数已应用。"); options.Changed += () => optionStatus.Text = "参数已改变，点击“应用参数”后用于新任务。";
        var advanced = new StackPanel { Spacing = 12 }; advanced.Children.Add(options.Panel); advanced.Children.Add(optionStatus); advanced.Children.Add(form.Button("应用参数", async () =>
        {
            foreach (var key in new[] { "concurrent-fragments", "retries", "socket-timeout" }) { var value = options.Get(key); if (value.Length > 0 && !(key == "retries" && value == "infinite") && (!int.TryParse(value, out var n) || n < (key == "retries" ? 0 : 1))) throw new FormatException("并发数、重试次数或超时无效。"); }
            if (options.Get("cookie-profile").Contains('\n') || options.Get("cookie-profile").Contains('\r')) throw new FormatException("浏览器配置文件不能包含换行。");
            _ = studio.Native.ParseArguments(options.Get("custom")); applied = options.Values(); studio.Settings.Set("yt_dlp_options", JsonSerializer.Serialize(applied)); await studio.Settings.SaveAsync(); optionStatus.Text = "高级参数已应用。";
        })); root.Children.Add(new Expander { Header = "高级下载参数与浏览器 Cookie", HorizontalAlignment = HorizontalAlignment.Stretch, Content = advanced });
        var cookies = Form(); cookies.Text("domain", "网站域名（如 douyin.com）", "douyin.com"); var cookieContent = cookies.Text("content", "Netscape cookies.txt 或 Cookie 请求头", multiline: true); cookieContent.MaxHeight = 160;
        var cookiePanel = new StackPanel { Spacing = 12 }; cookiePanel.Children.Add(cookies.Panel); cookiePanel.Children.Add(Row(cookies.Button("从文件导入", async () => { var files = await window.PickFilesAsync(); if (files.Length > 0) cookieContent.Text = await File.ReadAllTextAsync(files[0]); }), cookies.Button("保存 Cookie", async () => { await CookieService.SaveAsync(studio.Settings, cookies.Get("domain"), cookieContent.Text); cookieContent.Text = ""; window.ShowMessage("Cookie 已保存在本机。手动导入时请将来源设为 files 并应用参数。"); }), cookies.Button("清除该站点", async () =>
        { var domain = CookieService.Domain(cookies.Get("domain")); if (studio.Settings.Data["winui_cookie_files"] is System.Text.Json.Nodes.JsonObject map && map.Remove(domain)) { var file = Path.Combine(studio.Settings.CookieRoot, domain + ".txt"); if (File.Exists(file)) File.Delete(file); await studio.Settings.SaveAsync(); } })));
        cookiePanel.Children.Add(Note("Chrome / Edge 的数据库占用或加密限制可能导致读取失败；可切换 Firefox 或手动导入。Cookie 不保证消除站点限流与访问限制。"));
        root.Children.Add(new Expander { Header = "手动 Cookie 管理", HorizontalAlignment = HorizontalAlignment.Stretch, Content = cookiePanel });
        root.Children.Add(Row(form.Button("开始下载", async () =>
        {
            var links = Regex.Matches(form.Get("links"), @"https?://[^\s<>\""'，。；]+", RegexOptions.IgnoreCase).Select(m => m.Value.TrimEnd(')', ']', '）')).Distinct().ToArray(); if (links.Length == 0) throw new FormatException("请填写有效的下载链接。");
            var directory = DirectoryFor(form, studio); var format = form.Index("format"); var height = form.Get("height") == "最佳" ? 0 : form.Int("height"); var values = new Dictionary<string, string>(applied);
            var tasks = links.Select(url => studio.Queue.AddAsync("yt-dlp 下载", url, (item, token) => studio.DownloadAsync(url, directory, format, height, values, token, item))).ToArray(); await Task.WhenAll(tasks);
            window.ShowMessage("下载队列已结束，详情见“任务与结果”。");
        }, true), form.Button("配置与依赖", () => window.NavigateAsync("config:yt-dlp")), form.Button("任务与结果", () => window.NavigateAsync("tasks")))); return root;
    }
}
