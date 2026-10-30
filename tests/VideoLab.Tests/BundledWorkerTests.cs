using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using VideoLab.Core;
using Xunit;

namespace VideoLab.Tests;

public sealed class BundledWorkerTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "VideoLab-bundled-test-" + Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public async Task DisposeAsync() { await FileOperations.DeleteDirectoryAsync(root, root); }
    private static string RepoRoot()
    { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VideoLab.slnx"))) directory = directory.Parent; return directory!.FullName; }
    private async Task<(SettingsStore, PluginManager)> ManagerAsync()
    { var settings = new SettingsStore(Path.Combine(root, "data")); await settings.LoadAsync(Path.Combine(root, "empty-legacy")); var manager = new PluginManager(settings, Path.Combine(RepoRoot(), "assets/extensions/generated")); await manager.LoadAsync(); Assert.Single(manager.Store.Installed); return (settings, manager); }
    [Theory]
    [InlineData("known", .5)]
    [InlineData("estimated", .25)]
    [InlineData("unknown", null)]
    [InlineData("zero", .25)]
    [InlineData("null", null)]
    [InlineData("negative", null)]
    [InlineData("overflow", null)]
    [InlineData("malformed", .5)]
    public async Task OfficialYtDlpWorkerRunsThroughCommonEnvelope(string mode, double? fraction)
    {
        var (settings, manager) = await ManagerAsync();
        settings.Set("yt_dlp_path", Path.Combine(AppContext.BaseDirectory, "worker", "VideoLab.TestWorker.exe"));
        settings.Set("ffmpeg_path", Environment.GetEnvironmentVariable("FFMPEG_TEST_PATH") ?? "ffmpeg.exe");
        var output = Path.Combine(root, "下载.mp4"); List<(string Method, JsonObject Data)> events = [];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await manager.ExecuteAsync("yt-dlp", new() { ["arguments"] = new JsonArray("--fixture-output", output, mode) }, root, timeout.Token, (method, data) => { lock (events) events.Add((method, data)); });
        Assert.False(result.Cancelled); Assert.Empty(result.Errors); Assert.Equal(output, result.Paths.Single()); Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(output));
        Assert.Equal(fraction, Assert.Single(events, e => e.Method == "progress").Data["fraction"]?.GetValue<double>());
        Assert.Contains(events, e => e.Method == "log" && e.Data["message"]?.ToString() == "fixture log after progress");
        if (mode is "estimated" or "unknown" or "negative")
        {
            var data = events.Single(e => e.Method == "progress").Data;
            Assert.Equal("", data["speed"]?.ToString()); Assert.Equal("", data["eta"]?.ToString());
        }
    }
    [Fact] public async Task OutputReaderFailureStopsDownloaderAndReportsError()
    {
        var (settings, manager) = await ManagerAsync();
        settings.Set("yt_dlp_path", Path.Combine(AppContext.BaseDirectory, "worker", "VideoLab.TestWorker.exe"));
        settings.Set("ffmpeg_path", Environment.GetEnvironmentVariable("FFMPEG_TEST_PATH") ?? "ffmpeg.exe");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await manager.ExecuteAsync("yt-dlp", new() { ["arguments"] = new JsonArray("--fixture-overlong") }, root, timeout.Token);
        Assert.False(result.Cancelled); Assert.Contains(result.Errors, error => error.Contains("输出行过长")); Assert.Empty(manager.Running);
    }
    [Fact] public async Task MissingDependencyPreventsWorkerExecution()
    {
        var (_, manager) = await ManagerAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ExecuteAsync("yt-dlp", new() { ["arguments"] = new JsonArray("ignored") }, root, CancellationToken.None));
        Assert.Contains("依赖", exception.Message); Assert.Empty(manager.Running);
    }
}
