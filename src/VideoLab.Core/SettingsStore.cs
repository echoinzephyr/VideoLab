using System.Text.Json;
using System.Text.Json.Nodes;

namespace VideoLab.Core;

public sealed class SettingsStore
{
    private readonly SemaphoreSlim writes = new(1, 1);
    public string Root { get; }
    public JsonObject Data { get; private set; } = new();
    public string FilePath => Path.Combine(Root, "settings.json");
    public string ExtensionsRoot => Path.Combine(Root, "extensions");
    public string CookieRoot => Path.Combine(Root, "cookies");
    public string MigrationWarning { get; private set; } = "";
    public SettingsStore(string? root = null) => Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoLab", "WinUI3");
    public string Get(string key, string fallback = "") => Data[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text : fallback;
    public bool Flag(string key, bool fallback = false) => Data[key] is JsonValue v && v.TryGetValue<bool>(out var value) ? value : fallback;
    public void Set<T>(string key, T value) => Data[key] = JsonSerializer.SerializeToNode(value);
    public async Task LoadAsync(string? legacyRoot = null, string? legacyCookies = null)
    {
        Directory.CreateDirectory(Root);
        if (File.Exists(FilePath))
        {
            try { Data = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))?.AsObject() ?? new(); }
            catch (JsonException)
            {
                var backup = FilePath + ".bak";
                if (!File.Exists(backup)) throw new InvalidDataException("设置文件损坏，请保留该目录后恢复备份。");
                Data = JsonNode.Parse(await File.ReadAllTextAsync(backup))!.AsObject();
                MigrationWarning = "设置文件损坏，已从备份恢复。";
            }
        }
        if (Flag("legacy_imported_v1")) return;
        var legacy = legacyRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "icu.meoji", "videoder_demo");
        var preferences = Path.Combine(legacy, "shared_preferences.json");
        if (File.Exists(preferences))
        {
            try
            {
                var old = JsonNode.Parse(await File.ReadAllTextAsync(preferences))!.AsObject();
                foreach (var item in old)
                {
                    var key = item.Key.StartsWith("flutter.", StringComparison.Ordinal) ? item.Key[8..] : item.Key;
                    if (key.StartsWith("tiktok_downloader_", StringComparison.Ordinal)) continue;
                    if (!Data.ContainsKey(key)) Data[key] = item.Value?.DeepClone();
                }
                CopyTree(Path.Combine(legacy, "extensions", "settings"), Path.Combine(ExtensionsRoot, "settings"));
                var oldCookies = legacyCookies ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "cookies");
                if (Directory.Exists(oldCookies))
                {
                    Directory.CreateDirectory(CookieRoot);
                    foreach (var file in Directory.EnumerateFiles(oldCookies, "*.txt").Where(p => Path.GetFileName(p).StartsWith("cookies_") || Path.GetFileName(p).StartsWith("cookie_custom_")))
                        if (!File.Exists(Path.Combine(CookieRoot, Path.GetFileName(file)))) File.Copy(file, Path.Combine(CookieRoot, Path.GetFileName(file)), false);
                }
                Set("legacy_source", legacy);
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            { MigrationWarning = $"旧配置导入尚未完成，可在设置中重试：{error.Message}"; await SaveAsync(); return; }
        }
        Set("legacy_imported_v1", true); await SaveAsync();
    }
    public static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var item in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || item.Name == "tiktok-downloader") continue;
            var target = Path.Combine(destination, item.Name);
            if (item is DirectoryInfo) CopyTree(item.FullName, target);
            else if (!File.Exists(target)) File.Copy(item.FullName, target);
        }
    }
    public async Task SaveAsync()
    {
        await writes.WaitAsync();
        try
        {
            Directory.CreateDirectory(Root);
            var text = Data.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(FilePath + ".tmp", text);
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", true);
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        finally { writes.Release(); }
    }
    public Dictionary<string, Dictionary<string, string>> Presets(string scope)
    {
        try
        {
            var raw = Get($"winui_presets_{scope}");
            if (raw.Length > 0) return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(raw) ?? [];
            var legacy = JsonNode.Parse(Get($"editor_presets_v1_{scope}", "{}"))!.AsObject();
            return legacy.ToDictionary(p => p.Key, p => MigratePreset(scope, p.Value!.AsObject()));
        }
        catch (JsonException) { return []; }
    }
    private static Dictionary<string, string> MigratePreset(string scope, JsonObject source)
    {
        var result = source.ToDictionary(v => v.Key, v => v.Value?.ToString() ?? "");
        var aliases = new Dictionary<string, string> { ["cropX"] = "crop_x", ["cropY"] = "crop_y", ["cropWidth"] = "crop_width", ["cropHeight"] = "crop_height", ["audioEncoder"] = "audio_encoder", ["videoBitrate"] = "video_bitrate", ["audioBitrate"] = "audio_bitrate", ["videoFilter"] = "video_filter", ["audioFilter"] = "audio_filter", ["speedPreset"] = "encoder_preset", ["rate"] = "sample_rate", ["audioKbps"] = "bitrate", ["opacity"] = "watermark_opacity", ["targetLufs"] = "target_lufs", ["subtitle"] = "subtitle_index" };
        foreach (var pair in aliases) if (result.TryGetValue(pair.Key, out var value)) { result[pair.Value] = value; result.Remove(pair.Key); }
        if (scope == "expert")
        {
            var audio = result.GetValueOrDefault("audioMode", "false").Equals("true", StringComparison.OrdinalIgnoreCase); result["mode"] = audio ? "音频" : "视频";
            if (result.TryGetValue(audio ? "audioFormat" : "videoFormat", out var format)) result["format"] = format;
            var videoPresets = new Dictionary<string, string> { ["transcode"] = "转码", ["remux"] = "换封装", ["resize"] = "缩放", ["rotate"] = "旋转", ["speed"] = "倍速", ["subtitles"] = "封装字幕", ["gif"] = "GIF", ["merge"] = "拼接" };
            var audioPresets = new Dictionary<string, string> { ["convert"] = "转换", ["trim"] = "剪切", ["merge"] = "拼接", ["normalize"] = "响度统一" };
            if (result.TryGetValue("preset", out var preset)) result["video_preset"] = videoPresets.GetValueOrDefault(preset, "转码");
            if (result.TryGetValue("audioPreset", out var ap)) result["audio_preset"] = audioPresets.GetValueOrDefault(ap, "转换");
        }
        if (scope == "utilities")
        {
            if (result.Remove("width", out var width)) result["watermark_width"] = width;
            var operations = new Dictionary<string, string> { ["mute"] = "视频静音", ["mirror"] = "水平镜像", ["verticalMirror"] = "垂直镜像", ["watermark"] = "图片水印", ["subtitles"] = "导出 SRT 字幕", ["denoise"] = "音频降噪", ["volume"] = "音量调整", ["normalize"] = "响度统一" };
            if (result.TryGetValue("operation", out var operation)) result["operation"] = operations.GetValueOrDefault(operation, operation);
        }
        return result;
    }
    public async Task SavePresetAsync(string scope, string name, Dictionary<string, string> values)
    {
        name = name.Trim(); if (name.Length is < 1 or > 40) throw new ArgumentException("预设名称需为 1–40 个字符。");
        var presets = Presets(scope);
        if (presets.Count >= 30 && !presets.ContainsKey(name)) throw new InvalidOperationException("每个编辑器最多保存 30 个预设。");
        presets[name] = values.Where(p => !new[] { "input", "output", "directory", "inputs", "watermark_path", "command" }.Contains(p.Key)).ToDictionary();
        Set($"winui_presets_{scope}", JsonSerializer.Serialize(presets)); await SaveAsync();
    }
    public async Task DeletePresetAsync(string scope, string name)
    { var presets = Presets(scope); presets.Remove(name); Set($"winui_presets_{scope}", JsonSerializer.Serialize(presets)); await SaveAsync(); }
}
