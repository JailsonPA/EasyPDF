using EasyPDF.Application.ViewModels;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EasyPDF.Tests.ViewModels;

public sealed class SidebarViewModelTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static SidebarViewModel Make(IBookmarkRepository? repo = null)
    {
        var bookmarkRepo = repo ?? DefaultRepo();
        return new SidebarViewModel(
            Substitute.For<IPdfRenderService>(),
            bookmarkRepo,
            NullLogger<SidebarViewModel>.Instance);
    }

    private static IBookmarkRepository DefaultRepo()
    {
        var repo = Substitute.For<IBookmarkRepository>();
        repo.GetAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bookmark>>([]));
        return repo;
    }

    private static PdfDocument MakeDoc(int pageCount = 3, int tocCount = 0) =>
        new(FilePath: "/test/doc.pdf",
            FileName: "doc.pdf",
            PageCount: pageCount,
            FileSizeBytes: 1024,
            OpenedAt: DateTime.UtcNow,
            Pages: Enumerable.Range(0, pageCount)
                .Select(i => new PdfPageInfo(i, 595, 842)).ToArray(),
            TableOfContents: Enumerable.Range(0, tocCount)
                .Select(i => new TocEntry($"Chapter {i + 1}", i, 0, [])).ToArray());

    // ─── LoadDocumentAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task LoadDocumentAsync_CreatesThumbnailForEachPage()
    {
        var vm = Make();
        await vm.LoadDocumentAsync(MakeDoc(5));
        Assert.Equal(5, vm.Thumbnails.Count);
    }

    [Fact]
    public async Task LoadDocumentAsync_CreatesTocItemForEachTopLevelEntry()
    {
        var vm = Make();
        await vm.LoadDocumentAsync(MakeDoc(tocCount: 4));
        Assert.Equal(4, vm.TocItems.Count);
    }

    [Fact]
    public async Task LoadDocumentAsync_LoadsBookmarksFromRepository()
    {
        var repo = Substitute.For<IBookmarkRepository>();
        repo.GetAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bookmark>>(
            [
                new Bookmark(Guid.NewGuid(), "/test/doc.pdf", 0, "Intro",   DateTime.UtcNow),
                new Bookmark(Guid.NewGuid(), "/test/doc.pdf", 2, "Summary", DateTime.UtcNow),
            ]));

        var vm = Make(repo);
        await vm.LoadDocumentAsync(MakeDoc());

        Assert.Equal(2, vm.Bookmarks.Count);
    }

    [Fact]
    public async Task LoadDocumentAsync_ReplacesThumbsOnSecondLoad()
    {
        var vm = Make();
        await vm.LoadDocumentAsync(MakeDoc(10));
        await vm.LoadDocumentAsync(MakeDoc(3));
        Assert.Equal(3, vm.Thumbnails.Count);
    }

    // ─── SetSelectedPage ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetSelectedPage_SetsIsSelectedOnTargetThumbnail()
    {
        var vm = Make();
        await vm.LoadDocumentAsync(MakeDoc(3));

        vm.SetSelectedPage(1);

        Assert.True(vm.Thumbnails[1].IsSelected);
    }

    [Fact]
    public async Task SetSelectedPage_ClearsPreviousSelection()
    {
        var vm = Make();
        await vm.LoadDocumentAsync(MakeDoc(3));
        vm.SetSelectedPage(0);

        vm.SetSelectedPage(2);

        Assert.False(vm.Thumbnails[0].IsSelected);
        Assert.True(vm.Thumbnails[2].IsSelected);
    }

    [Fact]
    public async Task SetSelectedPage_UnknownIndex_DoesNotThrow()
    {
        var vm = Make();
        await vm.LoadDocumentAsync(MakeDoc(3));

        var ex = Record.Exception(() => vm.SetSelectedPage(99));
        Assert.Null(ex);
    }

    // ─── AddBookmarkAsync / Delete ────────────────────────────────────────────

    [Fact]
    public async Task AddBookmarkAsync_AddsToCollection()
    {
        var vm = Make();
        await vm.AddBookmarkAsync("/test/doc.pdf", 1, "My Mark");

        Assert.Single(vm.Bookmarks);
        Assert.Equal("My Mark", vm.Bookmarks[0].Title);
        Assert.Equal(1, vm.Bookmarks[0].PageIndex);
    }

    [Fact]
    public async Task DeleteRequested_RemovesBookmarkFromCollection()
    {
        var repo = Substitute.For<IBookmarkRepository>();
        repo.GetAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bookmark>>(
            [
                new Bookmark(Guid.NewGuid(), "/test/doc.pdf", 0, "First",  DateTime.UtcNow),
                new Bookmark(Guid.NewGuid(), "/test/doc.pdf", 1, "Second", DateTime.UtcNow),
            ]));

        var vm = Make(repo);
        await vm.LoadDocumentAsync(MakeDoc());

        vm.Bookmarks[0].DeleteCommand.Execute(null);
        await Task.Delay(100); // async void handler — wait for remove to settle

        Assert.Single(vm.Bookmarks);
        Assert.Equal("Second", vm.Bookmarks[0].Title);
    }

    // ─── Clear ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_EmptiesAllCollections()
    {
        var repo = Substitute.For<IBookmarkRepository>();
        repo.GetAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bookmark>>(
                [new Bookmark(Guid.NewGuid(), "/test/doc.pdf", 0, "B", DateTime.UtcNow)]));

        var vm = Make(repo);
        await vm.LoadDocumentAsync(MakeDoc(3, tocCount: 2));

        vm.Clear();

        Assert.Empty(vm.Thumbnails);
        Assert.Empty(vm.TocItems);
        Assert.Empty(vm.Bookmarks);
    }

    // ─── Commands ─────────────────────────────────────────────────────────────

    [Fact]
    public void ToggleVisibility_TogglesIsVisible()
    {
        var vm = Make();
        Assert.True(vm.IsVisible);

        vm.ToggleVisibilityCommand.Execute(null);
        Assert.False(vm.IsVisible);

        vm.ToggleVisibilityCommand.Execute(null);
        Assert.True(vm.IsVisible);
    }

    [Fact]
    public void SetTab_ChangesActiveTab()
    {
        var vm = Make();
        Assert.Equal(SidebarTab.Thumbnails, vm.ActiveTab);

        vm.SetTabCommand.Execute(SidebarTab.Bookmarks);
        Assert.Equal(SidebarTab.Bookmarks, vm.ActiveTab);

        vm.SetTabCommand.Execute(SidebarTab.TableOfContents);
        Assert.Equal(SidebarTab.TableOfContents, vm.ActiveTab);
    }

    // ─── Navigation events ────────────────────────────────────────────────────

    [Fact]
    public async Task ThumbnailNavigate_FiresPageNavigationRequested()
    {
        var vm = Make();
        await vm.LoadDocumentAsync(MakeDoc(3));

        int? firedPage = null;
        vm.PageNavigationRequested += (_, page) => firedPage = page;

        vm.Thumbnails[2].NavigateCommand.Execute(null);

        Assert.Equal(2, firedPage);
    }

    [Fact]
    public async Task TocItemNavigate_FiresPageNavigationRequested()
    {
        var vm = Make();
        await vm.LoadDocumentAsync(MakeDoc(3, tocCount: 3));

        int? firedPage = null;
        vm.PageNavigationRequested += (_, page) => firedPage = page;

        vm.TocItems[1].NavigateCommand.Execute(null); // entry at index 1 has PageIndex == 1

        Assert.Equal(1, firedPage);
    }

    [Fact]
    public async Task BookmarkNavigate_FiresPageNavigationRequested()
    {
        var vm = Make();
        await vm.AddBookmarkAsync("/test/doc.pdf", 3, "Chapter");

        int? firedPage = null;
        vm.PageNavigationRequested += (_, page) => firedPage = page;

        vm.Bookmarks[0].NavigateCommand.Execute(null);

        Assert.Equal(3, firedPage);
    }
}
