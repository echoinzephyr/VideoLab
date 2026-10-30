using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Globalization;

namespace VideoLab.Core;

public sealed record CookieInspection(string Content, string Format, int Count, int Expired, int OtherDomains, string[] Domains)
{
    public string Summary => $"{Format} · 可用 {Count} 条" + (Expired > 0 ? $" · 已过期 {Expired} 条（忽略）" : "") + (OtherDomains > 0 ? $" · 其他站点 {OtherDomains} 条（忽略）" : "");
}
public sealed record SavedCookie(string Domain, string Path, string Status);
public sealed class CookieLease(string path) : IDisposable
{
    public string Path { get; } = path;
    public void Dispose() { if (Path.Length > 0) try { File.Delete(Path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
}

public static partial class CookieService
{
    public const int MaximumLength = 4 * 1024 * 1024;
    private static readonly SemaphoreSlim writes = new(1, 1);
    private sealed record Entry(string Host, bool Subdomains, string Path, bool Secure, long Expires, string Name, string Value, bool HttpOnly);
    public static readonly Dictionary<string, string> Platforms = new() { ["youtube"] = "youtube.com", ["bilibili"] = "bilibili.com", ["twitter"] = "x.com", ["tiktok"] = "tiktok.com", ["douyin"] = "douyin.com" };
    public static string Domain(string input)
    {
        var value = input.Trim().TrimStart('.');
        if (value.Contains("://")) { if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new FormatException("域名无效。"); value = uri.Host; }
        if (value.Length == 0 || value.Contains('/') || value.Contains(':') || value.Any(char.IsWhiteSpace) || !Uri.CheckHostName(value).Equals(UriHostNameType.Dns)) throw new FormatException("请输入有效的网站域名。");
        return new IdnMapping().GetAscii(value.TrimEnd('.')).ToLowerInvariant();
    }
    public static string Encode(string content, string domain)
    {
        var result = Inspect(content, domain);
        if (result.Count == 0) throw new FormatException(result.Expired > 0 ? "Cookie 均已过期，请重新导出登录信息。" : "没有匹配该站点的 Cookie，请检查网站域名。");
        return result.Content;
    }
    public static string SuggestDomain(string content, string downloadUrl = "")
    {
        var text = content.TrimStart('\uFEFF', ' ', '\r', '\n');
        if (text.StartsWith('[') || text.StartsWith('{') || text.StartsWith('#') || text.Count(c => c == '\t') >= 6)
        {
            var inspection = Inspect(content);
            if (inspection.Count == 0) throw new FormatException("登录信息已经过期，请重新导出后再导入。");
            var candidate = inspection.Domains.OrderBy(d => d.Length).First();
            if (inspection.Domains.All(d => d == candidate || d.EndsWith('.' + candidate, StringComparison.Ordinal))) return candidate;
        }
        return Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? Domain(uri.IdnHost) : "";
    }
    public static CookieInspection Inspect(string content, string domain = "")
    {
        if (content.Length > MaximumLength) throw new FormatException("Cookie 内容超过 4 MB 限制。");
        domain = domain.Length == 0 ? "" : Domain(domain);
        content = content.Trim(' ', '\r', '\n').TrimStart('\uFEFF').Trim(' ', '\r', '\n');
        List<Entry> entries = [];
        string format;
        if (content.StartsWith('[') || content.StartsWith('{'))
        {
            format = "JSON";
            try
            {
                var document = JsonNode.Parse(content);
                var array = document as JsonArray ?? document?["cookies"] as JsonArray;
                if (array is null) throw new FormatException("JSON 需要 Cookie 数组或包含 cookies 数组的对象。");
                foreach (var node in array)
                {
                    if (node is not JsonObject cookie) throw new FormatException("JSON 中存在无效的 Cookie 项。");
                    string Text(string key, string fallback = "") => cookie[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : cookie[key] is null ? fallback : throw new FormatException($"Cookie 的 {key} 必须为文本。");
                    bool Flag(string key, bool fallback = false) => cookie[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : cookie[key] is null ? fallback : throw new FormatException($"Cookie 的 {key} 必须为布尔值。");
                    var host = Text("domain", domain); var expires = 0L;
                    var expiration = cookie["expirationDate"] ?? cookie["expires"];
                    if (!Flag("session") && expiration is not null)
                    {
                        if (!double.TryParse(expiration.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds > 253402300799) throw new FormatException("Cookie 的过期时间无效，需要 Unix 秒数。");
                        expires = seconds <= 0 ? 0 : (long)Math.Floor(seconds);
                    }
                    entries.Add(new(host, !Flag("hostOnly", !host.StartsWith('.')), Text("path", "/"), Flag("secure"), expires, Text("name"), Text("value"), Flag("httpOnly")));
                }
            }
            catch (JsonException) { throw new FormatException("Cookie JSON 格式无效。"); }
        }
        else if (content.StartsWith('#') || content.Contains('\t') && !Regex.IsMatch(content, @"(?im)^\s*Cookie:"))
        {
            format = "Netscape";
            var lines = Regex.Split(content, "\\r\\n|\\n|\\r");
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i]; if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') && !line.StartsWith("#HttpOnly_")) continue;
                var fields = line.Split('\t');
                if (fields.Length != 7)
                { var match = NetscapeRow().Match(line); if (match.Success) fields = Enumerable.Range(1, 7).Select(n => match.Groups[n].Value).ToArray(); }
                if (fields.Length != 7 || !bool.TryParse(fields[1], out var subdomains) || !bool.TryParse(fields[3], out var secure) || !long.TryParse(fields[4], out var expires) || expires < 0) throw new FormatException($"Cookie 第 {i + 1} 行需要有效的 Netscape 7 列数据。");
                var httpOnly = fields[0].StartsWith("#HttpOnly_");
                entries.Add(new(httpOnly ? fields[0][10..] : fields[0], subdomains, fields[2], secure, expires, fields[5], fields[6], httpOnly));
            }
        }
        else
        {
            format = "Cookie 请求头";
            if (domain.Length == 0) throw new FormatException("Cookie 请求头不含域名，请先填写网站域名。");
            var cookieLines = Regex.Split(content, "\\r\\n|\\n|\\r").Where(l => l.TrimStart().StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (cookieLines.Length > 0)
            {
                if (Regex.Split(content, "\\r\\n|\\n|\\r").Any(l => !string.IsNullOrWhiteSpace(l) && !l.Contains(':') && !Regex.IsMatch(l, @"^[A-Z]+\s+\S+\s+HTTP/"))) throw new FormatException("请求头包含无法识别的行，请仅复制 Cookie 请求头。");
                content = string.Join("; ", cookieLines.Select(l => l[(l.IndexOf(':') + 1)..].Trim()));
            }
            if (content.StartsWith("Set-Cookie:", StringComparison.OrdinalIgnoreCase)) throw new FormatException("请复制请求中的 Cookie，而不是响应中的 Set-Cookie。");
            foreach (var part in content.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                var equal = part.IndexOf('='); if (equal < 1) throw new FormatException("Cookie 请求头需要 name=value 格式。");
                var name = part[..equal].Trim(); var value = part[(equal + 1)..].Trim();
                if (name.Any(c => char.IsWhiteSpace(c) || c is '\r' or '\n') || value.Contains('\r') || value.Contains('\n')) throw new FormatException("Cookie 内容包含非法换行。");
                entries.Add(new(domain, true, "/", false, 0, name, value, false));
            }
        }
        if (entries.Count == 0) throw new FormatException("未找到可用 Cookie。");
        if (entries.Count > 20000) throw new FormatException("Cookie 数量超过 20000 条限制。");
        Dictionary<(string Host, string Path, string Name), Entry> unique = [];
        foreach (var entry in entries)
        {
            var host = Domain(entry.Host);
            if (entry.Name.Length == 0 || entry.Name.Any(c => c <= 32 || c >= 127 || "()<>@,;:\\\"/[]?={}".Contains(c)) || entry.Value.Any(c => c is '\r' or '\n' or '\t' or '\0') || !entry.Path.StartsWith('/') || entry.Path.Any(c => c is '\r' or '\n' or '\t' or '\0')) throw new FormatException("Cookie 名称、路径或值含无效字符。");
            unique[(host, entry.Path, entry.Name)] = entry with { Host = host };
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var expired = 0; var other = 0; List<Entry> usable = [];
        foreach (var entry in unique.Values)
        {
            if (domain.Length > 0 && !(entry.Host == domain || entry.Host.EndsWith('.' + domain, StringComparison.Ordinal) || entry.Subdomains && domain.EndsWith('.' + entry.Host, StringComparison.Ordinal))) { other++; continue; }
            if (entry.Expires > 0 && entry.Expires <= now) { expired++; continue; }
            usable.Add(entry);
        }
        var rows = usable.Select(e => (e.HttpOnly ? "#HttpOnly_" : "") + (e.Subdomains ? "." : "") + e.Host + "\t" + (e.Subdomains ? "TRUE" : "FALSE") + "\t" + e.Path + "\t" + (e.Secure ? "TRUE" : "FALSE") + "\t" + e.Expires.ToString(CultureInfo.InvariantCulture) + "\t" + e.Name + "\t" + e.Value);
        return new("# Netscape HTTP Cookie File\n" + string.Join('\n', rows) + "\n", format, usable.Count, expired, other, usable.Select(e => e.Host).Distinct().Order().ToArray());
    }
    public static async Task SaveAsync(SettingsStore settings, string domain, string content)
    {
        domain = Domain(domain); var encoded = Encode(content, domain);
        await writes.WaitAsync();
        try
        {
            Directory.CreateDirectory(settings.CookieRoot);
            var path = Path.Combine(settings.CookieRoot, domain + ".txt"); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, encoded); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            var map = settings.Data["winui_cookie_files"] as JsonObject ?? new(); map[domain] = path; settings.Data["winui_cookie_files"] = map; await settings.SaveAsync();
        }
        finally { writes.Release(); }
    }
    private static Dictionary<string, string> Candidates(SettingsStore settings)
    {
        var candidates = new Dictionary<string, string>();
        foreach (var pair in settings.Data["winui_cookie_files"] as JsonObject ?? new())
            if (pair.Value is JsonValue value && value.TryGetValue<string>(out var path))
                try { candidates[Domain(pair.Key)] = path; } catch (FormatException) { }
        foreach (var pair in Platforms) candidates.TryAdd(pair.Value, Path.Combine(settings.CookieRoot, $"cookies_{pair.Key}.txt"));
        try
        {
            foreach (var custom in JsonNode.Parse(settings.Get("custom_cookies", "[]")) as JsonArray ?? [])
            {
                try
                {
                    if (custom is not JsonObject item || item["domain"] is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
                    var domain = Domain(text); var id = item["id"]?.GetValue<string>() ?? "";
                    var path = item["filePath"]?.GetValue<string>() ?? item["path"]?.GetValue<string>() ?? (Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,80}$") ? Path.Combine(settings.CookieRoot, $"cookie_custom_{id}.txt") : ""); candidates.TryAdd(domain, path);
                }
                catch (Exception error) when (error is FormatException or InvalidOperationException) { }
            }
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException) { }
        return candidates;
    }
    public static string Resolve(SettingsStore settings, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        var host = uri.IdnHost.ToLowerInvariant();
        var candidates = Candidates(settings);
        bool Matches(string domain)
        {
            if (host == domain || host.EndsWith('.' + domain, StringComparison.Ordinal)) return true;
            var canonical = host switch { "youtu.be" => "youtube.com", "b23.tv" => "bilibili.com", "v.douyin.com" => "douyin.com", "twitter.com" or "www.twitter.com" or "t.co" => "x.com", "x.com" or "www.x.com" => "twitter.com", _ => "" };
            return canonical.Length > 0 && (domain == canonical || domain == "www." + canonical);
        }
        var match = candidates.Where(p => Matches(p.Key))
            .OrderByDescending(p => p.Key.Length).FirstOrDefault(p => File.Exists(p.Value) || (settings.Data["winui_cookie_files"] as JsonObject)?.ContainsKey(p.Key) == true);
        if (match.Value is null) return "";
        if (!File.Exists(match.Value)) throw new FileNotFoundException("该站点的 Cookie 文件已丢失，请重新导入或切换 Cookie 来源。");
        return match.Value;
    }
    public static async Task<string> ReadAsync(string path, CancellationToken token = default)
    {
        if (new FileInfo(path).Length > MaximumLength) throw new FormatException("Cookie 文件超过 4 MB 限制。");
        return await File.ReadAllTextAsync(path, token);
    }
    public static async Task<IReadOnlyList<SavedCookie>> SavedAsync(SettingsStore settings)
    {
        var result = new List<SavedCookie>();
        foreach (var pair in Candidates(settings).OrderBy(p => p.Key))
        {
            if (!File.Exists(pair.Value) && !(settings.Data["winui_cookie_files"] as JsonObject ?? new()).ContainsKey(pair.Key)) continue;
            string status;
            try { status = Inspect(await ReadAsync(pair.Value), pair.Key).Summary; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException) { status = File.Exists(pair.Value) ? "内容无效，请重新导入" : "文件已丢失，请重新导入"; }
            result.Add(new(pair.Key, pair.Value, status));
        }
        return result;
    }
    public static async Task DeleteAsync(SettingsStore settings, string domain)
    {
        domain = Domain(domain); await writes.WaitAsync();
        try
        {
            var candidates = Candidates(settings);
            if (candidates.TryGetValue(domain, out var path) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(settings.CookieRoot), StringComparison.OrdinalIgnoreCase) && File.Exists(path)) File.Delete(path);
            foreach (var platform in Platforms.Where(p => p.Value == domain))
            {
                var legacy = Path.Combine(settings.CookieRoot, $"cookies_{platform.Key}.txt");
                if (File.Exists(legacy)) File.Delete(legacy);
            }
            (settings.Data["winui_cookie_files"] as JsonObject)?.Remove(domain);
            try
            {
                if (JsonNode.Parse(settings.Get("custom_cookies", "[]")) is JsonArray custom)
                {
                    foreach (var node in custom.ToArray())
                        if (node?["domain"] is JsonValue value && value.TryGetValue<string>(out var legacyDomain) && Domain(legacyDomain) == domain) custom.Remove(node);
                    settings.Set("custom_cookies", custom.ToJsonString());
                }
            }
            catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException) { }
            await settings.SaveAsync();
        }
        finally { writes.Release(); }
    }
    public static async Task<CookieLease> PrepareAsync(SettingsStore settings, string url, CancellationToken token)
    {
        var source = Resolve(settings, url);
        if (source.Length == 0) return new("");
        var inspection = Inspect(await ReadAsync(source, token));
        if (inspection.Count == 0) throw new FormatException("该站点的 Cookie 均已过期，请重新导出并导入。");
        var directory = Path.Combine(settings.CookieRoot, "active"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".txt");
        try { await File.WriteAllTextAsync(path, inspection.Content, token); return new(path); }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    public static Dictionary<string, string> DownloadOptions(IReadOnlyDictionary<string, string> options)
    {
        var result = options.ToDictionary();
        var browser = result.GetValueOrDefault("cookie-browser", "files");
        if (browser == "none") { result["cookie-browser"] = "files"; result.Remove("cookie-profile"); result.Remove("cookie-container"); return result; }
        if (!new[] { "files", "firefox", "edge", "chrome", "brave", "chromium", "opera", "vivaldi", "safari" }.Contains(browser)) throw new FormatException("不支持该 Cookie 来源，请重新选择。");
        if (browser == "files") { result.Remove("cookie-profile"); result.Remove("cookie-container"); return result; }
        var profile = result.GetValueOrDefault("cookie-profile", "").Trim().Trim('"');
        var container = browser == "firefox" ? result.GetValueOrDefault("cookie-container", "").Trim() : "";
        if ((profile + container).Any(c => c is '\r' or '\n' or '\0')) throw new FormatException("浏览器配置不能包含换行或空字符。");
        if (container.Contains(':')) throw new FormatException("Firefox 容器名称不能包含冒号。");
        result["cookie-profile"] = profile + (container.Length > 0 ? "::" + container : "");
        return result;
    }
    public static string FailureHint(string diagnostic)
    {
        var text = diagnostic.ToLowerInvariant();
        if (text.Contains("could not copy") && text.Contains("cookie") || text.Contains("database is locked")) return "无法读取浏览器 Cookie 数据库。请完全退出浏览器（包括后台进程）后重试，或导出 cookies.txt / JSON 后导入。";
        if (text.Contains("dpapi") || text.Contains("app-bound") || text.Contains("decrypt") && (text.Contains("cookie") || text.Contains("chrome") || text.Contains("chromium"))) return "浏览器 Cookie 解密失败。请使用当前 Windows 用户运行，更新 yt-dlp，或改用 Firefox / 手动导入。";
        if (text.Contains("cookie") && (text.Contains("expired") || text.Contains("fresh") || text.Contains("no longer valid")) || text.Contains("login required") || text.Contains("sign in to confirm")) return "站点需要有效登录信息。请在浏览器重新登录并导出最新 Cookie，确认来源和域名后重试。";
        return "";
    }
    [GeneratedRegex(@"^(\S+)\s+(TRUE|FALSE)\s+(\S+)\s+(TRUE|FALSE)\s+(\d+)\s+(\S+)\s+([^\r\n]*)$")]
    private static partial Regex NetscapeRow();
}
