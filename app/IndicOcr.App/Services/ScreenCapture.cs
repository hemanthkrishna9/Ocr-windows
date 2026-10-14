using System.ComponentModel;
using System.Runtime.InteropServices;
using SkiaSharp;
using Windows.Graphics;

namespace IndicOcr.App.Services;

internal static class ScreenCapture
{
    /// <summary>
    /// Captures the whole virtual screen (all monitors) in physical pixels. The process is
    /// per-monitor-v2 DPI aware (app.manifest), so GetSystemMetrics and BitBlt use physical pixels.
    /// Caller owns the returned bitmap.
    /// </summary>
    public static SKBitmap CaptureVirtualScreen(out RectInt32 bounds)
    {
        int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("No screen area to capture.");
        bounds = new RectInt32(x, y, w, h);

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr memDc = IntPtr.Zero;
        IntPtr hbmp = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;
        SKBitmap? bmp = null;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            hbmp = NativeMethods.CreateCompatibleBitmap(screenDc, w, h);
            if (memDc == IntPtr.Zero || hbmp == IntPtr.Zero)
                throw new InvalidOperationException("Could not allocate the capture surface.");
            oldObj = NativeMethods.SelectObject(memDc, hbmp);

            if (!NativeMethods.BitBlt(memDc, 0, 0, w, h, screenDc, x, y,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // Deselect before GetDIBits (required: the bitmap must not be selected into a DC).
            NativeMethods.SelectObject(memDc, oldObj);
            oldObj = IntPtr.Zero;

            bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // negative = top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            };
            int lines = NativeMethods.GetDIBits(screenDc, hbmp, 0, (uint)h, bmp.GetPixels(), ref header,
                NativeMethods.DIB_RGB_COLORS);
            if (lines == 0) throw new InvalidOperationException("GetDIBits failed.");

            var result = bmp;
            bmp = null;
            return result;
        }
        finally
        {
            bmp?.Dispose();
            if (oldObj != IntPtr.Zero) NativeMethods.SelectObject(memDc, oldObj);
            if (hbmp != IntPtr.Zero) NativeMethods.DeleteObject(hbmp);
            if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Copies a sub-rectangle (clamped to the bitmap) into a new bitmap the caller owns.</summary>
    public static SKBitmap Crop(SKBitmap src, RectInt32 r)
    {
        int x = Math.Clamp(r.X, 0, src.Width - 1);
        int y = Math.Clamp(r.Y, 0, src.Height - 1);
        int w = Math.Clamp(r.Width, 1, src.Width - x);
        int h = Math.Clamp(r.Height, 1, src.Height - y);
        using var subset = new SKBitmap();
        if (!src.ExtractSubset(subset, new SKRectI(x, y, x + w, y + h)))
            throw new InvalidOperationException("Could not crop the capture.");
        return subset.Copy();
    }

    /// <summary>
    /// Small on-screen text reads better at 2x. Returns a new 2x bitmap (caller owns it) when the crop is
    /// shorter than minHeight, otherwise null. Width and height are capped so the result stays reasonable.
    /// </summary>
    public static SKBitmap? UpscaleIfSmall(SKBitmap src, int minHeight = 400)
    {
        if (src.Height >= minHeight) return null;
        long w = (long)src.Width * 2, h = (long)src.Height * 2;
        if (w > 8000 || h > 8000) return null;
        var info = new SKImageInfo((int)w, (int)h, SKColorType.Bgra8888, SKAlphaType.Premul);
        return src.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }
}
