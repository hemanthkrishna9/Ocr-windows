using IndicOcr.App.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace IndicOcr.App;

/// <summary>
/// Borderless window covering the whole virtual screen, showing a frozen (dimmed) screenshot.
/// The user drags a rectangle; the result is in physical pixels relative to the capture origin.
/// Selection maths uses the ratio (physical width / XAML width), so it is correct whatever
/// DIP scale XAML picked for a window that spans monitors of different DPI.
/// </summary>
public sealed partial class SnipOverlayWindow : Window
{
    private readonly byte[] _backdropJpeg;
    private readonly RectInt32 _bounds;
    private readonly TaskCompletionSource<RectInt32?> _result = new();
    private bool _dragging;
    private bool _everActive;
    private Windows.Foundation.Point _start;

    public SnipOverlayWindow(byte[] backdropJpeg, RectInt32 virtualScreenBounds)
    {
        InitializeComponent();
        _backdropJpeg = backdropJpeg;
        _bounds = virtualScreenBounds;
        Closed += (_, _) => _result.TrySetResult(null);
        Activated += OnActivated;
    }

    /// <summary>Shows the overlay and completes with the selection, or null if cancelled.</summary>
    public async Task<RectInt32?> SelectAsync()
    {
        Backdrop.Source = await ImageHelper.ToBitmapImageAsync(_backdropJpeg);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        Title = "Indic OCR snip";
        AppWindow.IsShownInSwitchers = false;

        Activate();
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, NativeMethods.SWP_SHOWWINDOW);

        return await _result.Task;
    }

    private void Finish(RectInt32? rect)
    {
        if (_result.TrySetResult(rect))
        {
            try { Close(); } catch { /* already closing */ }
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // Lost focus (for example Alt+Tab): cancel so the screen is never left covered.
            if (_everActive) Finish(null);
        }
        else
        {
            _everActive = true;
        }
    }

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        Root.Focus(FocusState.Programmatic);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Finish(null);
        }
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (point.Properties.IsRightButtonPressed)
        {
            Finish(null);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        _dragging = true;
        _start = point.Position;
        Root.CapturePointer(e.Pointer);
        Selection.Visibility = Visibility.Visible;
        UpdateSelection(point.Position);
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        UpdateSelection(e.GetCurrentPoint(Root).Position);
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);

        var end = e.GetCurrentPoint(Root).Position;
        double sx = Root.ActualWidth > 0 ? _bounds.Width / Root.ActualWidth : 1;
        double sy = Root.ActualHeight > 0 ? _bounds.Height / Root.ActualHeight : 1;

        double left = Math.Min(_start.X, end.X) * sx;
        double top = Math.Min(_start.Y, end.Y) * sy;
        double right = Math.Max(_start.X, end.X) * sx;
        double bottom = Math.Max(_start.Y, end.Y) * sy;

        int x = (int)Math.Max(0, Math.Floor(left));
        int y = (int)Math.Max(0, Math.Floor(top));
        int w = (int)Math.Min(_bounds.Width - x, Math.Ceiling(right - left));
        int h = (int)Math.Min(_bounds.Height - y, Math.Ceiling(bottom - top));

        if (w < 8 || h < 8)
        {
            Finish(null); // a click or a tiny drag counts as cancel
            return;
        }
        Finish(new RectInt32(x, y, w, h));
    }

    private void UpdateSelection(Windows.Foundation.Point current)
    {
        double left = Math.Min(_start.X, current.X);
        double top = Math.Min(_start.Y, current.Y);
        Canvas.SetLeft(Selection, left);
        Canvas.SetTop(Selection, top);
        Selection.Width = Math.Abs(current.X - _start.X);
        Selection.Height = Math.Abs(current.Y - _start.Y);
    }
}
