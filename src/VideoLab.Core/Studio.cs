using System.Text.Json.Nodes;
using VideoLab.Interop;

namespace VideoLab.Core;

public sealed class Studio : IAsyncDisposable
{
    private int engineRevision;
    public SettingsStore Settings { get; }
    public NativeSession Native { get; }
    public TaskQueue Queue { get; } = new();
    public PluginManager Plugins { get; }
    public HardwareInfo? Hardware { get; private set; }
    public EngineVerification Engine { get; } = new();
    public string Ffmpeg => ToolRunner.Resolve(Settings.Get("ffmpeg_path"), "ffmpeg");
    public Studio(string? dataRoot = null, string? bundledRoot = null)
    {
        Settings = new(dataRoot); Native = new(); Plugins = new(Settings, bundledRoot ?? Path.Combine(AppContext.BaseDirectory, "extensions"));
        Settings.Changed += key =>
        {
            if (key != "ffmpeg_path") return;
            ++engineRevision; Engine.Invalidate(); Hardware = null;
            Settings.Set("gpu_encoders", "[]"); Settings.Set("gpu_acceleration", false);
            foreach (var family in new[] { "h264", "hevc", "av1", "vp9" }) Settings.Set("gpu_" + family, "");
        };
    }
    public async Task InitializeAsync()
    {
        await Settings.LoadAsync();
        if (Settings.MigrationWarning.Length > 0) Queue.Log(Settings.MigrationWarning);
        try { await Plugins.LoadAsync(); if (Plugins.Warning.Length > 0) Queue.Log(Plugins.Warning); }
        catch (Exception error) { Queue.Log("扩展初始化失败：" + error.Message); }
        await Engine.VerifyAsync(Ffmpeg);
    }
    public async Task<WorkResult> MediaAsync(WorkItem? item, string[] args, string? output, bool stillImage, CancellationToken token)
    {
        var executable = Ffmpeg;
        var revision = engineRevision;
        var dependencies = await DependencyChecker.MediaAsync(executable, token);
        if (dependencies.Any(d => !d.CanRun)) throw new InvalidOperationException("请修复媒体依赖：" + DependencyChecker.Describe(dependencies.Where(d => !d.CanRun)));
        if (revision != engineRevision) throw new InvalidOperationException("媒体引擎配置已变化，请重新执行。");
        Queue.Log("FFmpeg " + Native.FormatArguments(args));
        double duration = 0;
        if (!stillImage && item is not null)
        {
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-i" && File.Exists(args[i + 1]))
            { var metadata = await Native.ProbeAsync(executable, args[i + 1], token); if (metadata.Duration > 0) duration += metadata.Duration; }
            var timeIndex = Array.IndexOf(args, "-t");
            if (timeIndex >= 0 && timeIndex + 1 < args.Length && double.TryParse(args[timeIndex + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var limited) && limited > 0) duration = limited;
        }
        var result = await Native.RunAsync(executable, args, output, stillImage, token, e =>
        {
            if (e.Type == 13 && e.Message.Length > 0) Queue.Log(e.Message);
            if (e.Type == 12 && item is not null)
            {
                var detail = string.IsNullOrWhiteSpace(e.Detail) ? new JsonObject() : JsonNode.Parse(e.Detail)!.AsObject();
                var status = "处理中 " + detail["out_time"] + " · " + detail["speed"] + " · " + detail["fps"] + " fps";
                Queue.Dispatch(() => item.Report(status, e.Fraction));
            }
        }, duration);
        if (result.Cancelled || token.IsCancellationRequested) return new(output is not null && File.Exists(output) ? [output] : [], [], true);
        if (result.Status != 0 || result.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? $"FFmpeg 处理失败（退出码 {result.ExitCode}），请查看日志。" : result.Error);
        return new(output is not null ? [output] : [], []);
    }
    public async Task DetectHardwareAsync(CancellationToken token = default)
    {
        var executable = Ffmpeg; var revision = engineRevision;
        var hardware = await Native.HardwareAsync(executable, token);
        foreach (var encoder in hardware.Gpu) Queue.Log("检测候选编码器：" + encoder);
        var verdicts = await Native.VerifyGpuAsync(executable, hardware.Gpu, token);
        token.ThrowIfCancellationRequested();
        if (revision != engineRevision) throw new InvalidOperationException("媒体引擎配置已变化，本次硬件检测结果已丢弃。");
        Hardware = hardware;
        var usable = verdicts.Where(v => v.Usable).Select(v => v.Encoder).ToArray(); Settings.Set("gpu_encoders", System.Text.Json.JsonSerializer.Serialize(usable));
        foreach (var v in verdicts) Queue.Log($"{v.Encoder}：{(v.Usable ? "可用" : v.Reason)}");
        foreach (var family in new[] { "h264", "hevc", "av1", "vp9" })
        {
            var current = Settings.Get("gpu_" + family); var candidates = usable.Where(e => e.StartsWith(family + "_", StringComparison.Ordinal)).ToArray();
            if (!candidates.Contains(current)) Settings.Set("gpu_" + family, candidates.FirstOrDefault() ?? "");
        }
        if (usable.Length == 0) Settings.Set("gpu_acceleration", false);
        await Settings.SaveAsync();
    }
    public Dictionary<string, string> SelectedGpu() => Settings.Flag("gpu_acceleration")
        ? new[] { "h264", "hevc", "av1", "vp9" }.ToDictionary(f => f, f => Settings.Get("gpu_" + f)) : [];
    public async Task<WorkResult> DownloadAsync(string url, string directory, int format, int height, Dictionary<string, string> options, CancellationToken token, WorkItem item)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new FormatException("请输入有效的媒体链接。");
        var source = options.GetValueOrDefault("cookie-browser", "files");
        var downloadOptions = CookieService.DownloadOptions(options);
        using var cookie = source == "files" ? await CookieService.PrepareAsync(Settings, url, token) : new CookieLease("");
        Queue.Log(source == "none" ? "Cookie：本次不使用登录信息。" : source == "files" ? cookie.Path.Length > 0 ? "Cookie：已匹配站点，使用独立副本。" : "Cookie：未找到匹配的站点，将以未登录状态下载。" : "Cookie：从 " + source + " 读取登录信息。");
        var args = Native.DownloadArguments(new { ffmpeg_path = Ffmpeg, download_path = directory, url, format, height, aria2_path = Settings.Flag("use_aria2") ? ToolRunner.Resolve(Settings.Get("aria2_path"), "aria2c") : "", cookie_path = cookie.Path, options = downloadOptions });
        var result = await Plugins.ExecuteAsync("yt-dlp", new() { ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(args) }, directory, token, (method, data) =>
        {
            if (method is "log" or "output") Queue.Log(data["message"]?.ToString() ?? data["line"]?.ToString() ?? "");
            if (method == "progress") Queue.Dispatch(() => item.Report((data["message"]?.ToString() ?? "正在下载") + " " + data["speed"] + " " + data["eta"], data["fraction"]?.GetValue<double>()));
        });
        if (!result.Cancelled && result.Errors.Count > 0)
        {
            var hint = CookieService.FailureHint(string.Join("\n", result.Errors));
            if (hint.Length > 0) result = result with { Errors = result.Errors.Concat(new[] { hint }).ToArray() };
        }
        if (!result.Cancelled && format == 0 && result.Errors.Count == 0)
        {
            var errors = new List<string>();
            foreach (var file in result.Paths)
            { var info = await Native.ProbeAsync(Ffmpeg, file, token); if (!info.Streams.Any(s => s.Kind == 1) || !info.Streams.Any(s => s.Kind == 2)) errors.Add("下载结果未同时包含视频与音频：" + file); }
            return result with { Errors = errors };
        }
        return result;
    }
    public async ValueTask DisposeAsync() { Queue.CancelAll(); while (Queue.Busy) await Task.Delay(25); await Native.DisposeAsync(); }
}
