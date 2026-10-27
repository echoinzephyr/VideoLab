using Microsoft.UI.Xaml;

namespace VideoLab.App;

public partial class App : Application
{
    private MainWindow? window;
    public App() { InitializeComponent(); UnhandledException += (_, e) => { window?.ShowError(e.Exception.Message); e.Handled = true; }; }
    protected override void OnLaunched(LaunchActivatedEventArgs args) { window = new MainWindow(); window.Activate(); _ = window.InitializeAsync(); }
}
