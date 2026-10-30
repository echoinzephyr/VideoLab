using VideoLab.Core;
using System.Text.Json.Nodes;
using Xunit;

namespace VideoLab.Tests;

public sealed class DependencyTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "VideoLab-dependencies-" + Guid.NewGuid().ToString("N"));
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "worker"))) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        foreach (var tool in new[] { "ffmpeg", "ffprobe" }) File.Copy(Path.Combine(root, "VideoLab.TestWorker.exe"), Path.Combine(root, tool + ".exe"));
        return Task.CompletedTask;
    }
    public Task DisposeAsync() => FileOperations.DeleteDirectoryAsync(root, root);
    private Task Mode(string value) => File.WriteAllTextAsync(Path.Combine(root, "dependency-mode.txt"), value);

    [Theory]
    [InlineData("3.4.8", true)] [InlineData("4.0.99", true)] [InlineData("4.1", false)]
    [InlineData("4.10.0", false)] [InlineData("10.0.0", false)] [InlineData("n4.4.1", false)]
    [InlineData("7.1-essentials_build-www.gyan.dev", false)]
    public void ReleaseVersionsCompareNumerically(string raw, bool old)
    {
        var version = FfmpegVersion.Parse("ffmpeg version " + raw)!;
        Assert.Equal(raw, version.Raw); Assert.NotNull(version.Release); Assert.Equal(old, version.Release < FfmpegVersion.Minimum);
    }
    [Theory]
    [InlineData("N-119821-gabcdef")] [InlineData("git-2026-10-25-abc")]
    [InlineData("2026-10-25-git-abc")] [InlineData("n4.1-dev-123-gabcdef")]
    [InlineData("4.1-rc1")] [InlineData("7.1-123-gabcdef")]
    public void DevelopmentVersionsKeepTheirIdentity(string raw)
    { var version = FfmpegVersion.Parse("ffmpeg version " + raw)!; Assert.Equal(raw, version.Raw); Assert.Null(version.Release); }

    [Fact] public async Task ChecksBothMediaToolsAndRejectsWrongExecutable()
    {
        var results = await DependencyChecker.MediaAsync(root); Assert.All(results, r => Assert.True(r.CanRun, r.Message));
        File.Delete(Path.Combine(root, "ffprobe.exe"));
        results = await DependencyChecker.MediaAsync(root); Assert.Equal(DependencyState.Missing, results[1].State);
        var wrong = await DependencyChecker.ExecutableAsync("ffmpeg", Path.Combine(root, "VideoLab.TestWorker.exe"), "ffmpeg", true);
        Assert.Equal(DependencyState.Failed, wrong.State);
    }
    [Fact] public async Task OldReleasesAndUnusableDevelopmentBuildsFail()
    {
        await Mode("4.0.99"); var check = await DependencyChecker.ExecutableAsync("ffmpeg", root, "ffmpeg", true); Assert.False(check.CanRun); Assert.Contains("版本过低", check.Message);
        await Mode("no-encoders"); check = await DependencyChecker.ExecutableAsync("ffmpeg", root, "ffmpeg", true); Assert.False(check.CanRun); Assert.Contains("编码器", check.Message);
    }
    [Fact] public async Task MissingOptionalAndEnabledToolsHaveDifferentOutcomes()
    {
        Assert.Equal(DependencyState.Optional, (await DependencyChecker.ExecutableAsync("aria2", "", "aria2c", false)).State);
        Assert.False((await DependencyChecker.ExecutableAsync("aria2", Path.Combine(root, "missing.exe"), "aria2c", true)).CanRun);
    }
    [Theory] [InlineData("fail")] [InlineData("empty")]
    public async Task FailureAndEmptyOutputNeverCountAsReady(string mode)
    {
        await Mode(mode); var check = await DependencyChecker.ExecutableAsync("fixture", root, "VideoLab.TestWorker", true);
        Assert.False(check.CanRun); if (mode == "fail") { Assert.Contains("42", check.Message); Assert.Contains("fixture cannot start", check.Message); }
    }
    [Fact] public async Task TimeoutIsReportedAndUserCancellationPropagates()
    {
        await Mode("hang");
        var check = await DependencyChecker.ExecutableAsync("fixture", root, "VideoLab.TestWorker", true, timeoutSeconds: 1); Assert.Equal(DependencyState.TimedOut, check.State);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DependencyChecker.ExecutableAsync("fixture", root, "VideoLab.TestWorker", true, cancellation.Token));
    }
    [Fact] public void ResolvesQuotedDirectoriesBinAndPathCommands()
    {
        Assert.Equal(Path.Combine(root, "ffmpeg.exe"), ToolRunner.Resolve(" \"" + root + "\" ", "ffmpeg"));
        Directory.CreateDirectory(Path.Combine(root, "bin")); File.Move(Path.Combine(root, "ffmpeg.exe"), Path.Combine(root, "bin", "ffmpeg.exe"));
        Assert.Equal(Path.Combine(root, "bin", "ffmpeg.exe"), ToolRunner.Resolve(root, "ffmpeg"));
        Assert.True(Path.IsPathFullyQualified(ToolRunner.Resolve("dotnet", "dotnet")));
        Assert.Equal(Path.Combine(root, "bin", "ffprobe.exe"), ToolRunner.Ffprobe(root));
    }
    [Fact] public async Task LateChecksCannotOverwriteReplacementAndSamePathCanBeRechecked()
    {
        var old = new TaskCompletionSource<IReadOnlyList<DependencyCheck>>();
        var engine = new EngineVerification((path, _) => path == "old" ? old.Task : Task.FromResult<IReadOnlyList<DependencyCheck>>([new("ffmpeg", path, DependencyState.Failed, "broken")]));
        var first = engine.VerifyAsync("old"); engine.Invalidate(); await engine.VerifyAsync("new");
        old.SetResult([new("ffmpeg", "old", DependencyState.Ready, "ready")]); await first;
        Assert.Equal(EngineState.Failed, engine.State); Assert.Equal("new", engine.Results.Single().Path);
        await engine.VerifyAsync("new"); Assert.Equal(EngineState.Failed, engine.State);
        engine.Invalidate(); Assert.Equal(EngineState.Unverified, engine.State); Assert.Empty(engine.Results);
    }
    [Fact] public async Task RepairedExecutableAtSamePathBecomesReady()
    {
        var calls = 0;
        var engine = new EngineVerification((path, _) => Task.FromResult<IReadOnlyList<DependencyCheck>>([new("ffmpeg", path, calls++ == 0 ? DependencyState.Failed : DependencyState.Ready, "fixture")]));
        await engine.VerifyAsync(root); Assert.Equal(EngineState.Failed, engine.State);
        await engine.VerifyAsync(root); Assert.Equal(EngineState.Ready, engine.State);
    }
    [Fact] public async Task OfficialAria2BecomesRequiredWhenEnabled()
    {
        var settings = new SettingsStore(root); var manager = new PluginManager(settings, root); manager.Store.OfficialKeys.Add("fixture-key");
        var plugin = new InstalledPlugin(new PluginManifest(new JsonObject { ["id"] = "yt-dlp", ["dependencies"] = new JsonArray(new JsonObject { ["id"] = "aria2", ["executable"] = "fixture-missing-aria2", ["required"] = false }) }), root, "fixture-key", "fixture", null);
        Assert.Equal(DependencyState.Optional, (await manager.InspectDependenciesAsync(plugin, new(), default)).Single().State);
        settings.Set("use_aria2", true);
        Assert.False((await manager.InspectDependenciesAsync(plugin, new(), default)).Single().CanRun);
    }
    [Fact] public async Task SettingsOnlyInvalidateWhenValueChanges()
    {
        var settings = new SettingsStore(root); var changes = 0; settings.Changed += _ => changes++;
        settings.Set("ffmpeg_path", root); settings.Set("ffmpeg_path", root); Assert.Equal(1, changes);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EngineVerification().VerifyAsync(root, cancel.Token));
    }
    [Fact] public void OfficialToolsFollowSharedSettingsIncludingClearingAPath()
    {
        var settings = new SettingsStore(root); var manager = new PluginManager(settings, root); manager.Store.OfficialKeys.Add("fixture-key");
        var plugin = new InstalledPlugin(new PluginManifest(new JsonObject { ["id"] = "yt-dlp" }), root, "fixture-key", "fixture", null);
        var dependency = new JsonObject { ["id"] = "ffmpeg", ["preference"] = "ffmpeg_path", ["executable"] = "ffmpeg" };
        var oldPath = Path.Combine(root, "missing-old-ffmpeg.exe"); var configuration = new JsonObject { ["ffmpeg"] = oldPath };
        settings.Set("ffmpeg_path", root); Assert.Equal(Path.Combine(root, "ffmpeg.exe"), manager.DependencyValue(plugin, dependency, configuration));
        settings.Set("ffmpeg_path", ""); Assert.Equal("", manager.DependencyValue(plugin, dependency, configuration));
        var thirdParty = plugin with { Manifest = new PluginManifest(new JsonObject { ["id"] = "fixture.plugin" }) };
        Assert.Equal(oldPath, manager.DependencyValue(thirdParty, dependency, configuration));
    }
}
