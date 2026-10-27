using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace VideoLab.Core;

public sealed record ReleaseInfo(string Version, string Url, JsonArray Assets);
public static partial class UpdateService
{
    public const string Repository = "https://github.com/Cydiacoft/VideoLab";
    public static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient() { var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("VideoLab/26.10.27"); return client; }
    public static int CompareVersions(string left, string right)
    {
        static (int[] Core, string Pre, int Build) Parse(string value)
        {
            var m = VersionPattern().Match(value.Trim()); if (!m.Success) throw new FormatException("无法识别版本号。");
            return ([int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)], m.Groups[4].Value, int.TryParse(m.Groups[5].Value, out var build) ? build : -1);
        }
        var a = Parse(left); var b = Parse(right);
        for (int i = 0; i < 3; i++) if (a.Core[i] != b.Core[i]) return a.Core[i].CompareTo(b.Core[i]);
        if (a.Pre == b.Pre) return a.Build >= 0 && b.Build >= 0 ? a.Build.CompareTo(b.Build) : 0;
        if (a.Pre.Length == 0) return 1; if (b.Pre.Length == 0) return -1;
        var ap = a.Pre.Split('.'); var bp = b.Pre.Split('.');
        for (int i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        { var an = int.TryParse(ap[i], out var ai); var bn = int.TryParse(bp[i], out var bi); var c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(ap[i], bp[i]); if (c != 0) return c; }
        return ap.Length.CompareTo(bp.Length);
    }
    public static async Task<ReleaseInfo> LatestAsync(CancellationToken token = default)
    {
        JsonObject node;
        try { node = await Http.GetFromJsonAsync<JsonObject>("https://api.github.com/repos/Cydiacoft/VideoLab/releases/latest", token) ?? throw new InvalidDataException("发布信息为空。"); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
        { throw new InvalidOperationException("VideoLab 尚未发布正式版本，可从 GitHub Actions 下载构建产物。"); }
        return new(node["tag_name"]!.GetValue<string>(), node["html_url"]!.GetValue<string>(), node["assets"]!.AsArray());
    }
    public static async Task<byte[]> VerifiedDownloadAsync(string url, long expectedSize, string sha256, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || expectedSize is <= 0 or > 268435456 || !Regex.IsMatch(sha256, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("下载信息无效。");
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidDataException("拒绝不安全的下载重定向。");
        using var output = new MemoryStream(); await using var input = await response.Content.ReadAsStreamAsync(token);
        byte[] buffer = new byte[81920]; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0) { if (output.Length + count > expectedSize) throw new InvalidDataException("下载超出声明大小。"); await output.WriteAsync(buffer.AsMemory(0, count), token); }
        var bytes = output.ToArray();
        if (bytes.LongLength != expectedSize || !Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("下载校验失败。");
        return bytes;
    }
    [GeneratedRegex(@"^[vV]?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+(\d+))?$")]
    private static partial Regex VersionPattern();
}
