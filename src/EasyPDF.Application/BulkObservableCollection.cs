using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EasyPDF.Application;

/// <summary>
/// An ObservableCollection that replaces its entire contents with a single Reset
/// notification instead of one Add/Remove notification per item.
///
/// Without this, loading a 1000-page PDF called Pages.Add() 1000 times, firing
/// 1000 CollectionChanged events and triggering a WPF layout pass for each one —
/// producing a visible freeze proportional to document length.
/// ReplaceAll() fires exactly 1 Reset notification regardless of page count.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressed;

    /// <summary>
    /// Clears the collection and adds all <paramref name="items"/> in one operation,
    /// raising a single Reset notification at the end.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        _suppressed = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
        }
        finally
        {
            _suppressed = false;
            // Raise the same property notifications ObservableCollection raises after mutations.
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(
                new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressed) base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressed) base.OnPropertyChanged(e);
    }
}
