using EasyPDF.Application;
using EasyPDF.Application.Interfaces;
using EasyPDF.Application.ViewModels;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EasyPDF.Tests.ViewModels;

public sealed class MainViewModelTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    private sealed class Fakes
    {
        public IPdfDocumentService    DocService   { get; } = Substitute.For<IPdfDocumentService>();
        public IRecentFilesRepository RecentRepo   { get; } = Substitute.For<IRecentFilesRepository>();
        public IDialogService         DialogSvc    { get; } = Substitute.For<IDialogService>();
        public IThemeService          ThemeSvc     { get; } = Substitute.For<IThemeService>();
        public IPrintService          PrintSvc     { get; } = Substitute.For<IPrintService>();
        public IUpdateService         UpdateSvc    { get; } = Substitute.For<IUpdateService>();
        public IBookmarkRepository    BookmarkRepo { get; } = Substitute.For<IBookmarkRepository>();
        public ISearchService         SearchSvc    { get; } = Substitute.For<ISearchService>();
        public IPdfRenderService      RenderSvc    { get; } = Substitute.For<IPdfRenderService>();
        public ITextExtractionService TextSvc      { get; } = Substitute.For<ITextExtractionService>();
    }

    private static (MainViewModel vm, Fakes f) Make(AppTheme initialTheme = AppTheme.Dark)
    {
        var f = new Fakes();

        f.ThemeSvc.CurrentTheme.Returns(initialTheme);
        f.RecentRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RecentFile>>([]));
        f.BookmarkRepo.GetAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bookmark>>([]));
        f.UpdateSvc.CheckForUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpdateInfo?>(null));

        var viewer  = new PdfViewerViewModel(f.RenderSvc, f.TextSvc, NullLogger<PdfViewerViewModel>.Instance);
        var sidebar = new SidebarViewModel(f.RenderSvc, f.BookmarkRepo, NullLogger<SidebarViewModel>.Instance);
        var search  = new SearchViewModel(f.SearchSvc, NullLogger<SearchViewModel>.Instance);

        var vm = new MainViewModel(
            f.DocService, f.RecentRepo, f.DialogSvc, f.ThemeSvc, f.PrintSvc, f.UpdateSvc,
            new AppSettings(), viewer, sidebar, search,
            NullLogger<MainViewModel>.Instance);

        return (vm, f);
    }

    private static PdfDocument MakeDoc(int pageCount = 3) =>
        new(FilePath: "/test/doc.pdf",
            FileName: "doc.pdf",
            PageCount: pageCount,
            FileSizeBytes: 1024,
            OpenedAt: DateTime.UtcNow,
            Pages: Enumerable.Range(0, pageCount)
                .Select(i => new PdfPageInfo(i, 595, 842)).ToArray(),
            TableOfContents: []);

    // ─── Initial state ────────────────────────────────────────────────────────

    [Fact]
    public void HasDocument_FalseInitially()
    {
        var (vm, _) = Make();
        Assert.False(vm.HasDocument);
    }

    [Fact]
    public void Title_IsEasyPdfWhenNoDocument()
    {
        var (vm, _) = Make();
        Assert.Equal("EasyPDF", vm.Title);
    }

    // ─── ToggleSearch ─────────────────────────────────────────────────────────

    [Fact]
    public void ToggleSearch_OpensPanelWhenClosed()
    {
        var (vm, _) = Make();
        Assert.False(vm.IsSearchPanelOpen);

        vm.ToggleSearchCommand.Execute(null);

        Assert.True(vm.IsSearchPanelOpen);
    }

    [Fact]
    public void ToggleSearch_ClosesPanelWhenOpen()
    {
        var (vm, _) = Make();
        vm.IsSearchPanelOpen = true;

        vm.ToggleSearchCommand.Execute(null);

        Assert.False(vm.IsSearchPanelOpen);
    }

    // ─── ToggleTheme ──────────────────────────────────────────────────────────

    [Fact]
    public void ToggleTheme_WhenDark_AppliesLightTheme()
    {
        var (vm, f) = Make(AppTheme.Dark);

        vm.ToggleThemeCommand.Execute(null);

        f.ThemeSvc.Received(1).ApplyTheme(AppTheme.Light);
    }

    [Fact]
    public void ToggleTheme_WhenLight_AppliesHighContrastTheme()
    {
        var (vm, f) = Make(AppTheme.Light);

        vm.ToggleThemeCommand.Execute(null);

        f.ThemeSvc.Received(1).ApplyTheme(AppTheme.HighContrast);
    }

    [Fact]
    public void ToggleTheme_WhenHighContrast_AppliesDarkTheme()
    {
        var (vm, f) = Make(AppTheme.HighContrast);

        vm.ToggleThemeCommand.Execute(null);

        f.ThemeSvc.Received(1).ApplyTheme(AppTheme.Dark);
    }

    // ─── OpenFile ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenFile_WhenDialogCancelled_DoesNotLoadDocument()
    {
        var (vm, f) = Make();
        f.DialogSvc.OpenPdfFileAsync().Returns(Task.FromResult<string?>(null));

        await vm.OpenFileCommand.ExecuteAsync(null);

        Assert.False(vm.HasDocument);
        await f.DocService.DidNotReceive().OpenAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ─── DropFile ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DropFile_NonPdfExtension_ShowsError()
    {
        var (vm, f) = Make();

        await vm.DropFileAsync("document.docx");

        await f.DialogSvc.Received(1).ShowErrorAsync(Arg.Any<string>(), Arg.Any<string>());
        Assert.False(vm.HasDocument);
    }

    [Fact]
    public async Task DropFile_FileNotFound_ShowsError()
    {
        var (vm, f) = Make();

        // Path ends with .pdf but does not exist on disk.
        await vm.DropFileAsync(@"C:\no_such_file_xyz_easypdf_test.pdf");

        await f.DialogSvc.Received(1).ShowErrorAsync(Arg.Any<string>(), Arg.Any<string>());
        Assert.False(vm.HasDocument);
    }

    // ─── LoadDocument ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadDocument_SetsHasDocumentAndTitle()
    {
        var (vm, f) = Make();
        string tmp = Path.ChangeExtension(Path.GetTempFileName(), ".pdf");
        File.WriteAllBytes(tmp, []);
        try
        {
            var doc = new PdfDocument(
                FilePath: tmp,
                FileName: Path.GetFileName(tmp),
                PageCount: 2,
                FileSizeBytes: 0,
                OpenedAt: DateTime.UtcNow,
                Pages: [new PdfPageInfo(0, 595, 842), new PdfPageInfo(1, 595, 842)],
                TableOfContents: []);

            f.DocService
                .OpenAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(doc));

            await vm.DropFileAsync(tmp);

            Assert.True(vm.HasDocument);
            Assert.Contains(Path.GetFileName(tmp), vm.Title);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ─── CloseDocument ────────────────────────────────────────────────────────

    [Fact]
    public async Task CloseDocument_ClearsHasDocument()
    {
        var (vm, _) = Make();
        // Bypass full load — set document directly to test close logic in isolation.
        vm.CurrentDocument = MakeDoc();
        Assert.True(vm.HasDocument);

        await vm.CloseDocumentCommand.ExecuteAsync(null);

        Assert.False(vm.HasDocument);
    }

    [Fact]
    public async Task CloseDocument_ClearsViewerPages()
    {
        var (vm, _) = Make();
        vm.CurrentDocument = MakeDoc(5);
        vm.Viewer.LoadDocument(MakeDoc(5));
        Assert.Equal(5, vm.Viewer.Pages.Count);

        await vm.CloseDocumentCommand.ExecuteAsync(null);

        Assert.Empty(vm.Viewer.Pages);
    }

    // ─── AddBookmark ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AddBookmark_WhenNoDocument_DoesNothing()
    {
        var (vm, f) = Make();
        Assert.False(vm.HasDocument);

        await vm.AddBookmarkCommand.ExecuteAsync(null);

        await f.DialogSvc.DidNotReceive().PromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // ─── Update banner ────────────────────────────────────────────────────────

    [Fact]
    public void UpdateAvailable_FalseWhenNoPendingUpdate()
    {
        var (vm, _) = Make();
        Assert.Null(vm.PendingUpdate);
        Assert.False(vm.UpdateAvailable);
    }

    [Fact]
    public void UpdateAvailable_TrueWhenPendingUpdateSet()
    {
        var (vm, _) = Make();
        vm.PendingUpdate = new UpdateInfo("2.0.0", "https://example.com/releases/v2.0.0");
        Assert.True(vm.UpdateAvailable);
    }

    [Fact]
    public void DismissUpdate_ClearsPendingUpdate()
    {
        var (vm, _) = Make();
        vm.PendingUpdate = new UpdateInfo("2.0.0", "https://example.com/releases/v2.0.0");

        vm.DismissUpdateCommand.Execute(null);

        Assert.Null(vm.PendingUpdate);
        Assert.False(vm.UpdateAvailable);
    }
}
