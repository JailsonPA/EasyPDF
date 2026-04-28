using System.Globalization;
using System.Windows.Data;

namespace EasyPDF.UI.Converters;

/// <summary>
/// Converts a 0-based int index to a 1-based string for display in page-number inputs,
/// and converts back from a 1-based string to a 0-based int on LostFocus.
/// </summary>
[ValueConversion(typeof(int), typeof(string))]
public sealed class OneBasedIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i ? (i + 1).ToString() : "1";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && int.TryParse(s.Trim(), out int page))
            return Math.Max(0, page - 1);
        return 0;
    }
}
