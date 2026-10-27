using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace VideoLab.Core;

public static class DependencyInstaller
{
    public static async Task<string> YtDlpAsync(string root, CancellationToken token)
    {
        var release = await UpdateService.Http.GetFromJsonAsync<JsonObject>("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest", token) ?? throw new InvalidDataException("无法读取 yt-dlp 发布信息。");
        var assets = release["assets"]!.AsArray(); var asset = assets.First(n => n!["name"]!.GetValue<string>() == "yt-dlp.exe")!.AsObject();
        var hash = asset["digest"]?.GetValue<string>();
        if (hash?.StartsWith("sha256:") == true) hash = hash[7..];
        else
        {
            var checksum = assets.First(n => n!["name"]!.GetValue<string>() == "SHA2-256SUMS")!.AsObject();
            if (checksum["size"]!.GetValue<long>() > 1024 * 1024) throw new InvalidDataException("校验文件过大。");
            var content = await UpdateService.Http.GetStringAsync(checksum["browser_download_url"]!.GetValue<string>(), token);
            hash = content.Split('\n').Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length == 2 && p[1].TrimStart('*') == "yt-dlp.exe").Select(p => p[0]).Single();
        }
        var bytes = await UpdateService.VerifiedDownloadAsync(asset["browser_download_url"]!.GetValue<string>(), asset["size"]!.GetValue<long>(), hash, token);
        var directory = Path.Combine(root, "yt-dlp", hash[..16]); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "yt-dlp.exe");
        await File.WriteAllBytesAsync(path, bytes, token); var result = await ToolRunner.RunAsync(path, ["--version"], token); if (result.ExitCode != 0) throw new InvalidOperationException("下载的 yt-dlp 无法正常启动。"); return path;
    }
}
