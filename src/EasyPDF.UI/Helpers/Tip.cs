using System.Windows;

namespace EasyPDF.UI.Helpers;

/// <summary>
/// Attached properties that build a <see cref="RichToolTip"/> and assign it to
/// the element's ToolTip — use Tip.Title / Tip.Keys instead of plain ToolTip="…".
/// </summary>
public static class Tip
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.RegisterAttached("Title", typeof(string), typeof(Tip),
            new PropertyMetadata(null, Rebuild));

    public static readonly DependencyProperty KeysProperty =
        DependencyProperty.RegisterAttached("Keys", typeof(string), typeof(Tip),
            new PropertyMetadata(null, Rebuild));

    public static string? GetTitle(DependencyObject d) => (string?)d.GetValue(TitleProperty);
    public static void SetTitle(DependencyObject d, string? v) => d.SetValue(TitleProperty, v);

    public static string? GetKeys(DependencyObject d) => (string?)d.GetValue(KeysProperty);
    public static void SetKeys(DependencyObject d, string? v) => d.SetValue(KeysProperty, v);

    private static void Rebuild(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        string? title = GetTitle(fe);
        fe.ToolTip = string.IsNullOrEmpty(title)
            ? null
            : new RichToolTip { Title = title!, Shortcut = GetKeys(fe) };
    }
}
