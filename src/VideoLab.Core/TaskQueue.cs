using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace VideoLab.Core;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public record WorkResult(IReadOnlyList<string> Paths, IReadOnlyList<string> Errors, bool Cancelled = false)
{
    public static WorkResult File(string path) => new([path], []);
}
public sealed class WorkItem : ObservableModel
{
    public string Name { get; }
    public string Input { get; }
    public CancellationTokenSource Cancellation { get; } = new();
    public string Status { get; private set; } = "等待";
    public double Progress { get; private set; }
    public bool Finished { get; private set; }
    public IReadOnlyList<string> Paths { get; private set; } = [];
    public IReadOnlyList<string> Errors { get; private set; } = [];
    public TimeSpan Elapsed { get; private set; }
    public long Bytes { get; private set; }
    public string Summary => $"{Name} · {Status} · {Bytes / 1024d:N1} KB · {Elapsed.TotalSeconds:N1}s" + (Errors.Count == 0 ? "" : $" · {string.Join("；", Errors)}");
    public WorkItem(string name, string input) { Name = name; Input = input; }
    public void Report(string status, double? progress = null) { Status = status; if (progress is not null) Progress = Math.Clamp(progress.Value, 0, 1); Changed(nameof(Summary)); Changed(nameof(Progress)); }
    public void Complete(WorkResult result, TimeSpan elapsed)
    {
        Paths = result.Paths; Errors = result.Errors; Elapsed = elapsed;
        Bytes = Paths.Sum(p => { try { return new FileInfo(p).Length; } catch { return 0L; } });
        Finished = true; Report(result.Cancelled ? "已取消" : Errors.Count > 0 ? (Paths.Count > 0 ? "部分完成" : "失败") : "完成", 1);
        Changed(nameof(Paths)); Changed(nameof(Finished));
    }
}
public sealed class TaskQueue
{
    private readonly SemaphoreSlim queue = new(1, 1);
    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    public ObservableCollection<WorkItem> Items { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public event Action? Updated;
    public bool Busy => Items.Any(i => !i.Finished);
    public void Dispatch(Action action) { if (context is not null && context != SynchronizationContext.Current) context.Post(_ => action(), null); else action(); }
    public void Log(string line) => Dispatch(() => { Logs.Add(line.Length > 16384 ? line[..16384] : line); while (Logs.Count > 500) Logs.RemoveAt(0); Updated?.Invoke(); });
    public Task<WorkItem> AddAsync(string name, string input, Func<WorkItem, CancellationToken, Task<WorkResult>> execute)
    {
        var item = new WorkItem(name, input); Dispatch(() => { Items.Add(item); Updated?.Invoke(); });
        return RunAsync(item, execute);
    }
    private async Task<WorkItem> RunAsync(WorkItem item, Func<WorkItem, CancellationToken, Task<WorkResult>> execute)
    {
        var watch = new Stopwatch(); bool acquired = false;
        try
        {
            await queue.WaitAsync(item.Cancellation.Token); acquired = true; watch.Start();
            item.Cancellation.Token.ThrowIfCancellationRequested(); Dispatch(() => item.Report("处理中"));
            var result = await execute(item, item.Cancellation.Token);
            if (item.Cancellation.IsCancellationRequested) result = result with { Cancelled = true };
            Dispatch(() => item.Complete(result, watch.Elapsed));
        }
        catch (OperationCanceledException) { Dispatch(() => item.Complete(new(item.Paths, [], true), watch.Elapsed)); }
        catch (Exception error)
        { Log(error.Message); Dispatch(() => item.Complete(new(item.Paths, [error.Message], item.Cancellation.IsCancellationRequested), watch.Elapsed)); }
        finally { if (acquired) queue.Release(); Dispatch(() => Updated?.Invoke()); }
        return item;
    }
    public void CancelAll() { foreach (var item in Items.Where(i => !i.Finished)) item.Cancellation.Cancel(); }
}
