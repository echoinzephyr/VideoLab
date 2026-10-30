using System.ComponentModel;
using System.Text.RegularExpressions;

namespace VideoLab.Core;

public enum DependencyState { Ready, Optional, Missing, Failed, TimedOut }

public sealed record DependencyCheck(string Id, string Path, DependencyState State, string Message, string? Version = null)
{
    public bool CanRun => State is DependencyState.Ready or DependencyState.Optional;
}

public static class DependencyChecker
{
    public static async Task<DependencyCheck> ExecutableAsync(string id, string value, string executable, bool required, CancellationToken token = default, int timeoutSeconds = 10)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(value) && !required) return new(id, "", DependencyState.Optional, "未配置（可选）");
        var path = value;
        try
        {
            path = ToolRunner.Resolve(value, executable);
            var result = await ToolRunner.RunAsync(path, [executable is "ffmpeg" or "ffprobe" ? "-version" : "--version"], token, timeoutSeconds);
            var summary = result.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            if (summary.Length > 300) summary = summary[..300];
            if (result.ExitCode != 0) return new(id, path, DependencyState.Failed, $"检测失败（退出码 {result.ExitCode}）：{summary}");
            if (summary.Length == 0) return new(id, path, DependencyState.Failed, "检测失败：工具未返回版本信息。");
            string? displayVersion = null;
            if (executable == "ffmpeg")
            {
                var version = FfmpegVersion.Parse(summary);
                if (version is null) return new(id, path, DependencyState.Failed, "该文件不是可识别的 FFmpeg。");
                displayVersion = version.Raw;
                if (version.Release is not null && version.Release < FfmpegVersion.Minimum)
                    return new(id, path, DependencyState.Failed, $"FFmpeg {version.Raw} 版本过低，需要 {FfmpegVersion.Minimum} 及以上版本。");
                if (version.Release is null)
                {
                    var capabilities = await ToolRunner.RunAsync(path, ["-hide_banner", "-encoders"], token, timeoutSeconds);
                    if (capabilities.ExitCode != 0 || !Regex.IsMatch(capabilities.Text, @"^\s*[VA][A-Z.]{5}\s+(?![=\s])\S+\s+", RegexOptions.Multiline))
                        return new(id, path, DependencyState.Failed, $"FFmpeg {version.Raw} 未报告可用的音频或视频编码器。");
                }
            }
            if (executable == "ffprobe" && !Regex.IsMatch(summary, @"^ffprobe version\s+\S+", RegexOptions.IgnoreCase))
                return new(id, path, DependencyState.Failed, "该文件不是可识别的 ffprobe。");
            return new(id, path, DependencyState.Ready, "已就绪：" + summary, displayVersion);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(id, path, DependencyState.TimedOut, $"检测超时（{timeoutSeconds} 秒），请检查工具是否能正常启动。"); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        { return new(id, path, DependencyState.Missing, "未找到可执行文件，请配置正确路径或将工具加入 PATH。"); }
        catch (Exception error) when (error is Win32Exception or IOException or ArgumentException or UnauthorizedAccessException)
        { return new(id, path, DependencyState.Failed, "不可用：" + error.Message); }
    }

    public static async Task<IReadOnlyList<DependencyCheck>> MediaAsync(string ffmpeg, CancellationToken token = default)
    {
        var engine = await ExecutableAsync("ffmpeg", ffmpeg, "ffmpeg", true, token);
        var probe = await ExecutableAsync("ffprobe", ToolRunner.Ffprobe(ffmpeg), "ffprobe", true, token);
        return new[] { engine, probe };
    }

    public static string Describe(IEnumerable<DependencyCheck> results) => string.Join("\n", results.Select(r => r.Id + "：" + r.Message + (r.Path.Length == 0 ? "" : "\n路径：" + r.Path)));
}
