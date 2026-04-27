using EasyPDF.Core.Models;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EasyPDF.UI.Converters;

/// <summary>
/// Converts a <see cref="RenderedPage"/> (raw BGRA bytes) to a <see cref="BitmapSource"/>
/// suitable for WPF Image controls. Conversion happens on the calling thread.
/// </summary>
[ValueConversion(typeof(RenderedPage), typeof(ImageSource))]
public sealed class RenderedPageToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not RenderedPage page) return null;

        var format = page.BitsPerPixel == 32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24;
        // dpiX/dpiY tell WPF the physical pixel density of this bitmap.
        // When DpiScale > 1 (e.g. 1.5 on a 150% display), the bitmap was rendered
        // with proportionally more pixels, so WPF displays it at the correct logical
        // size without any upscaling — eliminating the blurriness on high-DPI screens.
        double dpi = 96.0 * page.DpiScale;
        var bitmap = BitmapSource.Create(
            page.Width, page.Height,
            dpi, dpi,
            format,
            null,
            page.PixelData,
            page.Stride);
        // Freeze makes the bitmap immutable and thread-safe:
        // WPF can cache it in GPU texture memory, the GC can collect it from
        // any thread, and re-binding the same RenderedPage skips re-creation.
        bitmap.Freeze();
        return bitmap;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
