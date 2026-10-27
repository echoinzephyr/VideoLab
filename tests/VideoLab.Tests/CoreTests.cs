using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSec.Cryptography;
using VideoLab.Core;
using VideoLab.Interop;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace VideoLab.Tests;

public sealed class CoreTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "VideoLab-tests-" + Guid.NewGuid().ToString("N"));
    private string Ffmpeg => Environment.GetEnvironmentVariable("FFMPEG_TEST_PATH") ?? "ffmpeg.exe";
    private string Source => Path.Combine(root, "中文 输入.mp4");
    public Task InitializeAsync() { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }
    private async Task FixtureAsync()
    { var result = await ToolRunner.RunAsync(Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=96x64:rate=10", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "1", "-c:v", "libx264", "-c:a", "aac", "-shortest", Source]); Assert.Equal(0, result.ExitCode); }
    [Fact] public void AbiLayoutsMatchNativeHeader()
    {
        Assert.Equal(24, Marshal.SizeOf<VDStringArray>()); Assert.Equal(88, Marshal.SizeOf<VDEvent>());
        Assert.Equal(72, Marshal.SizeOf<VDMediaTaskResult>()); Assert.Equal(96, Marshal.SizeOf<VDMediaInfo>());
        Assert.Equal(4u, Native.vd_core_abi_version());
    }
    [Theory]
    [InlineData("v26.10.27+8", "26.10.3+7", 1)]
    [InlineData("26.10.27+9", "26.10.27+8", 1)]
    [InlineData("26.10.27-winui.1+8", "26.10.27+8", -1)]
    [InlineData("26.10.27-winui.2", "26.10.27-winui.10", -1)]
    public void ReleaseVersionsCompare(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(UpdateService.CompareVersions(a, b)));
    [Fact] public async Task NativeArgumentsRoundTripAndValidate()
    {
        await using var core = new NativeSession();
        var args = new[] { "-i", "D:\\中文 空格\\file.mp4", "-vf", "scale=w='min(1920,iw)':h=-2", "quoted\"name" };
        Assert.Equal(args, core.ParseArguments(core.FormatArguments(args)));
        Assert.Contains("vp9", core.Codecs("webm")); Assert.DoesNotContain("h264", core.Codecs("webm"));
        Assert.Throws<InvalidOperationException>(() => core.BasicArguments(2, "in", "out", "mp4", crf: 99));
        Assert.Throws<InvalidOperationException>(() => core.ParseArguments("'unterminated"));
        for (int i = 0; i < 100; i++) Assert.NotEmpty(core.BasicArguments(0, "中文.mp4", "新文件.mp4", "mp4"));
    }
    [Fact] public async Task RealMediaProbeAndBasicOperations()
    {
        await FixtureAsync(); await using var core = new NativeSession(); var info = await core.ProbeAsync(Ffmpeg, Source);
        Assert.Contains(info.Streams, s => s.Kind == 1 && s.Width == 96); Assert.Contains(info.Streams, s => s.Kind == 2); Assert.True(info.Duration > 0);
        foreach (uint operation in new uint[] { 0, 1, 2, 3 })
        {
            var output = Path.Combine(root, $"基本_{operation}." + (operation == 1 ? "mp3" : "mp4"));
            var args = core.BasicArguments(operation, Source, output, operation == 1 ? "mp3" : "mp4", end: "0.5");
            var result = await core.RunAsync(Ffmpeg, args, output, false, CancellationToken.None); Assert.Equal(0, result.Status); Assert.True(new FileInfo(output).Length > 0);
        }
    }
    [Theory] [InlineData("png")] [InlineData("jpg")] [InlineData("webp")] [InlineData("bmp")] [InlineData("tiff")]
    public async Task RealImageFormatsAndPreview(string format)
    {
        await FixtureAsync(); await using var core = new NativeSession(); var output = Path.Combine(root, "截图." + format);
        var args = core.ImageArguments(new { operation = "frame", input_path = Source, output_path = output, format, time_seconds = .2, width = 48, rotation = 90, preview_max_width = 960 });
        var result = await core.RunAsync(Ffmpeg, args, output, true, CancellationToken.None); Assert.Equal(0, result.Status);
        var info = await core.ProbeAsync(Ffmpeg, output); Assert.Equal(48, info.Streams.First(s => s.Kind == 1).Height);
    }
    [Theory] [InlineData("mute", "mkv")] [InlineData("mirror", "mp4")] [InlineData("verticalMirror", "mp4")] [InlineData("denoise", "wav")] [InlineData("volume", "wav")] [InlineData("normalize", "wav")]
    public async Task RealUtilities(string operation, string extension)
    {
        await FixtureAsync(); await using var core = new NativeSession(); var output = Path.Combine(root, operation + "." + extension);
        var args = core.UtilityArguments(new { operation, input_path = Source, output_path = output, volume = .5, target_lufs = -16 });
        Assert.Equal(0, (await core.RunAsync(Ffmpeg, args, output, false, CancellationToken.None)).Status);
        var info = await core.ProbeAsync(Ffmpeg, output); if (operation == "mute") Assert.DoesNotContain(info.Streams, s => s.Kind == 2);
    }
    [Fact] public async Task RealExpertVideoAudioAndWatermark()
    {
        await FixtureAsync(); await using var core = new NativeSession();
        foreach (uint preset in new uint[] { 0, 1, 2, 3, 4, 6, 7 })
        {
            var extension = preset == 1 ? "mkv" : preset == 6 ? "gif" : "mp4"; var output = Path.Combine(root, $"expert{preset}.{extension}");
            var values = new Dictionary<string, string> { ["encoder"] = preset == 1 ? "copy" : "libx264", ["audio_encoder"] = preset == 1 ? "copy" : "aac" };
            var args = core.ExpertArguments(preset, preset == 7 ? [Source, Source] : [Source], output, values);
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var result = await core.RunAsync(Ffmpeg, core.ExecutionArguments(args), output, preset == 6, CancellationToken.None, e => { if (e.Type == 13) logs.Enqueue(e.Message); });
            Assert.True(result.Status == 0, $"Preset {preset}: {result} {string.Join(Environment.NewLine, logs)}");
        }
        var audio = Path.Combine(root, "声音.wav"); var audioArgs = core.AudioArguments(3, [Source], audio, "wav", 192, 48000, 2, "0", "1");
        Assert.Equal(0, (await core.RunAsync(Ffmpeg, core.ExecutionArguments(audioArgs), audio, false, CancellationToken.None)).Status);
        var image = Path.Combine(root, "watermark.png"); var imageArgs = core.ImageArguments(new { operation = "cover", input_path = Source, output_path = image, format = "png", width = 32 });
        Assert.Equal(0, (await core.RunAsync(Ffmpeg, imageArgs, image, true, CancellationToken.None)).Status);
        var preview = Path.Combine(root, "preview.png"); var watermark = core.UtilityArguments(new { operation = "watermark", input_path = Source, output_path = preview, watermark_path = image, watermark_width = 32, watermark_opacity = .5, position = "bottomRight", preview = true, preview_time = .2 });
        Assert.Equal(0, (await core.RunAsync(Ffmpeg, watermark, preview, true, CancellationToken.None)).Status);
    }
    [Fact] public async Task NativeCancellationAndRecoveryReleaseSlots()
    {
        await using var core = new NativeSession(); using var cancel = new CancellationTokenSource(200);
        var result = await core.RunAsync(Ffmpeg, ["-hide_banner", "-nostdin", "-re", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10", "-t", "60", "-f", "null", "-"], null, false, cancel.Token);
        Assert.True(result.Cancelled); Assert.NotEmpty(core.Formats());
        for (int i = 0; i < 66; i++) await Assert.ThrowsAsync<InvalidOperationException>(() => core.ProbeAsync(Ffmpeg, Path.Combine(root, "missing.mp4")));
    }
    [Theory] [InlineData(0u, "mp3")] [InlineData(1u, "wav")] [InlineData(2u, "flac")] [InlineData(3u, "m4a")]
    public async Task AudioWizardsExecuteWithConfiguredRateAndChannels(uint preset, string format)
    {
        await FixtureAsync(); await using var core = new NativeSession(); var output = Path.Combine(root, "audio." + format);
        var args = core.AudioArguments(preset, preset == 2 ? [Source, Source] : [Source], output, format, 192, 48000, 2, "0", "0.5");
        Assert.Equal(0, (await core.RunAsync(Ffmpeg, core.ExecutionArguments(args), output, false, CancellationToken.None)).Status);
        var info = await core.ProbeAsync(Ffmpeg, output); Assert.Contains(info.Streams, s => s.Kind == 2 && s.Channels == 2 && s.SampleRate == 48000);
    }
    [Fact] public async Task SubtitleMuxAndExportUseRealStreams()
    {
        await FixtureAsync(); await using var core = new NativeSession(); var subtitle = Path.Combine(root, "字幕.srt"); await File.WriteAllTextAsync(subtitle, "1\n00:00:00,000 --> 00:00:00,800\n中文字幕\n");
        var video = Path.Combine(root, "subtitled.mkv"); var args = core.ExpertArguments(5, [Source, subtitle], video, []);
        Assert.Equal(0, (await core.RunAsync(Ffmpeg, core.ExecutionArguments(args), video, false, CancellationToken.None)).Status);
        var output = Path.Combine(root, "exported.srt"); var export = core.UtilityArguments(new { operation = "subtitles", input_path = video, output_path = output, subtitle_index = 0 });
        Assert.Equal(0, (await core.RunAsync(Ffmpeg, export, null, false, CancellationToken.None)).Status); Assert.Contains("中文字幕", await File.ReadAllTextAsync(output));
    }
    [Fact] public async Task HardwareAndGpuResultsMarshalAndRelease()
    {
        await using var core = new NativeSession(); var hardware = await core.HardwareAsync(Ffmpeg); Assert.Contains("libx264", hardware.Video); Assert.Contains("aac", hardware.Audio);
        var verdicts = await core.VerifyGpuAsync(Ffmpeg, ["h264_nvenc"]); Assert.Single(verdicts); Assert.Equal("h264_nvenc", verdicts[0].Encoder); Assert.True(verdicts[0].Usable || verdicts[0].Reason.Length > 0);
    }
    [Fact] public async Task MigrationPreservesOriginalAndIsIdempotent()
    {
        var legacy = Path.Combine(root, "legacy"); Directory.CreateDirectory(legacy); var cookies = Path.Combine(root, "old-cookies"); Directory.CreateDirectory(cookies);
        await File.WriteAllTextAsync(Path.Combine(cookies, "cookies_douyin.txt"), "private-cookie");
        var original = "{\"flutter.ffmpeg_path\":\"D:/ffmpeg.exe\",\"flutter.gpu_acceleration\":true,\"flutter.editor_presets_v1_image\":\"{\\\"small\\\":{\\\"width\\\":320}}\"}";
        await File.WriteAllTextAsync(Path.Combine(legacy, "shared_preferences.json"), original);
        var settings = new SettingsStore(Path.Combine(root, "new")); await settings.LoadAsync(legacy, cookies);
        Assert.Equal("D:/ffmpeg.exe", settings.Get("ffmpeg_path")); Assert.True(settings.Flag("gpu_acceleration")); Assert.Equal("320", settings.Presets("image")["small"]["width"]);
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(legacy, "shared_preferences.json")));
        settings.Set("ffmpeg_path", "changed"); await settings.SaveAsync(); await settings.LoadAsync(legacy, cookies); Assert.Equal("changed", settings.Get("ffmpeg_path"));
        Assert.True(File.Exists(Path.Combine(settings.CookieRoot, "cookies_douyin.txt")));
    }
    [Fact] public async Task PresetsExcludePathsAndEnforceLimit()
    {
        var settings = new SettingsStore(root); await settings.LoadAsync(Path.Combine(root, "empty-legacy"));
        await settings.SavePresetAsync("image", "test", new() { ["width"] = "320", ["inputs"] = "private", ["directory"] = "private", ["watermark_path"] = "private" });
        Assert.Single(settings.Presets("image")["test"]);
        for (int i = 1; i < 30; i++) await settings.SavePresetAsync("image", "p" + i, new() { ["width"] = "1" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SavePresetAsync("image", "extra", []));
    }
    [Fact] public async Task LegacyExpertUtilitiesAndCustomCookiesRemainUsable()
    {
        var settings = new SettingsStore(root); await settings.LoadAsync(Path.Combine(root, "empty-legacy"));
        settings.Set("editor_presets_v1_expert", "{\"audio\":{\"audioMode\":true,\"audioPreset\":\"normalize\",\"audioFormat\":\"wav\",\"audioKbps\":192,\"rate\":48000}}");
        settings.Set("editor_presets_v1_utilities", "{\"logo\":{\"operation\":\"watermark\",\"width\":\"160\",\"opacity\":0.5}}");
        Assert.Equal("音频", settings.Presets("expert")["audio"]["mode"]); Assert.Equal("wav", settings.Presets("expert")["audio"]["format"]);
        Assert.Equal("图片水印", settings.Presets("utilities")["logo"]["operation"]); Assert.Equal("0.5", settings.Presets("utilities")["logo"]["watermark_opacity"]);
        Directory.CreateDirectory(settings.CookieRoot); var cookie = Path.Combine(settings.CookieRoot, "cookie_custom_123.txt"); await File.WriteAllTextAsync(cookie, "cookie");
        settings.Set("custom_cookies", "[{\"id\":\"123\",\"domain\":\"www.douyin.com\"}]"); Assert.Equal(cookie, CookieService.Resolve(settings, "https://v.douyin.com/123"));
    }
    [Fact] public void CookiesHandleHeadersNetscapeAndInvalidRows()
    {
        Assert.Contains("a\tvalue=1", CookieService.Encode("a=value=1; b=2", "douyin.com"));
        Assert.Contains("\tFALSE\t0\tname\tvalue", CookieService.Encode("# Netscape\n.douyin.com TRUE / FALSE 0 name value", "douyin.com"));
        Assert.Throws<FormatException>(() => CookieService.Encode("# Netscape\ninvalid", "douyin.com"));
    }
    [Fact] public async Task QueueContinuesAfterFailureAndCancellationWins()
    {
        var queue = new TaskQueue(); var first = queue.AddAsync("fail", "input", (_, _) => throw new IOException("fixture"));
        var completed = queue.AddAsync("success", "input", (_, _) => Task.FromResult(new WorkResult([], [])));
        Assert.Equal("失败", (await first).Status); Assert.Equal("完成", (await completed).Status);
        var late = queue.AddAsync("cancel", "input", async (item, _) => { item.Cancellation.Cancel(); await Task.Yield(); return new([], []); }); Assert.Equal("已取消", (await late).Status);
    }
    [Fact] public async Task SupersededPreviewDoesNotPublish()
    {
        await using var preview = new LivePreview(); List<string> published = [];
        preview.Schedule(async (directory, token) => { await Task.Delay(100, token); var file = Path.Combine(directory, "a"); await File.WriteAllTextAsync(file, "old", token); return file; }, (bytes, _) => published.Add(Encoding.UTF8.GetString(bytes!)));
        preview.Schedule(async (directory, token) => { var file = Path.Combine(directory, "b"); await File.WriteAllTextAsync(file, "new", token); return file; }, (bytes, _) => published.Add(Encoding.UTF8.GetString(bytes!)));
        await Task.Delay(700); Assert.Equal(["new"], published);
    }
    [Fact] public void NativeSignedBundledPackagesVerifyInDotnet()
    {
        var source = Environment.GetEnvironmentVariable("VIDEOLAB_REPO_ROOT") ?? FindRoot();
        foreach (var id in new[] { "yt-dlp" })
        { var package = PluginPackage.Decode(File.ReadAllBytes(Path.Combine(source, "assets/extensions/generated", id + ".vdx"))); Assert.Equal(id, package.Manifest.Id); Assert.NotNull(package.PublicKey); }
    }
    private static string FindRoot() { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VideoLab.slnx"))) directory = directory.Parent; return directory!.FullName; }
}
