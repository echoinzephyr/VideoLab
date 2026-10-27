namespace VideoLab.Core;

public sealed class LivePreview : IAsyncDisposable
{
    private CancellationTokenSource? current;
    private int revision;
    private readonly HashSet<Task> active = [];
    public void Schedule(Func<string, CancellationToken, Task<string>> render, Action<byte[]?, string?> publish)
    {
        current?.Cancel(); var cancellation = new CancellationTokenSource(); current = cancellation; var version = ++revision;
        var task = RenderAsync(); lock (active) active.Add(task); _ = task.ContinueWith(_ => { lock (active) active.Remove(task); }, TaskScheduler.Default);
        async Task RenderAsync()
        {
            string? directory = null;
            try
            {
                await Task.Delay(400, cancellation.Token); directory = Path.Combine(Path.GetTempPath(), "VideoLab-preview-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                var file = await render(directory, cancellation.Token); var bytes = await File.ReadAllBytesAsync(file, cancellation.Token);
                if (version == revision && !cancellation.IsCancellationRequested) publish(bytes, null);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (version == revision && !cancellation.IsCancellationRequested) publish(null, error.Message); }
            finally
            {
                if (directory is not null && Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()) + "VideoLab-preview-", StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(directory, true); } catch (IOException) { }
                if (ReferenceEquals(current, cancellation)) current = null;
                cancellation.Dispose();
            }
        }
    }
    public void Cancel() { ++revision; current?.Cancel(); current = null; }
    public async ValueTask DisposeAsync() { Cancel(); Task[] tasks; lock (active) tasks = active.ToArray(); await Task.WhenAll(tasks); }
}
