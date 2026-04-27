using System.Globalization;
using System.Windows.Data;

namespace EasyPDF.UI.Converters;

[ValueConversion(typeof(double), typeof(string))]
public sealed class ZoomToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? $"{d * 100:F0}%" : "100%";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s)
        {
            s = s.TrimEnd('%').Trim();
            if (double.TryParse(s, out double pct))
                return pct / 100.0;
        }
        return 1.0;
    }
}
