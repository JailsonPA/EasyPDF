using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.Core.Models;

namespace EasyPDF.Application.ViewModels;

public sealed partial class BookmarkItemViewModel : ObservableObject
{
    private readonly Bookmark _bookmark;

    public Guid Id => _bookmark.Id;
    public int PageIndex => _bookmark.PageIndex;
    public int DisplayNumber => _bookmark.PageIndex + 1;
    public DateTime CreatedAt => _bookmark.CreatedAt;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string? _notes;

    public event EventHandler<BookmarkItemViewModel>? NavigateRequested;
    public event EventHandler<BookmarkItemViewModel>? DeleteRequested;

    public BookmarkItemViewModel(Bookmark bookmark)
    {
        _bookmark = bookmark;
        _title = bookmark.Title;
        _notes = bookmark.Notes;
    }

    [RelayCommand]
    private void Navigate() => NavigateRequested?.Invoke(this, this);

    [RelayCommand]
    private void Delete() => DeleteRequested?.Invoke(this, this);

    public Bookmark ToModel() => _bookmark with { Title = Title, Notes = Notes };
}
