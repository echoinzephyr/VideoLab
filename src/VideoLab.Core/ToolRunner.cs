using System.Diagnostics;

namespace VideoLab.Core;

public static class ToolRunner
{
    public static string Resolve(string path, string tool)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Trim();
        if (!Directory.Exists(path))
        {
            if (path.Length == 0) path = tool + ".exe";
            if (File.Exists(path)) return Path.GetFullPath(path);
            if (Path.IsPathRooted(path) || path.Contains(Path.DirectorySeparatorChar) || path.Contains(Path.AltDirectorySeparatorChar)) return path;
            foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
                if (!Path.IsPathRooted(directory)) continue;
                foreach (var name in Path.HasExtension(path) ? new[] { path } : new[] { path + ".exe", path })
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
            return path;
        }
        var direct = Path.Combine(path, tool + ".exe");
        return Path.GetFullPath(File.Exists(direct) ? direct : Path.Combine(path, "bin", tool + ".exe"));
    }
    public static string Ffprobe(string ffmpeg) => Path.Combine(Path.GetDirectoryName(Resolve(ffmpeg, "ffmpeg")) ?? "", "ffprobe.exe");
    public static async Task<(int ExitCode, string Text)> RunAsync(string executable, IEnumerable<string> args, CancellationToken token = default, int timeoutSeconds = 15)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        process.Start();
        ProcessJob? attached = null;
        try { if (OperatingSystem.IsWindows()) attached = ProcessJob.Attach(process); }
        catch { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        using var processJob = attached;
        using var stop = budget.Token.Register(() => { processJob?.Dispose(); try { process.Kill(true); } catch (InvalidOperationException) { } });
        var stdout = ReadBoundedAsync(process.StandardOutput, budget.Token); var stderr = ReadBoundedAsync(process.StandardError, budget.Token);
        try { await process.WaitForExitAsync(budget.Token); return (process.ExitCode, (await stdout) + (await stderr)); }
        catch
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr); } catch { }
            throw;
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new System.Text.StringBuilder(); char[] buffer = new char[4096];
        int count; while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0) if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
        return text.ToString();
    }
    public static string NewOutput(string directory, string input, string operation, string extension)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("请选择存在的输出文件夹。");
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(input)}_{operation}_{DateTime.Now:yyyyMMdd_HHmmss_fffffff}.{extension.TrimStart('.')}");
    }
}
