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

    [ObservableProperty]
    private double _viewportTopFrac;

    [ObservableProperty]
    private double _viewportHeightFrac = 1.0;

    public int PageIndex { get; }
    public int DisplayNumber => PageIndex + 1;
    public double WidthPt { get; }
    public double HeightPt { get; }

    public event EventHandler<ThumbnailItemViewModel>? NavigateRequested;

    public ThumbnailItemViewModel(int pageIndex, double widthPt, double heightPt)
    {
        PageIndex = pageIndex;
        WidthPt   = widthPt;
        HeightPt  = heightPt;
    }

    [RelayCommand]
    private void Navigate() => NavigateRequested?.Invoke(this, this);
}
