using EasyPDF.Application.Interfaces;
using Microsoft.Xaml.Behaviors;
using System.Windows;

namespace EasyPDF.UI.Behaviors;

/// <summary>
/// Handles PDF file drag-and-drop onto any UIElement whose DataContext implements IFileDropTarget.
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

        if ((sender as FrameworkElement)?.DataContext is not IFileDropTarget target)
            return;

        // async void is required for WPF event handlers. Without try/catch, any exception
        // thrown after the first await would bypass all callers and surface as an unhandled
        // dispatcher exception. DropFileAsync handles expected errors internally; this guard
        // catches the unexpected ones so the user gets a clear message rather than a crash dialog.
        try
        {
            await target.DropFileAsync(files[0]);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "EasyPDF — Drag & Drop Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
