using EasyPDF.Core.Models;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EasyPDF.UI.Converters;


/// Converts a <see cref="RenderedPage"/> (raw BGRA bytes) to a <see cref="BitmapSource"/>
[ValueConversion(typeof(RenderedPage), typeof(ImageSource))]
public sealed class RenderedPageToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not RenderedPage page) return null;

        var format = page.BitsPerPixel == 32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24;

        double dpi = 96.0 * page.DpiScale;
        var bitmap = BitmapSource.Create(
            page.Width, page.Height,
            dpi, dpi,
            format,
            null,
            page.PixelData,
            page.Stride);
       
        bitmap.Freeze();
        return bitmap;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
