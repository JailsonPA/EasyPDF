using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EasyPDF.UI.Converters;

[ValueConversion(typeof(string), typeof(Color))]
public sealed class HexToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { }
        }
        return Colors.DodgerBlue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
