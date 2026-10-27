using System.Diagnostics;
using System.Text.Json.Nodes;

if (args.FirstOrDefault() == "--child") { await Task.Delay(1000); await File.WriteAllTextAsync(args[1], "child survived"); return; }
if (args.FirstOrDefault() is "--version" or "-version") { Console.WriteLine("VideoLab fixture 1.0"); return; }
if (args.FirstOrDefault() == "--fixture-output")
{
    await File.WriteAllBytesAsync(args[1], [1, 2, 3, 4]); Console.WriteLine("__VIDEOADER_PROGRESS__:{\"status\":\"downloading\",\"downloaded_bytes\":5,\"total_bytes\":10,\"speed\":1024,\"eta\":1}"); Console.WriteLine("__VIDEOADER_OUTPUT__:" + System.Text.Json.JsonSerializer.Serialize(args[1])); return;
}
JsonNode? pendingExecute = null;
while (await Console.In.ReadLineAsync() is string line)
{
    var request = JsonNode.Parse(line)!.AsObject(); var method = request["method"]!.GetValue<string>(); var values = request["params"]!.AsObject();
    JsonObject result = new();
    if (method == "initialize") result = new() { ["protocolVersion"] = 1, ["hostApiVersion"] = 1 };
    else if (method == "execute")
    {
        var input = values["inputs"] as JsonObject ?? values;
        if (input["mode"]?.ToString() == "tree")
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true }; start.ArgumentList.Add("--child"); start.ArgumentList.Add(input["marker"]!.GetValue<string>()); var child = Process.Start(start)!;
            Console.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "started", ["params"] = new JsonObject { ["pid"] = child.Id } }.ToJsonString()); Console.Out.Flush(); continue;
        }
        if (input["mode"]?.ToString() == "hang") continue;
        if (input["mode"]?.ToString() == "partial")
        {
            await File.WriteAllTextAsync(Path.Combine(values["outputDirectory"]!.GetValue<string>(), "finished.txt"), "completed"); pendingExecute = request["id"]!.DeepClone();
            Console.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "progress", ["params"] = new JsonObject { ["fraction"] = .5 } }.ToJsonString()); Console.Out.Flush(); continue;
        }
        if (input["mode"]?.ToString() == "malformed") { Console.WriteLine("invalid JSON"); continue; }
        result = new() { ["paths"] = new JsonArray("result.txt"), ["errors"] = new JsonArray(), ["request"] = values.DeepClone() };
    }
    else if (method == "cancel" && pendingExecute is not null)
    {
        Console.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = pendingExecute.DeepClone(), ["result"] = new JsonObject { ["paths"] = new JsonArray("finished.txt"), ["cancelled"] = false } }.ToJsonString()); pendingExecute = null;
    }
    Console.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result }.ToJsonString()); Console.Out.Flush();
}
