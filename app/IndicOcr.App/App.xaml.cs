using Microsoft.UI.Xaml;

namespace IndicOcr.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Log only the exception type and stack trace. Never the message, which could
        // in theory carry document content.
        AppLog.Error("Unhandled exception", e.Exception);
        e.Handled = true;
    }
}
