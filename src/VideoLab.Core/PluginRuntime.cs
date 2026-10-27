using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace VideoLab.Core;

internal sealed class ProcessJob : SafeHandleZeroOrMinusOneIsInvalid
{
    private ProcessJob() : base(true) { }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessTime, JobTime; public uint Flags; public nuint Minimum, Maximum; public uint Active; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern ProcessJob CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(ProcessJob job, int informationClass, ref ExtendedLimits information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(ProcessJob job, nint process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    protected override bool ReleaseHandle() => CloseHandle(handle);
    public static ProcessJob Attach(Process process)
    {
        var job = CreateJobObject(0, null); var limits = new ExtendedLimits { Basic = new() { Flags = 0x2000 } };
        if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(job, process.Handle))
        { var code = Marshal.GetLastWin32Error(); job.Dispose(); throw new System.ComponentModel.Win32Exception(code, "无法管理插件子进程。"); }
        return job;
    }
}

public sealed class PluginRuntime : IAsyncDisposable
{
    private readonly Process process;
    private readonly ProcessJob job;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> pending = new();
    private readonly SemaphoreSlim input = new(1, 1);
    private readonly Task reader, errors;
    private long sequence;
    private int closed;
    private Exception? failure;
    public event Action<string, JsonObject>? Event;
    private PluginRuntime(Process process, ProcessJob job)
    {
        this.process = process; this.job = job;
        reader = Task.Run(async () =>
        {
            try
            {
                await ReadLinesAsync(process.StandardOutput, line =>
                {
                    var message = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException("插件消息无效。");
                    if (message["jsonrpc"]?.GetValue<string>() != "2.0") throw new InvalidDataException("插件协议消息无效。");
                    if (message["id"] is JsonValue id && id.TryGetValue<long>(out var number))
                    {
                        if (!pending.TryRemove(number, out var completion)) return;
                        if (message["error"] is not null) completion.TrySetException(new InvalidOperationException(message["error"]?["message"]?.GetValue<string>() ?? "插件任务失败。"));
                        else if (message["result"] is JsonObject result) completion.TrySetResult(result);
                        else completion.TrySetException(new InvalidDataException("插件结果必须是对象。"));
                    }
                    else if (message["method"] is JsonValue method)
                        Event?.Invoke(method.GetValue<string>(), message["params"] as JsonObject ?? new());
                });
                Fail(new IOException("插件已退出。"));
            }
            catch (Exception error) { Fail(error); }
        });
        errors = Task.Run(async () => { try { await ReadLinesAsync(process.StandardError, line => Event?.Invoke("log", new() { ["message"] = line })); } catch (Exception error) { Fail(error); } });
    }
    private static async Task ReadLinesAsync(StreamReader stream, Action<string> receive)
    {
        char[] buffer = new char[4096]; var pending = new StringBuilder(); int count;
        while ((count = await stream.ReadAsync(buffer)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == '\n') { var text = pending.ToString().TrimEnd('\r'); pending.Clear(); if (text.Length > 0) receive(text); }
                else { if (pending.Length >= 1024 * 1024) throw new InvalidDataException("插件响应超过大小限制。"); pending.Append(buffer[i]); }
            }
        }
        if (pending.Length > 0) receive(pending.ToString());
    }
    private void Fail(Exception error)
    {
        if (Interlocked.CompareExchange(ref failure, error, null) is not null) return;
        job.Dispose(); try { if (!process.HasExited) process.Kill(true); } catch (Exception termination) when (termination is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        foreach (var pair in pending) if (pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(error);
    }
    public static async Task<PluginRuntime> StartAsync(string executable, string directory, CancellationToken token)
    {
        var process = new Process { StartInfo = new(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false) } };
        PluginRuntime? runtime = null;
        try
        {
            process.Start(); runtime = new(process, ProcessJob.Attach(process));
            var info = await runtime.RequestAsync("initialize", new() { ["protocolVersion"] = 1, ["hostApiVersion"] = 1 }, token, TimeSpan.FromSeconds(10));
            if (info["protocolVersion"]?.GetValue<int>() != 1 || info["hostApiVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("插件协议不兼容。");
            return runtime;
        }
        catch { if (runtime is not null) await runtime.DisposeAsync(); else { try { process.Kill(true); } catch { } process.Dispose(); } throw; }
    }
    public async Task<JsonObject> RequestAsync(string method, JsonObject values, CancellationToken token, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(closed != 0, this); var id = Interlocked.Increment(ref sequence);
        if (Volatile.Read(ref failure) is Exception error) throw new IOException("插件已停止。", error);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously); pending[id] = completion;
        try
        {
            await input.WaitAsync(token);
            try { await process.StandardInput.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = values.DeepClone() }.ToJsonString()); await process.StandardInput.FlushAsync(token); }
            finally { input.Release(); }
            return await completion.Task.WaitAsync(timeout ?? TimeSpan.FromHours(24), token);
        }
        finally { pending.TryRemove(id, out _); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        Fail(new OperationCanceledException("插件已停止。"));
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
        try { await Task.WhenAll(reader, errors).WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
        process.Dispose(); input.Dispose();
    }
}
