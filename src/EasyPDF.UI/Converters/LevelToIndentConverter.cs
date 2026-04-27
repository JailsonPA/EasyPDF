using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EasyPDF.UI.Converters;

[ValueConversion(typeof(int), typeof(Thickness))]
public sealed class LevelToIndentConverter : IValueConverter
{
    public double IndentPerLevel { get; set; } = 16.0;
    public double BaseLeft { get; set; } = 4.0;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int level = value is int l ? l : 0;
        return new Thickness(BaseLeft + level * IndentPerLevel, 2, 4, 2);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
