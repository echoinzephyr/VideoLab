using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace VideoLab.Core;

public sealed class PluginManager
{
    private readonly SettingsStore settings;
    private readonly string bundledRoot;
    public PluginStore Store { get; }
    public JsonArray Bundled { get; private set; } = [];
    public JsonArray Catalog { get; private set; } = [];
    public HashSet<string> Running { get; } = [];
    public string Warning { get; private set; } = "";
    public PluginManager(SettingsStore settings, string bundledRoot) { this.settings = settings; this.bundledRoot = bundledRoot; Store = new(settings.ExtensionsRoot); }
    public async Task LoadAsync()
    {
        var index = Path.Combine(bundledRoot, "index.json");
        if (File.Exists(index))
        {
            var data = JsonNode.Parse(await File.ReadAllTextAsync(index))!.AsObject();
            foreach (var key in data["officialKeys"] as JsonArray ?? []) Store.OfficialKeys.Add(key!.GetValue<string>());
            Bundled = data["packages"]?.AsArray() ?? [];
        }
        await Store.LoadAsync();
        if (!settings.Data.ContainsKey("winui_disabled_extensions") && settings.Data["enabled_extensions"] is JsonArray legacyEnabled)
        {
            var enabled = legacyEnabled.Select(n => n!.GetValue<string>()).ToHashSet();
            settings.Set("winui_disabled_extensions", Bundled.Select(n => n!["manifest"]!["id"]!.GetValue<string>()).Where(id => !enabled.Contains(id)).ToArray());
        }
        if (!settings.Flag("winui_bundled_initialized"))
        {
            foreach (var item in Bundled)
            {
                var id = item!["manifest"]!["id"]!.GetValue<string>();
                if (!Store.Installed.ContainsKey(id)) await Store.InstallAsync(await File.ReadAllBytesAsync(BundledPath(item.AsObject())));
            }
            settings.Set("winui_bundled_initialized", true); await settings.SaveAsync();
        }
        Warning = string.Join("\n", Store.LoadErrors);
    }
    private string BundledPath(JsonObject entry) => Path.Combine(bundledRoot, Path.GetFileName(entry["asset"]!.GetValue<string>()));
    public async Task InstallBundledAsync(string id, CancellationToken token = default)
    { EnsureIdle(id); var entry = Bundled.First(n => n!["manifest"]!["id"]!.GetValue<string>() == id)!.AsObject(); await Store.InstallAsync(await File.ReadAllBytesAsync(BundledPath(entry), token), token: token); }
    public bool Enabled(string id)
    {
        var disabled = settings.Data["winui_disabled_extensions"] as JsonArray ?? [];
        return !disabled.Any(v => v!.GetValue<string>() == id);
    }
    public async Task EnableAsync(string id, bool enabled)
    {
        EnsureIdle(id); var disabled = (settings.Data["winui_disabled_extensions"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToHashSet();
        if (enabled) disabled.Remove(id); else disabled.Add(id); settings.Set("winui_disabled_extensions", disabled.ToArray()); await settings.SaveAsync();
    }
    public void EnsureIdle(string id) { if (Running.Contains(id)) throw new InvalidOperationException("请先取消并等待扩展任务退出。"); }
    public async Task<JsonObject> ConfigurationAsync(InstalledPlugin plugin)
    {
        var values = await Store.SettingsAsync(plugin.Manifest.Id);
        foreach (var field in plugin.Manifest.Settings)
        {
            var id = field!["id"]!.GetValue<string>();
            if (!values.ContainsKey(id)) values[id] = field["default"]?.DeepClone();
        }
        return values;
    }
    public async Task<Dictionary<string, string>> CheckDependenciesAsync(InstalledPlugin plugin, JsonObject configuration, CancellationToken token)
    {
        var checks = await InspectDependenciesAsync(plugin, configuration, token);
        return checks.ToDictionary(p => p.Id, p => p.Message);
    }
    public async Task<IReadOnlyList<DependencyCheck>> InspectDependenciesAsync(InstalledPlugin plugin, JsonObject configuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var results = new List<DependencyCheck>();
        foreach (var dependency in plugin.Manifest.Dependencies)
        {
            var d = dependency!.AsObject(); var id = d["id"]!.GetValue<string>(); var value = DependencyValue(plugin, d, configuration);
            var required = d["required"]?.GetValue<bool>() != false || (Store.IsOfficial(plugin) && id == "aria2" && settings.Flag("use_aria2"));
            if (d["kind"]?.GetValue<string>() != "api")
            {
                var executable = d["executable"]?.GetValue<string>() ?? id;
                var check = await DependencyChecker.ExecutableAsync(id, value, executable, required, token);
                results.Add(check);
                if (executable == "ffmpeg" && check.State != DependencyState.Optional && !plugin.Manifest.Dependencies.Any(n => n?["id"]?.GetValue<string>() == "ffprobe"))
                    results.Add(await DependencyChecker.ExecutableAsync("ffprobe", ToolRunner.Ffprobe(value), "ffprobe", true, token));
                continue;
            }
            if (string.IsNullOrWhiteSpace(value)) { results.Add(new(id, value, required ? DependencyState.Missing : DependencyState.Optional, required ? "尚未配置" : "未配置（可选）")); continue; }
            try
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidDataException("API 地址无效。");
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await UpdateService.Http.GetAsync(value.TrimEnd('/') + "/openapi.json", budget.Token); response.EnsureSuccessStatusCode();
                results.Add(new(id, value, DependencyState.Ready, "已连接"));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { results.Add(new(id, value, DependencyState.TimedOut, "连接超时（5 秒）。")); }
            catch (Exception error) { results.Add(new(id, value, DependencyState.Failed, "不可用：" + error.Message)); }
        }
        return results;
    }
    public string DependencyValue(InstalledPlugin plugin, JsonObject dependency, JsonObject configuration)
    {
        var id = dependency["id"]!.GetValue<string>(); var preference = dependency["preference"]?.GetValue<string>() ?? id;
        var value = Store.IsOfficial(plugin) && dependency["preference"] is not null
            ? settings.Get(preference).Trim() : (configuration[id]?.GetValue<string>() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value)) value = dependency["default"]?.GetValue<string>() ?? "";
        return dependency["kind"]?.GetValue<string>() == "api" || value.Length == 0 ? value : ToolRunner.Resolve(value, dependency["executable"]?.GetValue<string>() ?? id);
    }
    public async Task<WorkResult> ExecuteAsync(string id, JsonObject inputs, string directory, CancellationToken token, Action<string, JsonObject>? onEvent = null)
    {
        EnsureIdle(id); if (!Enabled(id)) throw new InvalidOperationException("请先启用扩展。"); Running.Add(id);
        try
        {
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("请选择存在的输出目录。");
            var plugin = Store.Installed[id]; await Store.VerifyAsync(id); var configuration = await ConfigurationAsync(plugin);
            foreach (var field in plugin.Manifest.Fields)
                if (field!["required"]?.GetValue<bool>() == true && string.IsNullOrWhiteSpace(inputs[field["id"]!.GetValue<string>()]?.ToString())) throw new InvalidDataException($"请填写 {field["label"] ?? field["id"]}。");
            onEvent?.Invoke("log", new() { ["message"] = "正在检查扩展的配置与依赖。" });
            var checks = await InspectDependenciesAsync(plugin, configuration, token);
            foreach (var check in checks) onEvent?.Invoke("log", new() { ["message"] = DependencyChecker.Describe([check]) });
            var failures = checks.Where(p => !p.CanRun).ToArray();
            if (failures.Length > 0) throw new InvalidOperationException("请修复配置与依赖：" + DependencyChecker.Describe(failures));
            token.ThrowIfCancellationRequested();
            var dependencies = new JsonObject(); foreach (var d in plugin.Manifest.Dependencies) { var dependencyId = d!["id"]!.GetValue<string>(); dependencies[dependencyId] = checks.First(c => c.Id == dependencyId).Path; }
            var request = new JsonObject { ["inputs"] = inputs.DeepClone(), ["settings"] = configuration.DeepClone(), ["dependencies"] = dependencies, ["outputDirectory"] = directory };
            if (Store.IsOfficial(plugin) && UpdateService.CompareVersions(plugin.Manifest.Version, "1.1.0") < 0)
            { foreach (var p in inputs) request[p.Key] = p.Value?.DeepClone(); foreach (var p in configuration) request[p.Key] = p.Value?.DeepClone(); }
            onEvent?.Invoke("log", new() { ["message"] = "正在启动扩展：" + plugin.Manifest.Name });
            await using var runtime = await PluginRuntime.StartAsync(plugin.Executable, plugin.Directory, token);
            runtime.Event += onEvent;
            try
            {
                var executing = runtime.RequestAsync("execute", request, CancellationToken.None);
                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = token.Register(() => stopped.TrySetResult());
                if (await Task.WhenAny(executing, stopped.Task) != executing)
                {
                    try { await runtime.RequestAsync("cancel", new(), CancellationToken.None, TimeSpan.FromSeconds(2)); } catch { }
                    if (await Task.WhenAny(executing, Task.Delay(2000)) != executing)
                    {
                        _ = executing.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        return new([], [], true);
                    }
                }
                var result = await executing;
                var paths = (result["paths"] as JsonArray ?? []).Where(n => n is JsonValue v && v.TryGetValue<string>(out var p) && !string.IsNullOrWhiteSpace(p) && !p.Contains('\0') && !p.Contains("://"))
                    .Select(n => n!.GetValue<string>()).Select(p => Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(directory, p))).Distinct().ToArray();
                var errors = (result["errors"] as JsonArray ?? []).Select(n => n!.ToString()).ToList();
                if (result["exitCode"]?.GetValue<int>() is int exit && exit != 0 && errors.Count == 0) errors.Add($"插件退出码 {exit}：{result["output"]}");
                return new(paths, errors, token.IsCancellationRequested || result["cancelled"]?.GetValue<bool>() == true);
            }
            catch (OperationCanceledException)
            {
                try { await runtime.RequestAsync("cancel", new(), CancellationToken.None, TimeSpan.FromSeconds(2)); } catch { }
                throw;
            }
        }
        finally { Running.Remove(id); }
    }
    public async Task RefreshCatalogAsync(CancellationToken token)
    {
        using var response = await UpdateService.Http.GetAsync("https://raw.githubusercontent.com/Cydiacoft/VideoLab/main/extensions/catalog.json", HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token); using var output = new MemoryStream(); byte[] buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0) { if (output.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("扩展目录过大。"); output.Write(buffer, 0, count); }
        var envelope = JsonNode.Parse(output.ToArray())!.AsObject(); var key = PluginPackage.VerifyEnvelope(envelope);
        if (!Store.OfficialKeys.Contains(key) || envelope["payload"]?["catalogVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("官方目录签名或版本无效。");
        Catalog = envelope["payload"]!["plugins"]!.AsArray();
        foreach (var item in Catalog) _ = PluginManifest.Parse(item!["manifest"]!.AsObject());
    }
    public async Task InstallCatalogAsync(JsonObject entry, CancellationToken token)
    {
        var manifest = PluginManifest.Parse(entry["manifest"]!.AsObject()); EnsureIdle(manifest.Id);
        var bytes = await UpdateService.VerifiedDownloadAsync(entry["url"]!.GetValue<string>(), entry["size"]!.GetValue<long>(), entry["sha256"]!.GetValue<string>(), token);
        var package = PluginPackage.Decode(bytes); if (PluginPackage.Canonical(package.Manifest.Json) != PluginPackage.Canonical(manifest.Json)) throw new InvalidDataException("下载包与目录清单不符。");
        await Store.InstallAsync(bytes, token: token);
    }
}
