using System.Diagnostics;
using System.Text.Json.Nodes;

if (args.FirstOrDefault() == "--child") { await Task.Delay(1000); await File.WriteAllTextAsync(args[1], "child survived"); return; }
if (args.FirstOrDefault() is "--version" or "-version") { Console.WriteLine("VideoLab fixture 1.0"); return; }
if (args.FirstOrDefault() == "--fixture-overlong")
{
    while (true) { Console.Write(new string('x', 8192)); Console.Out.Flush(); }
}
if (args.FirstOrDefault() == "--fixture-output")
{
    var progress = args.ElementAtOrDefault(2) switch
    {
        "estimated" => "{\"downloaded_bytes\":5,\"total_bytes\":\"NA\",\"total_bytes_estimate\":20,\"speed\":\"NA\",\"eta\":\"NA\"}",
        "unknown" => "{\"downloaded_bytes\":\"NA\",\"total_bytes\":\"NA\",\"total_bytes_estimate\":\"NA\",\"speed\":\"NA\",\"eta\":\"NA\"}",
        "zero" => "{\"downloaded_bytes\":5,\"total_bytes\":0,\"total_bytes_estimate\":20,\"speed\":0,\"eta\":0}",
        "null" => "{\"downloaded_bytes\":5,\"total_bytes\":null,\"total_bytes_estimate\":null}",
        "negative" => "{\"downloaded_bytes\":-5,\"total_bytes\":10,\"speed\":-1,\"eta\":-1}",
        "overflow" => "{\"downloaded_bytes\":5,\"total_bytes\":1e999}",
        _ => "{\"status\":\"downloading\",\"downloaded_bytes\":5,\"total_bytes\":10,\"speed\":1024,\"eta\":1}"
    };
    if (args.ElementAtOrDefault(2) == "malformed")
        foreach (var invalid in new[] { "broken", "[]", "null" }) Console.WriteLine("__VIDEOADER_PROGRESS__:" + invalid);
    Console.WriteLine("__VIDEOADER_PROGRESS__:" + progress);
    // Exceed pipe capacity so a stopped reader cannot hide behind a short fixture.
    for (int i = 0; i < 256; i++) Console.WriteLine(new string('x', 1024));
    Console.WriteLine("fixture log after progress");
    await File.WriteAllBytesAsync(args[1], [1, 2, 3, 4]); Console.WriteLine("__VIDEOADER_OUTPUT__:" + System.Text.Json.JsonSerializer.Serialize(args[1])); return;
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
