using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace VideoLab.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                new App();
            });
        }
        catch (Exception error)
        {
            var root = Environment.GetEnvironmentVariable("VIDEOLAB_DATA_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoLab", "WinUI3");
            Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "startup-error.log"), error.ToString());
            if (!Environment.GetCommandLineArgs().Contains("--smoke-test")) MessageBox(0, "VideoLab 无法启动：\n" + error.Message + "\n\n请保留完整发布目录。", "VideoLab", 0x10);
            Environment.ExitCode = 1;
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(nint window, string text, string caption, uint type);
}
