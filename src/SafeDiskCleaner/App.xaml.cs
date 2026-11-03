using Microsoft.UI.Xaml;
using SafeDiskCleaner.Core;

namespace SafeDiskCleaner;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            RunLog.WriteError(e.Exception.ToString());
            e.SetObserved();
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Log it, tell the user, keep the window: nothing is deleted outside the guarded clean path.
        RunLog.WriteError(e.Exception.ToString());
        if (_window is not null)
        {
            e.Handled = true;
            _window.ShowError("Something went wrong", e.Exception.Message + " Details are in error.log next to runs.csv.");
        }
    }
}
