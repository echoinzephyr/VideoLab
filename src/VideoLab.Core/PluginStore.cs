using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace VideoLab.Core;

public sealed record InstalledPlugin(PluginManifest Manifest, string Directory, string? PublicKey, string Digest, JsonObject? Previous)
{
    public string Executable => Path.Combine(Directory, Manifest.Entrypoint.Replace('/', Path.DirectorySeparatorChar));
    public JsonObject Record() => new() { ["id"] = Manifest.Id, ["version"] = Manifest.Version, ["publicKey"] = PublicKey, ["sha256"] = Digest, ["previous"] = Previous?.DeepClone() };
}
public sealed class PublisherApprovalRequired(string publicKey) : Exception("此插件发布者尚未受信任，请检查来源后确认。")
{ public string PublicKey { get; } = publicKey; public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(PublicKey))); }

public sealed class PluginStore
{
    private readonly SemaphoreSlim changes = new(1, 1);
    public string Root { get; }
    public HashSet<string> OfficialKeys { get; } = [];
    public HashSet<string> TrustedKeys { get; } = [];
    public Dictionary<string, InstalledPlugin> Installed { get; } = [];
    public List<string> LoadErrors { get; } = [];
    public bool DeveloperMode { get; set; }
    public PluginStore(string root) => Root = Path.GetFullPath(root);
    private string Index => Path.Combine(Root, "registry.json");
    private string Archive(string id, string version) => Path.Combine(Root, "archives", id, version + ".vdx");
    private string PackageDirectory(string id, string version) => Path.Combine(Root, "packages", id, version);
    public bool IsOfficial(InstalledPlugin plugin) => plugin.PublicKey is not null && OfficialKeys.Contains(plugin.PublicKey) && new[] { "yt-dlp" }.Contains(plugin.Manifest.Id);
    public async Task LoadAsync()
    {
        Directory.CreateDirectory(Root);
        if (!File.Exists(Index) && File.Exists(Index + ".bak")) File.Copy(Index + ".bak", Index);
        if (!File.Exists(Index)) return;
        JsonObject data;
        try { data = JsonNode.Parse(await File.ReadAllTextAsync(Index))!.AsObject(); }
        catch { if (!File.Exists(Index + ".bak")) throw; data = JsonNode.Parse(await File.ReadAllTextAsync(Index + ".bak"))!.AsObject(); LoadErrors.Add("安装记录已从备份恢复。"); }
        DeveloperMode = data["developerMode"]?.GetValue<bool>() ?? false;
        foreach (var key in data["trustedKeys"] as JsonArray ?? []) TrustedKeys.Add(key!.GetValue<string>());
        foreach (var record in data["installed"] as JsonArray ?? [])
        {
            try { var plugin = await ReadRecordAsync(record!.AsObject()); Installed[plugin.Manifest.Id] = plugin; }
            catch (Exception error)
            {
                LoadErrors.Add(error.Message);
                if (record?["previous"] is JsonObject previous)
                    try { var fallback = await ReadRecordAsync(previous); Installed[fallback.Manifest.Id] = fallback; LoadErrors.Add("已恢复上一个有效插件版本。"); } catch (Exception fallbackError) { LoadErrors.Add(fallbackError.Message); }
            }
        }
    }
    private async Task<InstalledPlugin> ReadRecordAsync(JsonObject record)
    {
        var id = record["id"]!.GetValue<string>(); var version = record["version"]!.GetValue<string>();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9.-]{0,79}$") || !System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) throw new InvalidDataException("扩展记录路径无效。");
        var package = PluginPackage.Decode(await File.ReadAllBytesAsync(Archive(id, version)));
        if (package.Manifest.Id != id || package.Manifest.Version != version || package.Digest != record["sha256"]!.GetValue<string>() || package.PublicKey != record["publicKey"]?.GetValue<string>()) throw new InvalidDataException("已安装插件校验失败。");
        RequireTrusted(package);
        var directory = PackageDirectory(id, version);
        foreach (var entry in package.Files)
        {
            var file = Path.Combine(directory, entry.Key);
            for (var ancestor = new DirectoryInfo(Path.GetDirectoryName(file)!); ancestor is not null && ancestor.FullName.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); ancestor = ancestor.Parent)
                if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("插件目录包含链接，请重新安装。");
            if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || !SHA256.HashData(await File.ReadAllBytesAsync(file)).SequenceEqual(SHA256.HashData(entry.Value))) throw new InvalidDataException("已安装插件内容已改变，请重新安装。");
        }
        return new(package.Manifest, directory, package.PublicKey, package.Digest, record["previous"]?.AsObject());
    }
    private void RequireTrusted(PluginPackage package)
    {
        if (package.PublicKey is null) { if (!DeveloperMode) throw new InvalidDataException("未签名插件仅可在开发者模式下安装。"); }
        else if (!OfficialKeys.Contains(package.PublicKey) && !TrustedKeys.Contains(package.PublicKey)) throw new PublisherApprovalRequired(package.PublicKey);
    }
    public async Task VerifyAsync(string id) => _ = await ReadRecordAsync(Installed[id].Record());
    public async Task<InstalledPlugin> InstallAsync(byte[] bytes, bool trustPublisher = false, CancellationToken token = default)
    {
        await changes.WaitAsync(token); string? staging = null;
        try
        {
            var package = PluginPackage.Decode(bytes);
            if (trustPublisher && package.PublicKey is not null) TrustedKeys.Add(package.PublicKey);
            RequireTrusted(package); var manifest = package.Manifest; Installed.TryGetValue(manifest.Id, out var old);
            if (old is not null && old.PublicKey != package.PublicKey) throw new InvalidDataException("更新包发布者与已安装版本不同。");
            if (old is not null && UpdateService.CompareVersions(manifest.Version, old.Manifest.Version) <= 0) throw new InvalidDataException("请安装更新版本，或使用回滚功能。");
            staging = Path.Combine(Root, "staging", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
            foreach (var file in package.Files)
            { token.ThrowIfCancellationRequested(); var target = Path.Combine(staging, file.Key); Directory.CreateDirectory(Path.GetDirectoryName(target)!); await File.WriteAllBytesAsync(target, file.Value, token); }
            await using (var runtime = await PluginRuntime.StartAsync(Path.Combine(staging, manifest.Entrypoint), staging, token)) { }
            var directory = PackageDirectory(manifest.Id, manifest.Version);
            await FileOperations.DeleteDirectoryAsync(directory, Root);
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!); Directory.Move(staging, directory); staging = null;
            var archive = Archive(manifest.Id, manifest.Version); Directory.CreateDirectory(Path.GetDirectoryName(archive)!); await File.WriteAllBytesAsync(archive, bytes, token);
            var installed = new InstalledPlugin(manifest, directory, package.PublicKey, package.Digest, old?.Record());
            Installed[manifest.Id] = installed;
            try { await SaveAsync(); } catch { if (old is null) Installed.Remove(manifest.Id); else Installed[manifest.Id] = old; throw; }
            return installed;
        }
        finally { try { if (staging is not null) await FileOperations.DeleteDirectoryAsync(staging, Root); } finally { changes.Release(); } }
    }
    public async Task RollbackAsync(string id)
    {
        await changes.WaitAsync();
        try
        {
            var current = Installed[id]; var record = current.Previous ?? throw new InvalidOperationException("没有可回滚版本。");
            var old = await ReadRecordAsync(record); Installed[id] = old with { Previous = null };
            try { await SaveAsync(); } catch { Installed[id] = current; throw; }
        }
        finally { changes.Release(); }
    }
    public async Task UninstallAsync(string id)
    {
        await changes.WaitAsync();
        try
        {
            if (!Installed.Remove(id, out var old)) return;
            try { await SaveAsync(); } catch { Installed[id] = old; throw; }
            foreach (var directory in new[] { Path.Combine(Root, "packages", id), Path.Combine(Root, "archives", id) }) await FileOperations.DeleteDirectoryAsync(directory, Root);
        }
        finally { changes.Release(); }
        // Settings remain available for an intentional reinstall.
    }
    public async Task SaveAsync()
    {
        Directory.CreateDirectory(Root);
        var data = new JsonObject { ["developerMode"] = DeveloperMode, ["trustedKeys"] = new JsonArray(TrustedKeys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()), ["installed"] = new JsonArray(Installed.Values.Select(p => (JsonNode)p.Record()).ToArray()) };
        await File.WriteAllTextAsync(Index + ".tmp", data.ToJsonString()); if (File.Exists(Index)) File.Copy(Index, Index + ".bak", true); File.Move(Index + ".tmp", Index, true);
    }
    public async Task<JsonObject> SettingsAsync(string id)
    {
        var path = Path.Combine(Root, "settings", id, "settings.json");
        return File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject() : new();
    }
    public async Task SaveSettingsAsync(string id, JsonObject values)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9.-]{0,79}$")) throw new InvalidDataException("插件 ID 无效。");
        var path = Path.Combine(Root, "settings", id, "settings.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".tmp", values.ToJsonString()); File.Move(path + ".tmp", path, true);
    }
}
