using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VideoLab.Core;

namespace VideoLab.App.Pages;

internal sealed partial class StudioPages
{
    private async Task<FrameworkElement> ExtensionsAsync()
    {
        var root = Page("扩展中心", "安装签名 VDX、配置依赖、启停、更新和回滚。随附插件可离线安装。"); var form = Form();
        var search = new TextBox { PlaceholderText = "搜索扩展名称或描述" }; var installedPanel = new StackPanel { Spacing = 14 }; var catalogPanel = new StackPanel { Spacing = 14 };
        async Task RefreshAsync() { window.RebuildNavigation(); await window.NavigateAsync("extensions"); }
        async Task ImportAsync()
        {
            var paths = await window.PickFilesAsync(); if (paths.Length == 0) return;
            var bytes = await File.ReadAllBytesAsync(paths[0]); var package = PluginPackage.Decode(bytes); studio.Plugins.EnsureIdle(package.Manifest.Id);
            try { await studio.Plugins.Store.InstallAsync(bytes); }
            catch (PublisherApprovalRequired approval)
            {
                var permissions = package.Manifest.Json["permissions"]?.ToJsonString() ?? "未声明";
                if (!await window.ConfirmAsync("信任插件发布者？", $"{package.Manifest.Name}\n公钥指纹：{approval.Fingerprint}\n权限：{permissions}\n插件以当前用户权限独立运行。", "信任并安装")) return;
                await studio.Plugins.Store.InstallAsync(bytes, true);
            }
            await RefreshAsync();
        }
        void Populate()
        {
            installedPanel.Children.Clear(); catalogPanel.Children.Clear(); var filter = search.Text.Trim();
            foreach (var plugin in studio.Plugins.Store.Installed.Values.Where(p => (p.Manifest.Name + p.Manifest.Description).Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                var id = plugin.Manifest.Id; var toggle = new ToggleSwitch { IsOn = studio.Plugins.Enabled(id), OnContent = "已启用", OffContent = "已停用" };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, "启用 " + plugin.Manifest.Name);
                toggle.Toggled += async (_, _) => await window.RunActionAsync(async () => { await studio.Plugins.EnableAsync(id, toggle.IsOn); window.RebuildNavigation(); });
                var rollback = form.Button("回滚到上一版", async () => { studio.Plugins.EnsureIdle(id); await studio.Plugins.Store.RollbackAsync(id); await RefreshAsync(); }); rollback.IsEnabled = plugin.Previous is not null;
                var management = new Expander { Header = "版本与卸载", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = Row(rollback, form.Button("卸载扩展", async () => { studio.Plugins.EnsureIdle(id); if (await window.ConfirmAsync("卸载扩展", "保留该扩展的配置，之后可重新安装。", "卸载")) { await studio.Plugins.Store.UninstallAsync(id); await RefreshAsync(); } })) };
                installedPanel.Children.Add(Card(plugin.Manifest.Name + " · " + plugin.Manifest.Version, Note(plugin.Manifest.Description), Row(toggle,
                    form.Button("打开", () => window.NavigateAsync(id == "yt-dlp" ? "download" : "plugin:" + id)),
                    form.Button("配置与依赖", () => studio.Plugins.Store.IsOfficial(plugin) ? window.NavigateToSettingsAsync("tools") : window.NavigateAsync("config:" + id))), management));
            }
            foreach (var bundled in studio.Plugins.Bundled.Where(p => !studio.Plugins.Store.Installed.ContainsKey(p!["manifest"]!["id"]!.GetValue<string>())))
            {
                var id = bundled!["manifest"]!["id"]!.GetValue<string>();
                catalogPanel.Children.Add(Card(bundled["manifest"]!["name"]!.GetValue<string>(), Note("随附签名插件 · 可离线安装"), form.Button("安装", async () => { await studio.Plugins.InstallBundledAsync(id); await RefreshAsync(); })));
            }
            foreach (var item in studio.Plugins.Catalog)
            {
                var entry = item!.AsObject(); var manifest = PluginManifest.Parse(entry["manifest"]!.AsObject()); if (!(manifest.Name + manifest.Description).Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                var installed = studio.Plugins.Store.Installed.GetValueOrDefault(manifest.Id); var available = installed is null || UpdateService.CompareVersions(manifest.Version, installed.Manifest.Version) > 0;
                var install = form.Button(installed is null ? "安装" : available ? "更新" : "已安装", async () => { await studio.Plugins.InstallCatalogAsync(entry, CancellationToken.None); await RefreshAsync(); }); install.IsEnabled = available;
                catalogPanel.Children.Add(Card(manifest.Name + " · " + manifest.Version, Note(manifest.Description), install));
            }
            if (installedPanel.Children.Count == 0) installedPanel.Children.Add(Note(filter.Length > 0 ? "没有找到匹配的已安装扩展。" : "尚未安装扩展。可在“发现扩展”中安装，或导入本地 VDX。"));
            if (catalogPanel.Children.Count == 0) catalogPanel.Children.Add(Note("暂无可显示的扩展，可刷新官方目录或安装本地 VDX。"));
        }
        search.TextChanged += (_, _) => Populate(); root.Children.Add(search);
        root.Children.Add(Row(form.Button("安装本地 VDX", ImportAsync, true), form.Button("刷新官方目录", async () => { try { await studio.Plugins.RefreshCatalogAsync(CancellationToken.None); Populate(); } catch (Exception error) { window.ShowMessage("在线目录暂不可用，仍可安装随附插件和本地 VDX。" + error.Message); } })));
        root.Children.Add(PageTabs("extension-tabs", ("installed", "已安装", installedPanel), ("catalog", "发现扩展", catalogPanel)));
        var developer = new ToggleSwitch { Header = "开发者模式（允许未签名插件）", IsOn = studio.Plugins.Store.DeveloperMode };
        developer.Toggled += async (_, _) => await window.RunActionAsync(async () => { studio.Plugins.Store.DeveloperMode = developer.IsOn; await studio.Plugins.Store.SaveAsync(); });
        root.Children.Add(new Expander { Header = "开发者选项", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = developer }); if (studio.Plugins.Warning.Length > 0) root.Children.Add(Note(studio.Plugins.Warning)); Populate(); await Task.CompletedTask; return root;
    }
    private void AddPluginField(EditorForm form, JsonObject field, JsonObject values)
    {
        var id = field["id"]!.GetValue<string>(); var label = field["label"]?.GetValue<string>() ?? id; var value = values[id]?.ToString() ?? field["default"]?.ToString() ?? "";
        switch (field["type"]!.GetValue<string>())
        {
            case "bool": form.Check(id, label, value.Equals("true", StringComparison.OrdinalIgnoreCase)); break;
            case "select": form.Select(id, label, field["options"]!.AsArray().Select(v => v!.GetValue<string>()), value); break;
            case "secret": form.Secret(id, label, value); break;
            case "directory": form.Path(id, label, value, directory: true); break;
            case "file": form.Path(id, label, value); break;
            default: form.Text(id, label, value, field["type"]!.GetValue<string>() == "multiline"); break;
        }
        if (field["help"] is not null) form.Panel.Children.Add(Note(field["help"]!.ToString()));
    }
    private static JsonObject FieldValues(EditorForm form, JsonArray fields)
    { var result = new JsonObject(); foreach (var field in fields) { var id = field!["id"]!.GetValue<string>(); result[id] = field["type"]!.GetValue<string>() == "bool" ? JsonValue.Create(form.Flag(id)) : JsonValue.Create(form.Get(id)); } return result; }
    private async Task<FrameworkElement> PluginTaskAsync(string id, bool configurationOnly = false)
    {
        var plugin = studio.Plugins.Store.Installed.GetValueOrDefault(id) ?? throw new InvalidOperationException("扩展尚未安装。");
        var root = Page(plugin.Manifest.Name, plugin.Manifest.Description); var inputs = Form(); var configuration = Form(); var saved = await studio.Plugins.ConfigurationAsync(plugin);
        foreach (var field in plugin.Manifest.Fields) AddPluginField(inputs, field!.AsObject(), new());
        inputs.Path("directory", "输出文件夹（留空沿用默认目录）", studio.Settings.Get("download_path"), directory: true);
        foreach (var field in plugin.Manifest.Settings) AddPluginField(configuration, field!.AsObject(), saved);
        var installLinks = new StackPanel { Spacing = 8 };
        foreach (var dependency in plugin.Manifest.Dependencies)
        {
            var d = dependency!.AsObject(); var key = "dependency_" + d["id"]!.GetValue<string>(); var value = studio.Plugins.DependencyValue(plugin, d, saved);
            if (d["kind"]?.GetValue<string>() == "api") configuration.Text(key, d["name"]?.ToString() ?? key, value); else configuration.Path(key, d["name"]?.ToString() ?? key, value);
            if (d["instructionsUrl"] is JsonValue uri) installLinks.Children.Add(configuration.Button((d["name"]?.ToString() ?? d["id"]!.ToString()) + " 安装说明", () => { MainWindow.Open(uri.GetValue<string>()); return Task.CompletedTask; }));
        }
        var dependencyRevision = 0;
        var dependencyStatus = Note("保存配置后检测依赖。"); configuration.Changed += () => { ++dependencyRevision; dependencyStatus.Text = "配置已改变，请保存并重新检测。"; };
        var dependencyDetail = Note("");
        var dependencyResult = new Expander { Header = "检测结果详情", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = dependencyDetail };
        async Task<JsonObject> SaveConfigurationAsync()
        {
            studio.Plugins.EnsureIdle(id); var values = FieldValues(configuration, plugin.Manifest.Settings);
            foreach (var dependency in plugin.Manifest.Dependencies) { var key = dependency!["id"]!.GetValue<string>(); values[key] = configuration.Get("dependency_" + key); }
            await studio.Plugins.Store.SaveSettingsAsync(id, values);
            if (studio.Plugins.Store.IsOfficial(plugin))
            {
                foreach (var d in plugin.Manifest.Dependencies) if (d!["preference"] is JsonValue preference) studio.Settings.Set(preference.GetValue<string>(), values[d["id"]!.GetValue<string>()]!.GetValue<string>());
                await studio.Settings.SaveAsync();
            }
            return values;
        }
        var detect = configuration.Button("保存并检测", async () =>
        {
            var revision = ++dependencyRevision; var values = await SaveConfigurationAsync(); dependencyStatus.Text = "正在检测依赖…";
            var results = await studio.Plugins.InspectDependenciesAsync(plugin, values, CancellationToken.None); if (revision != dependencyRevision) return;
            var failures = results.Count(result => !result.CanRun); dependencyStatus.Text = failures == 0 ? "配置与依赖已就绪。" : $"有 {failures} 项依赖需要处理。";
            dependencyDetail.Text = DependencyChecker.Describe(results); dependencyResult.IsExpanded = failures > 0;
        }, true);
        var configContent = new StackPanel { Spacing = 16 }; configContent.Children.Add(configuration.Panel); configContent.Children.Add(dependencyStatus); configContent.Children.Add(dependencyResult);
        if (installLinks.Children.Count > 0) configContent.Children.Add(new Expander { Header = "工具安装指南", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = installLinks });
        if (configurationOnly) { root.Children.Add(Row(detect)); root.Children.Add(Card("配置与依赖", configContent)); }
        if (!configurationOnly)
        {
            root.Children.Add(Card("任务参数", inputs.Panel));
            root.Children.Insert(2, inputs.Button("开始任务", async () =>
            {
                var values = FieldValues(inputs, plugin.Manifest.Fields); var directory = DirectoryFor(inputs, studio);
                await studio.Queue.AddAsync(plugin.Manifest.Name, directory, (item, token) => studio.Plugins.ExecuteAsync(id, values, directory, token, (method, data) =>
                { if (method == "log") studio.Queue.Log(data["message"]?.ToString() ?? ""); if (method == "progress") studio.Queue.Dispatch(() => item.Report(data["message"]?.ToString() ?? "处理中", data["fraction"]?.GetValue<double>())); }));
                window.ShowMessage("任务已结束。可在“任务与结果”中打开输出或查看失败原因。");
            }, true));
            configContent.Children.Insert(0, detect); root.Children.Add(new Expander { Header = "配置与依赖", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = configContent });
        }
        root.Children.Add(inputs.Button("查看任务与结果", () => window.NavigateAsync("tasks"))); return root;
    }
}
