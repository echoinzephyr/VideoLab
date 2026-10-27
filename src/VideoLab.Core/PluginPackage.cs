using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NSec.Cryptography;

namespace VideoLab.Core;

public sealed record PluginManifest(JsonObject Json)
{
    public string Id => Json["id"]!.GetValue<string>();
    public string Name => Json["name"]!.GetValue<string>();
    public string Version => Json["version"]!.GetValue<string>();
    public string Entrypoint => Json["entrypoint"]!.GetValue<string>();
    public string Description => Json["description"]?.GetValue<string>() ?? "";
    public JsonArray Fields => Json["fields"] as JsonArray ?? [];
    public JsonArray Settings => Json["settings"] as JsonArray ?? [];
    public JsonArray Dependencies => Json["dependencies"] as JsonArray ?? [];
    public static PluginManifest Parse(JsonObject value)
    {
        foreach (var key in new[] { "id", "name", "publisher", "version", "entrypoint" })
            if (value[key] is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) throw new InvalidDataException($"插件清单缺少 {key}。");
        if (!Regex.IsMatch(value["id"]!.GetValue<string>(), "^[a-z0-9][a-z0-9.-]{0,79}$") || !Regex.IsMatch(value["version"]!.GetValue<string>(), @"^\d+\.\d+\.\d+$")) throw new InvalidDataException("插件 ID 或版本无效。");
        foreach (var key in new[] { "manifestVersion", "protocolVersion", "hostApiVersion" }) if (value[key]?.GetValue<int>() != 1) throw new InvalidDataException("插件协议不兼容。");
        if (value["platform"]?.GetValue<string>() != "windows-x64" || !PluginPackage.SafePath(value["entrypoint"]!.GetValue<string>())) throw new InvalidDataException("插件平台或入口路径无效。");
        foreach (var group in new[] { "fields", "settings", "dependencies", "permissions" })
            if (value[group] is not null && value[group] is not JsonArray) throw new InvalidDataException($"{group} 必须为数组。");
        foreach (var group in new[] { "fields", "settings" })
        {
            var ids = new HashSet<string>();
            foreach (var node in value[group] as JsonArray ?? [])
            {
                var field = node?.AsObject() ?? throw new InvalidDataException("插件字段无效。");
                var id = field["id"]?.GetValue<string>() ?? ""; var type = field["type"]?.GetValue<string>() ?? "";
                if (!Regex.IsMatch(id, "^[a-zA-Z][a-zA-Z0-9_-]*$") || !ids.Add(id) || !new[] { "text", "multiline", "secret", "bool", "select", "file", "directory" }.Contains(type)) throw new InvalidDataException("插件字段类型或 ID 无效。");
                if (type == "select" && field["options"] is not JsonArray) throw new InvalidDataException("插件选择字段缺少选项。");
            }
        }
        return new(value);
    }
}

public sealed record PluginPackage(PluginManifest Manifest, Dictionary<string, byte[]> Files, string? PublicKey, string Digest)
{
    public static bool SafePath(string path) => path.Length > 0 && !path.Contains('\\') && !path.Contains(':') && !path.StartsWith('/') && path.Split('/').All(p =>
        p.Length > 0 && p != "." && p != ".." && !p.EndsWith('.') && !p.EndsWith(' ') && !Regex.IsMatch(p, "[<>\"|?*\\x00-\\x1f]") && !Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase));
    public static string Canonical(JsonNode? node)
    {
        JsonNode? Sort(JsonNode? n) => n switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
            JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
            _ => n?.DeepClone()
        };
        return Sort(node)?.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
    }
    public static string VerifyEnvelope(JsonObject envelope)
    {
        var key = envelope["publicKey"]?.GetValue<string>() ?? throw new InvalidDataException("签名缺少公钥。");
        var signature = Convert.FromBase64String(envelope["signature"]?.GetValue<string>() ?? "");
        var payload = envelope["payload"] as JsonObject ?? throw new InvalidDataException("签名缺少有效载荷。");
        var publicKey = NSec.Cryptography.PublicKey.Import(SignatureAlgorithm.Ed25519, Convert.FromBase64String(key), KeyBlobFormat.RawPublicKey);
        if (!SignatureAlgorithm.Ed25519.Verify(publicKey, Encoding.UTF8.GetBytes(Canonical(payload)), signature)) throw new InvalidDataException("插件签名验证失败。");
        return key;
    }
    public static PluginPackage Decode(byte[] bytes)
    {
        if (bytes.Length > 256 * 1024 * 1024 || bytes.Length < 22) throw new InvalidDataException("插件包大小无效。");
        using var input = new MemoryStream(bytes, false); using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Count is < 1 or > 4096) throw new InvalidDataException("插件文件数量超出限制。");
        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase); long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName; var isDirectory = name.EndsWith('/');
            var mode = (entry.ExternalAttributes >> 16) & 0xf000;
            if (!SafePath(isDirectory ? name[..^1] : name) || (mode != 0 && mode != 0x8000 && !(isDirectory && mode == 0x4000))) throw new InvalidDataException("插件包含不安全路径或链接。");
            if (isDirectory) continue;
            expanded = checked(expanded + entry.Length);
            if (expanded > 512 * 1024 * 1024 || entry.Length > 256 * 1024 * 1024 || files.ContainsKey(name)) throw new InvalidDataException("插件展开大小或重复文件无效。");
            using var content = entry.Open(); using var output = new MemoryStream(); byte[] buffer = new byte[81920]; int count;
            while ((count = content.Read(buffer)) > 0) { if (output.Length + count > entry.Length) throw new InvalidDataException("插件实际大小超过声明。"); output.Write(buffer, 0, count); }
            if (output.Length != entry.Length) throw new InvalidDataException("插件内容不完整。");
            files[name] = output.ToArray();
        }
        if (!files.TryGetValue("manifest.json", out var manifestBytes)) throw new InvalidDataException("插件缺少 manifest.json。");
        var manifest = PluginManifest.Parse(JsonNode.Parse(manifestBytes)!.AsObject());
        if (!files.ContainsKey(manifest.Entrypoint)) throw new InvalidDataException("插件缺少执行入口。");
        string? key = null;
        if (files.TryGetValue("signature.json", out var signatureBytes))
        {
            var signature = JsonNode.Parse(signatureBytes)!.AsObject(); key = VerifyEnvelope(signature);
            var actual = new JsonObject(); foreach (var pair in files.Where(p => p.Key != "signature.json")) actual[pair.Key] = Convert.ToHexStringLower(SHA256.HashData(pair.Value));
            if (Canonical(actual) != Canonical(signature["payload"])) throw new InvalidDataException("插件文件与签名清单不符。");
        }
        return new(manifest, files, key, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}
