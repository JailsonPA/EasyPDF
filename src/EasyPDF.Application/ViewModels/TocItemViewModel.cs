using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.Core.Models;
using System.Collections.ObjectModel;

namespace EasyPDF.Application.ViewModels;

public sealed partial class TocItemViewModel : ObservableObject
{
    public string Title { get; }
    public int PageIndex { get; }
    public int Level { get; }
    public ObservableCollection<TocItemViewModel> Children { get; }

    [ObservableProperty]
    private bool _isExpanded = true;

    public event EventHandler<TocItemViewModel>? NavigateRequested;

    public TocItemViewModel(TocEntry entry)
    {
        Title = entry.Title;
        PageIndex = entry.PageIndex;
        Level = entry.Level;
        Children = new ObservableCollection<TocItemViewModel>(
            entry.Children.Select(c => new TocItemViewModel(c) { NavigateRequested = NavigateRequested }));
    }

    [RelayCommand]
    private void Navigate() => NavigateRequested?.Invoke(this, this);
}
