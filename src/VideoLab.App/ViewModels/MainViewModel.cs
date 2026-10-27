using VideoLab.Core;

namespace VideoLab.App.ViewModels;

public sealed class MainViewModel : ObservableModel
{
    public Studio Studio { get; }
    public string Title => Studio.Queue.Items.LastOrDefault(i => !i.Finished)?.Summary ?? Studio.Queue.Items.LastOrDefault()?.Summary ?? "准备就绪";
    public string Detail => Studio.Settings.Get("download_path", "所有结果另存为新文件") + $" · {Studio.Queue.Items.Count(i => !i.Finished)} 项等待或处理中";
    public double Progress => Studio.Queue.Items.FirstOrDefault(i => !i.Finished)?.Progress ?? 0;
    public MainViewModel() { Studio = new(Environment.GetEnvironmentVariable("VIDEOLAB_DATA_ROOT")); Studio.Queue.Updated += Refresh; }
    public void Refresh() { Changed(nameof(Title)); Changed(nameof(Detail)); Changed(nameof(Progress)); }
}
