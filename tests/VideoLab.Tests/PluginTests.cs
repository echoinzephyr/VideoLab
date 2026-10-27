using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NSec.Cryptography;
using VideoLab.Core;
using Xunit;

namespace VideoLab.Tests;

public sealed class PluginTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "VideoLab-plugin-test-" + Guid.NewGuid().ToString("N"));
    private readonly Key signing = new(SignatureAlgorithm.Ed25519);
    private string PublicKey => Convert.ToBase64String(signing.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    public Task InitializeAsync() { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public async Task DisposeAsync() { signing.Dispose(); await FileOperations.DeleteDirectoryAsync(root, root); }
    private Dictionary<string, byte[]> Files(string version, string id = "fixture.echo")
    {
        Dictionary<string, byte[]> files = [];
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "worker")))
        { if (file.EndsWith(".pdb")) continue; var name = Path.GetFileName(file); files["bin/" + (name.EndsWith(".exe") ? "plugin.exe" : name)] = File.ReadAllBytes(file); }
        var manifest = new JsonObject { ["manifestVersion"] = 1, ["protocolVersion"] = 1, ["hostApiVersion"] = 1, ["id"] = id, ["name"] = "Fixture", ["publisher"] = "Tests", ["version"] = version, ["platform"] = "windows-x64", ["entrypoint"] = "bin/plugin.exe", ["fields"] = new JsonArray(), ["settings"] = new JsonArray(), ["dependencies"] = new JsonArray(), ["permissions"] = new JsonArray() };
        files["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString()); return files;
    }
    private byte[] Package(string version = "1.0.0", bool signed = true)
    {
        var files = Files(version); if (signed)
        {
            var payload = new JsonObject(); foreach (var p in files) payload[p.Key] = Convert.ToHexStringLower(SHA256.HashData(p.Value));
            var signature = SignatureAlgorithm.Ed25519.Sign(signing, Encoding.UTF8.GetBytes(PluginPackage.Canonical(payload)));
            files["signature.json"] = Encoding.UTF8.GetBytes(new JsonObject { ["publicKey"] = PublicKey, ["signature"] = Convert.ToBase64String(signature), ["payload"] = payload }.ToJsonString());
        }
        return Zip(files);
    }
    private static byte[] Zip(Dictionary<string, byte[]> files)
    {
        using var memory = new MemoryStream(); using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var p in files) { var entry = zip.CreateEntry(p.Key); using var output = entry.Open(); output.Write(p.Value); }
        return memory.ToArray();
    }
    private PluginStore Store() { var store = new PluginStore(Path.Combine(root, "plugins")); store.OfficialKeys.Add(PublicKey); return store; }
    [Fact] public async Task SignedInstallUpgradeRollbackPreservesSettings()
    {
        var store = Store(); await store.LoadAsync(); await store.InstallAsync(Package()); await store.SaveSettingsAsync("fixture.echo", new() { ["token"] = "fixture" });
        await store.InstallAsync(Package("1.1.0")); Assert.Equal("1.1.0", store.Installed["fixture.echo"].Manifest.Version);
        await store.RollbackAsync("fixture.echo"); Assert.Equal("1.0.0", store.Installed["fixture.echo"].Manifest.Version);
        Assert.Equal("fixture", (await store.SettingsAsync("fixture.echo"))["token"]!.GetValue<string>());
        var reload = Store(); await reload.LoadAsync(); Assert.Single(reload.Installed); await reload.VerifyAsync("fixture.echo");
        await reload.UninstallAsync("fixture.echo"); Assert.Empty(reload.Installed); Assert.Equal("fixture", (await reload.SettingsAsync("fixture.echo"))["token"]!.GetValue<string>());
    }
    [Fact] public async Task UnknownPublisherAndUnsignedPackagesRequireExplicitTrust()
    {
        var store = new PluginStore(Path.Combine(root, "plugins"));
        await Assert.ThrowsAsync<PublisherApprovalRequired>(() => store.InstallAsync(Package()));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(Package(signed: false)));
        await store.InstallAsync(Package(), true); Assert.Contains(PublicKey, store.TrustedKeys);
    }
    [Fact] public void TamperedArchivesAndTraversalAreRejected()
    {
        var package = PluginPackage.Decode(Package()); var files = package.Files.ToDictionary(); files["bin/plugin.exe"] = [1, 2, 3];
        Assert.Throws<InvalidDataException>(() => PluginPackage.Decode(Zip(files)));
        foreach (var path in new[] { "../escape", "/absolute", "bin\\escape", "bin/CON.txt", "bin/file:stream", "bin/../other" }) Assert.False(PluginPackage.SafePath(path));
        files = Files("1.0.0"); files["../escape"] = [1]; Assert.Throws<InvalidDataException>(() => PluginPackage.Decode(Zip(files)));
        files = Files("1.0.0"); files["MANIFEST.JSON"] = files["manifest.json"]; Assert.Throws<InvalidDataException>(() => PluginPackage.Decode(Zip(files)));
    }
    [Fact] public async Task InstalledFileTamperingIsDetected()
    {
        var store = Store(); var plugin = await store.InstallAsync(Package()); await File.WriteAllTextAsync(plugin.Executable, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.VerifyAsync(plugin.Manifest.Id));
    }
    [Fact] public async Task RuntimeRoundTripsAndRejectsMalformedMessages()
    {
        var plugin = await Store().InstallAsync(Package());
        await using var runtime = await PluginRuntime.StartAsync(plugin.Executable, plugin.Directory, CancellationToken.None);
        var result = await runtime.RequestAsync("execute", new() { ["inputs"] = new JsonObject { ["message"] = "中文" } }, CancellationToken.None);
        Assert.Equal("中文", result["request"]!["inputs"]!["message"]!.GetValue<string>());
        await Assert.ThrowsAnyAsync<Exception>(() => runtime.RequestAsync("execute", new() { ["inputs"] = new JsonObject { ["mode"] = "malformed" } }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
    }
    [Fact] public async Task ClosingRuntimeKillsDescendantProcesses()
    {
        var plugin = await Store().InstallAsync(Package()); var marker = Path.Combine(root, "survived.txt");
        var runtime = await PluginRuntime.StartAsync(plugin.Executable, plugin.Directory, CancellationToken.None);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); runtime.Event += (method, _) => { if (method == "started") started.TrySetResult(); };
        var executing = runtime.RequestAsync("execute", new() { ["inputs"] = new JsonObject { ["mode"] = "tree", ["marker"] = marker } }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await runtime.DisposeAsync(); await Assert.ThrowsAnyAsync<Exception>(() => executing); await Task.Delay(1200); Assert.False(File.Exists(marker));
    }
    [Fact] public async Task ManagerNormalizesOutputsAndCancellationPreservesCompletedFiles()
    {
        var settings = new SettingsStore(Path.Combine(root, "data")); await settings.LoadAsync(Path.Combine(root, "empty-legacy")); var manager = new PluginManager(settings, Path.Combine(root, "bundled")); manager.Store.OfficialKeys.Add(PublicKey); await manager.LoadAsync(); await manager.Store.InstallAsync(Package());
        var output = await manager.ExecuteAsync("fixture.echo", new(), root, CancellationToken.None); Assert.Equal(Path.Combine(root, "result.txt"), output.Paths.Single());
        using var cancel = new CancellationTokenSource(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executing = manager.ExecuteAsync("fixture.echo", new() { ["mode"] = "partial" }, root, cancel.Token, (method, _) => { if (method == "progress") started.TrySetResult(); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); var result = await executing;
        Assert.True(result.Cancelled); Assert.Equal(Path.Combine(root, "finished.txt"), result.Paths.Single()); Assert.True(File.Exists(result.Paths.Single())); Assert.Empty(manager.Running);
    }
}
