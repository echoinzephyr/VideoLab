using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VideoLab.PluginWorker;

internal static class Program
{
    private static readonly object outputLock = new();
    private static CancellationTokenSource? active;
    private static int running;
    private static string kind = "";
    private static void Send(JsonObject message) { message["jsonrpc"] = "2.0"; lock (outputLock) { Console.WriteLine(message.ToJsonString()); Console.Out.Flush(); } }
    internal static void Event(string method, JsonObject values) => Send(new() { ["method"] = method, ["params"] = values });
    private static void Reply(JsonNode? id, JsonObject result) => Send(new() { ["id"] = id?.DeepClone(), ["result"] = result });
    public static async Task Main()
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        var manifest = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "manifest.json"));
        kind = JsonNode.Parse(await File.ReadAllTextAsync(manifest))!["id"]!.GetValue<string>();
        Task? executing = null;
        while (await Console.In.ReadLineAsync() is string line)
        {
            JsonObject? message = null;
            try
            {
                if (line.Length > 1024 * 1024) throw new InvalidDataException("请求超过大小限制。");
                message = JsonNode.Parse(line)!.AsObject(); var method = message["method"]!.GetValue<string>(); var values = message["params"] as JsonObject ?? new();
                switch (method)
                {
                    case "initialize":
                        if (values["protocolVersion"]?.GetValue<int>() != 1 || values["hostApiVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("插件协议不兼容。");
                        Reply(message["id"], new() { ["protocolVersion"] = 1, ["hostApiVersion"] = 1, ["kind"] = kind }); break;
                    case "execute":
                        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("已有任务正在执行。");
                        active = new(); executing = ExecuteAsync(message["id"]?.DeepClone(), values, active.Token); break;
                    case "cancel": active?.Cancel(); Reply(message["id"], new() { ["cancelled"] = true }); break;
                    default: throw new InvalidOperationException("未知插件方法。");
                }
            }
            catch (Exception error) { Send(new() { ["id"] = message?["id"]?.DeepClone(), ["error"] = new JsonObject { ["code"] = -32000, ["message"] = error.Message } }); }
        }
        active?.Cancel(); if (executing is not null) await executing;
    }
    private static async Task ExecuteAsync(JsonNode? id, JsonObject request, CancellationToken token)
    {
        var paths = new List<string>(); var errors = new List<string>(); var exit = 0;
        try
        {
            var inputs = request["inputs"] as JsonObject ?? request; var settings = request["settings"] as JsonObject ?? new(); var dependencies = request["dependencies"] as JsonObject ?? new();
            var directory = request["outputDirectory"]?.GetValue<string>() ?? throw new InvalidDataException("缺少输出目录。");
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("输出目录不存在。");
            if (kind == "yt-dlp") exit = await RunYtDlpAsync(inputs, dependencies, paths, errors, token);
            else throw new InvalidOperationException("未知下载引擎。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { errors.Add(error.Message); exit = -1; Event("log", new() { ["message"] = error.Message }); }
        finally
        {
            Reply(id, new() { ["paths"] = JsonSerializer.SerializeToNode(paths), ["errors"] = JsonSerializer.SerializeToNode(errors), ["exitCode"] = exit, ["cancelled"] = token.IsCancellationRequested });
            active?.Dispose(); active = null; Interlocked.Exchange(ref running, 0);
        }
    }
    private static async Task<int> RunYtDlpAsync(JsonObject inputs, JsonObject dependencies, List<string> paths, List<string> errors, CancellationToken token)
    {
        var executable = dependencies["yt-dlp"]?.GetValue<string>() ?? inputs["executable"]?.GetValue<string>() ?? "";
        if (executable.Length == 0 || inputs["arguments"] is not JsonArray arguments) throw new InvalidDataException("缺少 yt-dlp 路径或参数。");
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument!.GetValue<string>());
        token.ThrowIfCancellationRequested(); process.Start();
        using var cancel = token.Register(() => { try { process.Kill(true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
        var diagnostic = new StringBuilder(); var valuesLock = new object();
        async Task ReadAsync(StreamReader reader)
        {
            char[] buffer = new char[4096]; var line = new StringBuilder(); int count;
            while ((count = await reader.ReadAsync(buffer, token)) > 0)
                for (int i = 0; i < count; i++)
                    if (buffer[i] == '\n') { Consume(line.ToString().TrimEnd('\r')); line.Clear(); }
                    else { if (line.Length >= 65536) throw new InvalidDataException("下载器输出行过长。"); line.Append(buffer[i]); }
            if (line.Length > 0) Consume(line.ToString());
        }
        void Consume(string line)
        {
            if (line.StartsWith("__VIDEOADER_OUTPUT__:"))
            { try { var path = JsonSerializer.Deserialize<string>(line["__VIDEOADER_OUTPUT__:".Length..]); if (!string.IsNullOrWhiteSpace(path)) lock (valuesLock) if (!paths.Contains(path)) paths.Add(path); } catch (JsonException) { } return; }
            if (line.StartsWith("__VIDEOADER_PROGRESS__:"))
            {
                try
                {
                    var value = JsonNode.Parse(line["__VIDEOADER_PROGRESS__:".Length..])!.AsObject(); var total = value["total_bytes"]?.GetValue<double>() ?? value["total_bytes_estimate"]?.GetValue<double>(); var downloaded = value["downloaded_bytes"]?.GetValue<double>();
                    Event("progress", new() { ["fraction"] = total > 0 && downloaded is not null ? Math.Clamp(downloaded.Value / total.Value, 0, 1) : null, ["message"] = value["status"]?.ToString() == "finished" ? "当前流下载完成，正在处理" : "正在下载", ["speed"] = value["speed"] is null ? "" : $"{value["speed"]} B/s", ["eta"] = value["eta"] is null ? "" : $"{value["eta"]}s" }); return;
                }
                catch (JsonException) { }
            }
            var percent = Regex.Match(line, @"\[download\]\s+(\d+(?:\.\d+)?)%");
            if (percent.Success && double.TryParse(percent.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)) Event("progress", new() { ["fraction"] = number / 100, ["message"] = "正在下载" });
            else Event("log", new() { ["message"] = line });
            lock (valuesLock) if (diagnostic.Length < 65536) diagnostic.AppendLine(line);
        }
        var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
        try { await process.WaitForExitAsync(token); await Task.WhenAll(stdout, stderr); }
        catch { try { process.Kill(true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { } try { await Task.WhenAll(stdout, stderr); } catch { } throw; }
        if (process.ExitCode != 0) errors.Add($"yt-dlp 退出码 {process.ExitCode}：{diagnostic}");
        return process.ExitCode;
    }
}
