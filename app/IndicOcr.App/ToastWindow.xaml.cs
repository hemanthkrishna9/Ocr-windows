using IndicOcr.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace IndicOcr.App;

/// <summary>Small borderless always-on-top notice, shown without taking focus, closed after two seconds.</summary>
public sealed partial class ToastWindow : Window
{
    // Held in a field so the timer is not garbage collected before it fires.
    private DispatcherQueueTimer? _timer;

    public ToastWindow(string message)
    {
        InitializeComponent();
        Message.Text = message;
        Title = "Indic OCR";
    }

    public void ShowBriefly()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        int w = (int)(340 * scale);
        int h = (int)(64 * scale);
        int screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        int x = (screenW - w) / 2;
        int y = screenH - h - (int)(96 * scale);

        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        AppWindow.IsShownInSwitchers = false;

        // Show without activating so the app the user is working in keeps focus.
        try
        {
            AppWindow.Show(false);
        }
        catch
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            _timer = null;
            try { Close(); } catch { /* already closed */ }
        };
        _timer.Start();
    }
}
