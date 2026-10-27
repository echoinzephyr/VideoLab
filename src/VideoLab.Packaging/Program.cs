using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NSec.Cryptography;
using VideoLab.Core;

namespace VideoLab.Packaging;

internal static class Program
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;
    private static string Root()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "VideoLab.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("请从 VideoLab 工程目录运行。");
    }
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Directory.SetCurrentDirectory(Root());
            switch (args.FirstOrDefault())
            {
                case "bundle": await BundleAsync(args.Contains("--release")); break;
                case "verify":
                    var verified = PluginPackage.Decode(await File.ReadAllBytesAsync(args[1])); Console.WriteLine($"{verified.Manifest.Id} {verified.Manifest.Version} {verified.Digest}"); break;
                case "keygen":
                    using (var key = new Key(Algorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport }))
                    { await File.WriteAllTextAsync(args[1], Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey))); Console.WriteLine(Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey))); } break;
                case "pack":
                    using (var key = args.Length > 3 ? Key.Import(Algorithm, Convert.FromBase64String((await File.ReadAllTextAsync(args[3])).Trim()), KeyBlobFormat.RawPrivateKey) : null)
                        await File.WriteAllBytesAsync(args[2], Pack(args[1], key)); break;
                case "sign-catalog":
                    using (var key = Key.Import(Algorithm, Convert.FromBase64String((await File.ReadAllTextAsync(args[2])).Trim()), KeyBlobFormat.RawPrivateKey))
                        await File.WriteAllTextAsync(args[3], PluginPackage.Canonical(Envelope(JsonNode.Parse(await File.ReadAllTextAsync(args[1]))!.AsObject(), key))); break;
                default: throw new ArgumentException("用法：bundle [--release] | verify <file> | keygen <seed-file> | pack <directory> <output> [seed-file] | sign-catalog <payload> <seed-file> <output>");
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
    private static JsonObject Envelope(JsonObject payload, Key key) => new()
    {
        ["publicKey"] = Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)),
        ["signature"] = Convert.ToBase64String(Algorithm.Sign(key, Encoding.UTF8.GetBytes(PluginPackage.Canonical(payload)))),
        ["payload"] = payload.DeepClone()
    };
    private static async Task<Key> SigningKeyAsync(bool release)
    {
        var value = Environment.GetEnvironmentVariable("VIDEOLAB_PLUGIN_SIGNING_KEY");
        if (string.IsNullOrWhiteSpace(value))
        {
            if (release) throw new InvalidOperationException("正式发布必须配置 VIDEOLAB_PLUGIN_SIGNING_KEY。");
            var path = Path.Combine("build", "plugin-signing", "local.seed"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) await File.WriteAllTextAsync(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            value = await File.ReadAllTextAsync(path);
        }
        var seed = Convert.FromBase64String(value.Trim()); if (seed.Length != 32) throw new InvalidDataException("签名密钥必须是 32 字节 seed 的 Base64。");
        return Key.Import(Algorithm, seed, KeyBlobFormat.RawPrivateKey);
    }
    private static byte[] Pack(string directory, Key? key)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("插件包不能包含链接。");
            var name = Path.GetRelativePath(directory, path).Replace('\\', '/'); if (!PluginPackage.SafePath(name) || name == "signature.json") throw new InvalidDataException("打包路径无效。");
            files[name] = File.ReadAllBytes(path);
        }
        _ = PluginManifest.Parse(JsonNode.Parse(files["manifest.json"])!.AsObject());
        if (key is not null)
        {
            var payload = new JsonObject(); foreach (var file in files) payload[file.Key] = Convert.ToHexStringLower(SHA256.HashData(file.Value));
            files["signature.json"] = Encoding.UTF8.GetBytes(PluginPackage.Canonical(Envelope(payload, key)));
        }
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var file in files.OrderBy(p => p.Key, StringComparer.Ordinal))
            { var entry = archive.CreateEntry(file.Key, CompressionLevel.SmallestSize); entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero); using var output = entry.Open(); output.Write(file.Value); }
        var bytes = memory.ToArray(); _ = PluginPackage.Decode(bytes); return bytes;
    }
    private static async Task BundleAsync(bool release)
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) throw new PlatformNotSupportedException("随附插件构建需要 Windows x64。");
        using var key = await SigningKeyAsync(release); var publish = Path.GetFullPath("build/plugin-worker");
        using var process = new Process { StartInfo = new("dotnet") { UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in new[] { "publish", "src/VideoLab.PluginWorker/VideoLab.PluginWorker.csproj", "-c", "Release", "-o", publish, "-p:DebugType=None", "-p:DebugSymbols=false" }) process.StartInfo.ArgumentList.Add(argument);
        process.Start(); await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new InvalidOperationException("插件引擎编译失败。");
        var packages = new JsonArray(); var catalog = new JsonArray(); Directory.CreateDirectory("assets/extensions/generated");
        foreach (var id in new[] { "yt-dlp" })
        {
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync($"extensions/bundled/{id}/manifest.json"))!.AsObject();
            var directory = Path.GetFullPath($"build/plugin-packages/{id}"); await FileOperations.DeleteDirectoryAsync(directory, Path.GetFullPath("build")); Directory.CreateDirectory(Path.Combine(directory, "bin"));
            foreach (var file in Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories))
            { if (file.EndsWith(".pdb")) continue; var name = Path.GetRelativePath(publish, file); if (name == "VideoLab.PluginWorker.exe") name = "plugin.exe"; var output = Path.Combine(directory, "bin", name); Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(file, output); }
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), PluginPackage.Canonical(manifest));
            var bytes = Pack(directory, key); var asset = $"assets/extensions/generated/{id}.vdx"; await File.WriteAllBytesAsync(asset, bytes);
            packages.Add(new JsonObject { ["asset"] = asset, ["manifest"] = manifest.DeepClone() });
            var releaseBase = Environment.GetEnvironmentVariable("VIDEOLAB_PLUGIN_RELEASE_BASE");
            if (!string.IsNullOrWhiteSpace(releaseBase)) catalog.Add(new JsonObject { ["manifest"] = manifest.DeepClone(), ["url"] = releaseBase.TrimEnd('/') + '/' + id + ".vdx", ["size"] = bytes.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["releaseNotes"] = "C# 原生下载引擎" });
            Console.WriteLine($"Packaged {id} {manifest["version"]} ({bytes.Length} bytes)");
        }
        await File.WriteAllTextAsync("assets/extensions/generated/index.json", PluginPackage.Canonical(new JsonObject { ["officialKeys"] = new JsonArray(Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey))), ["packages"] = packages }));
        if (catalog.Count > 0) await File.WriteAllTextAsync("build/plugin-packages/catalog.json", PluginPackage.Canonical(Envelope(new() { ["catalogVersion"] = 1, ["plugins"] = catalog }, key)));
    }
}
