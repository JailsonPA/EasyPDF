using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.Core.Models;

namespace EasyPDF.Application.ViewModels;

public sealed partial class ThumbnailItemViewModel : ObservableObject
{
    [ObservableProperty]
    private RenderedPage? _renderedPage;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isLoading = true;

    public int PageIndex { get; }
    public int DisplayNumber => PageIndex + 1;

    public event EventHandler<ThumbnailItemViewModel>? NavigateRequested;

    public ThumbnailItemViewModel(int pageIndex)
    {
        PageIndex = pageIndex;
    }

    [RelayCommand]
    private void Navigate() => NavigateRequested?.Invoke(this, this);
}
