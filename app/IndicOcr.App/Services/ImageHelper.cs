using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Storage.Streams;

namespace IndicOcr.App.Services;

internal static class ImageHelper
{
    /// <summary>JPEG is much faster for the large screen-snip backdrop.</summary>
    public static byte[] EncodeJpeg(SKBitmap bmp, int quality = 85)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    /// <summary>Must run on the UI thread. decodePixelWidth caps the decoded width to save memory.</summary>
    public static async Task<BitmapImage> ToBitmapImageAsync(byte[] encoded, int decodePixelWidth = 0)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(encoded);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var image = new BitmapImage();
        if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth; // limits decoded size for huge pages
        await image.SetSourceAsync(stream);
        return image;
    }
}
