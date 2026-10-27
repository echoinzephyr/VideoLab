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
    [Fact] public async Task OfficialYtDlpWorkerRunsThroughCommonEnvelope()
    {
        var (settings, manager) = await ManagerAsync();
        settings.Set("yt_dlp_path", Path.Combine(AppContext.BaseDirectory, "worker", "VideoLab.TestWorker.exe"));
        settings.Set("ffmpeg_path", Environment.GetEnvironmentVariable("FFMPEG_TEST_PATH") ?? "ffmpeg.exe");
        var output = Path.Combine(root, "下载.mp4"); List<string> events = [];
        var result = await manager.ExecuteAsync("yt-dlp", new() { ["arguments"] = new JsonArray("--fixture-output", output) }, root, CancellationToken.None, (method, _) => { lock (events) events.Add(method); });
        Assert.Empty(result.Errors); Assert.Equal(output, result.Paths.Single()); Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(output)); Assert.Contains("progress", events);
    }
    [Fact] public async Task MissingDependencyPreventsWorkerExecution()
    {
        var (_, manager) = await ManagerAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ExecuteAsync("yt-dlp", new() { ["arguments"] = new JsonArray("ignored") }, root, CancellationToken.None));
        Assert.Contains("依赖", exception.Message); Assert.Empty(manager.Running);
    }
}
