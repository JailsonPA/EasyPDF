using System.Globalization;
using System.Windows.Data;

namespace EasyPDF.UI.Converters;

/// <summary>
/// Compara o valor enum com o ConverterParameter (string) por nome.
/// Convert: retorna true se value.ToString() == parameter.ToString()
/// ConvertBack: não utilizado (IsChecked é OneWay; o Command altera o estado)
/// </summary>
[ValueConversion(typeof(Enum), typeof(bool))]
public sealed class EnumEqualityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && parameter is not null &&
        value.ToString() == parameter.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
