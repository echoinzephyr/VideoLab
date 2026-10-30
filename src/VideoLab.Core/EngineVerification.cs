namespace VideoLab.Core;

public enum EngineState { Unverified, Checking, Ready, Failed }

public sealed class EngineVerification(Func<string, CancellationToken, Task<IReadOnlyList<DependencyCheck>>>? inspect = null)
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<DependencyCheck>>> inspect = inspect ?? DependencyChecker.MediaAsync;
    private int revision;
    public EngineState State { get; private set; }
    public IReadOnlyList<DependencyCheck> Results { get; private set; } = [];
    public event Action? Changed;
    public string Summary => State switch
    {
        EngineState.Checking => "正在验证媒体引擎…",
        EngineState.Ready => "媒体引擎已就绪 · " + Results.FirstOrDefault(r => r.Id == "ffmpeg")?.Version,
        EngineState.Failed => "媒体引擎检测未通过，请在设置中查看原因。",
        _ => "媒体引擎尚未验证，请在设置中检测依赖。"
    };
    public string Detail => State is EngineState.Ready or EngineState.Failed ? DependencyChecker.Describe(Results) : Summary;
    public void Invalidate()
    {
        ++revision; Results = []; State = EngineState.Unverified; Changed?.Invoke();
    }
    public async Task VerifyAsync(string path, CancellationToken token = default)
    {
        var current = ++revision;
        Results = []; State = EngineState.Checking; Changed?.Invoke();
        try
        {
            var checks = await inspect(path, token);
            if (current != revision) return;
            Results = checks; State = checks.Count > 0 && checks.All(c => c.State == DependencyState.Ready) ? EngineState.Ready : EngineState.Failed;
            Changed?.Invoke();
        }
        catch
        {
            if (current == revision) Invalidate();
            throw;
        }
    }
}
