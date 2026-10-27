using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace VideoLab.Core;

public static partial class CookieService
{
    public static readonly Dictionary<string, string> Platforms = new() { ["youtube"] = "youtube.com", ["bilibili"] = "bilibili.com", ["twitter"] = "x.com", ["tiktok"] = "tiktok.com", ["douyin"] = "douyin.com" };
    public static string Domain(string input)
    {
        var value = input.Trim().TrimStart('.');
        if (value.Contains("://")) { if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new FormatException("域名无效。"); value = uri.Host; }
        if (value.Length == 0 || value.Contains('/') || value.Contains(':') || value.Any(char.IsWhiteSpace) || !Uri.CheckHostName(value).Equals(UriHostNameType.Dns)) throw new FormatException("请输入有效的网站域名。");
        return value.ToLowerInvariant();
    }
    public static string Encode(string content, string domain)
    {
        domain = Domain(domain); content = content.Trim(); List<string> rows = [];
        if (content.Contains('\t') || content.StartsWith("# Netscape") || content.StartsWith("# HTTP Cookie"))
        {
            var lines = Regex.Split(content, "\\r\\n|\\n|\\r");
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i]; if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') && !line.StartsWith("#HttpOnly_")) continue;
                var fields = line.Split('\t');
                if (fields.Length != 7)
                { var match = NetscapeRow().Match(line); if (match.Success) fields = Enumerable.Range(1, 7).Select(n => match.Groups[n].Value).ToArray(); }
                if (fields.Length != 7 || fields[1] is not ("TRUE" or "FALSE") || fields[3] is not ("TRUE" or "FALSE") || !fields[2].StartsWith('/') || !long.TryParse(fields[4], out var expires) || expires < 0) throw new FormatException($"Cookie 第 {i + 1} 行需要有效的 Netscape 7 列数据。");
                _ = Domain(fields[0].Replace("#HttpOnly_", "")); if (fields[5].Length == 0) continue; rows.Add(string.Join('\t', fields));
            }
        }
        else
        {
            if (content.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) content = content[7..].Trim();
            foreach (var part in content.Split(';'))
            {
                var equal = part.IndexOf('='); if (equal < 1) continue;
                var name = part[..equal].Trim(); var value = part[(equal + 1)..].Trim();
                if (name.Any(c => char.IsWhiteSpace(c) || c is '\r' or '\n') || value.Contains('\r') || value.Contains('\n')) throw new FormatException("Cookie 内容包含非法换行。");
                rows.Add($".{domain}\tTRUE\t/\tFALSE\t0\t{name}\t{value}");
            }
        }
        if (rows.Count == 0) throw new FormatException("未找到可用 Cookie。");
        return "# Netscape HTTP Cookie File\n" + string.Join('\n', rows) + "\n";
    }
    public static async Task SaveAsync(SettingsStore settings, string domain, string content)
    {
        domain = Domain(domain); Directory.CreateDirectory(settings.CookieRoot);
        var path = Path.Combine(settings.CookieRoot, domain + ".txt"); await File.WriteAllTextAsync(path, Encode(content, domain));
        var map = settings.Data["winui_cookie_files"] as JsonObject ?? new(); map[domain] = path; settings.Data["winui_cookie_files"] = map; await settings.SaveAsync();
    }
    public static string Resolve(SettingsStore settings, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        var candidates = new Dictionary<string, string>();
        foreach (var pair in settings.Data["winui_cookie_files"] as JsonObject ?? new()) candidates[pair.Key] = pair.Value!.GetValue<string>();
        foreach (var pair in Platforms) candidates.TryAdd(pair.Value, Path.Combine(settings.CookieRoot, $"cookies_{pair.Key}.txt"));
        try
        {
            foreach (var custom in JsonNode.Parse(settings.Get("custom_cookies", "[]"))!.AsArray())
            {
                var domain = Domain(custom!["domain"]!.GetValue<string>()); var id = custom["id"]?.GetValue<string>() ?? "";
                var path = custom["filePath"]?.GetValue<string>() ?? custom["path"]?.GetValue<string>() ?? (Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,80}$") ? Path.Combine(settings.CookieRoot, $"cookie_custom_{id}.txt") : ""); candidates.TryAdd(domain, path);
            }
        }
        catch (System.Text.Json.JsonException) { }
        return candidates.Where(p => File.Exists(p.Value) && (uri.Host == p.Key || uri.Host.EndsWith('.' + p.Key) || uri.Host == "v.douyin.com" && p.Key == "www.douyin.com"))
            .OrderByDescending(p => p.Key.Length).Select(p => p.Value).FirstOrDefault() ?? "";
    }
    [GeneratedRegex(@"^(\S+)\s+(TRUE|FALSE)\s+(\S+)\s+(TRUE|FALSE)\s+(\d+)\s+(\S+)\s+([^\r\n]*)$")]
    private static partial Regex NetscapeRow();
}
