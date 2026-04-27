using EasyPDF.Application.ViewModels;
using Microsoft.Xaml.Behaviors;
using System.Windows;

namespace EasyPDF.UI.Behaviors;

/// <summary>
/// Handles PDF file drag-and-drop onto any UIElement that has a MainViewModel in scope.
/// </summary>
public sealed class FileDragDropBehavior : Behavior<UIElement>
{
    protected override void OnAttached()
    {
        AssociatedObject.AllowDrop = true;
        AssociatedObject.DragOver += OnDragOver;
        AssociatedObject.Drop += OnDrop;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.DragOver -= OnDragOver;
        AssociatedObject.Drop -= OnDrop;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private static async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;

        if ((sender as FrameworkElement)?.DataContext is MainViewModel vm)
            await vm.DropFileAsync(files[0]);
    }
}
