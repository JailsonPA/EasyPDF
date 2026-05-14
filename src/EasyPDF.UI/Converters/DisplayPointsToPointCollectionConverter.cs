using EasyPDF.Core.Models;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EasyPDF.UI.Converters;

[ValueConversion(typeof(IReadOnlyList<DisplayPoint>), typeof(PointCollection))]
public sealed class DisplayPointsToPointCollectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IReadOnlyList<DisplayPoint> pts)
        {
            var col = new PointCollection(pts.Count);
            foreach (var p in pts)
                col.Add(new System.Windows.Point(p.X, p.Y));
            return col;
        }
        return new PointCollection();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
