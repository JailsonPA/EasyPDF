using EasyPDF.Application.ViewModels;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EasyPDF.Tests.ViewModels;

public sealed class PdfViewerViewModelTests
{
    private static PdfViewerViewModel Make() =>
        new(Substitute.For<IPdfRenderService>(), Substitute.For<ITextExtractionService>(), NullLogger<PdfViewerViewModel>.Instance);

    private static PdfDocument MakeDoc(int pageCount, double widthPt = 595, double heightPt = 842) =>
        new(FilePath: "test.pdf",
            FileName: "test.pdf",
            PageCount: pageCount,
            FileSizeBytes: 1024,
            OpenedAt: DateTime.UtcNow,
            Pages: Enumerable.Range(0, pageCount)
                .Select(i => new PdfPageInfo(i, widthPt, heightPt))
                .ToArray(),
            TableOfContents: []);

    // ─── LoadDocument ────────────────────────────────────────────────────────

    [Fact]
    public void LoadDocument_SetsPageCount()
    {
        var vm = Make();
        vm.LoadDocument(MakeDoc(10));
        Assert.Equal(10, vm.PageCount);
    }

    [Fact]
    public void LoadDocument_CreatesPageViewModels()
    {
        var vm = Make();
        vm.LoadDocument(MakeDoc(5));
        Assert.Equal(5, vm.Pages.Count);
    }

    [Fact]
    public void LoadDocument_ResetsToFirstPage()
    {
        var vm = Make();
        vm.LoadDocument(MakeDoc(5));
        Assert.Equal(0, vm.CurrentPageIndex);
    }

    // ─── Navigation ──────────────────────────────────────────────────────────

    [Fact]
    public void NextPage_IncreasesIndex()
    {
        var vm = Make();
        vm.LoadDocument(MakeDoc(3));

        vm.NextPageCommand.Execute(null);

        Assert.Equal(1, vm.CurrentPageIndex);
    }

    [Fact]
    public void PreviousPage_AtFirstPage_DoesNothing()
    {
        var vm = Make();
        vm.LoadDocument(MakeDoc(3));

        vm.PreviousPageCommand.Execute(null);

        Assert.Equal(0, vm.CurrentPageIndex);
    }

    [Fact]
    public void NextPage_AtLastPage_DoesNothing()
    {
        var vm = Make();
        vm.LoadDocument(MakeDoc(3));
        vm.GoToPageCommand.Execute(2);

        vm.NextPageCommand.Execute(null);

        Assert.Equal(2, vm.CurrentPageIndex);
    }

    [Fact]
    public void GoToPage_OutOfRange_DoesNothing()
    {
        var vm = Make();
        vm.LoadDocument(MakeDoc(3));

        vm.GoToPageCommand.Execute(-1);
        Assert.Equal(0, vm.CurrentPageIndex);

        vm.GoToPageCommand.Execute(99);
        Assert.Equal(0, vm.CurrentPageIndex);
    }

    [Fact]
    public void FirstPage_GoesToIndex0()
    {
        var vm = Make();
        vm.LoadDocument(MakeDoc(5));
        vm.GoToPageCommand.Execute(4);

        vm.FirstPageCommand.Execute(null);

        Assert.Equal(0, vm.CurrentPageIndex);
    }

    [Fact]
    public void LastPage_GoesToLastIndex()
    {
        var vm = Make();
        vm.LoadDocument(MakeDoc(5));

        vm.LastPageCommand.Execute(null);

        Assert.Equal(4, vm.CurrentPageIndex);
    }

    // ─── Zoom ────────────────────────────────────────────────────────────────

    [Fact]
    public void ZoomIn_ClampedAtMaximum()
    {
        var vm = Make();
        for (int i = 0; i < 200; i++)
            vm.ZoomInCommand.Execute(null);

        Assert.Equal(8.0, vm.Scale);
    }

    [Fact]
    public void ZoomOut_ClampedAtMinimum()
    {
        var vm = Make();
        for (int i = 0; i < 200; i++)
            vm.ZoomOutCommand.Execute(null);

        Assert.Equal(0.1, vm.Scale, precision: 5);
    }

    [Fact]
    public void ResetZoom_RestoresDefaultScale()
    {
        var vm = Make();
        vm.ZoomInCommand.Execute(null);

        vm.ResetZoomCommand.Execute(null);

        Assert.Equal(1.5, vm.Scale); // DefaultScale
    }

    [Fact]
    public void ZoomPercent_ReflectsCurrentScale()
    {
        var vm = Make();
        vm.ResetZoomCommand.Execute(null); // scale = 1.5

        Assert.Equal("150%", vm.ZoomPercent);
    }

    // ─── FitToWidth ──────────────────────────────────────────────────────────

    [Fact]
    public void FitToWidth_ComputesScaleFromViewportWidth()
    {
        // Pages are 595 pt wide. Viewport 631 px → scale = (631 - 36) / 595 = 1.0
        // 36 = 20 (scrollbar/padding safety) + 16 (Border Margin="8" × 2 sides)
        var vm = Make();
        vm.LoadDocument(MakeDoc(3, widthPt: 595));

        vm.FitToWidth(631.0);

        Assert.Equal(1.0, vm.Scale, precision: 5);
        Assert.True(vm.IsFitToWidth);
    }

    [Fact]
    public void ZoomIn_AfterFitToWidth_ClearsFitToWidth()
    {
        var vm = Make();
        vm.LoadDocument(MakeDoc(3, widthPt: 595));
        vm.FitToWidth(615.0);

        vm.ZoomInCommand.Execute(null);

        Assert.False(vm.IsFitToWidth);
    }

    // ─── Clear ───────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_ResetsAllState()
    {
        var vm = Make();
        vm.LoadDocument(MakeDoc(5));

        vm.Clear();

        Assert.Equal(0, vm.PageCount);
        Assert.Equal(0, vm.CurrentPageIndex);
        Assert.Empty(vm.Pages);
    }

    // ─── CanExecute ──────────────────────────────────────────────────────────

    [Fact]
    public void CanGoBack_IsFalseAtFirstPage()
    {
        var vm = Make();
        vm.LoadDocument(MakeDoc(3));
        Assert.False(vm.CanGoBack);
    }

    [Fact]
    public void CanGoForward_IsFalseAtLastPage()
    {
        var vm = Make();
        vm.LoadDocument(MakeDoc(3));
        vm.GoToPageCommand.Execute(2);
        Assert.False(vm.CanGoForward);
    }

    [Fact]
    public void CurrentPageDisplay_ReflectsOneBasedIndex()
    {
        var vm = Make();
        vm.LoadDocument(MakeDoc(5));
        vm.GoToPageCommand.Execute(2); // 0-based index 2 = page 3

        Assert.Equal("3 / 5", vm.CurrentPageDisplay);
    }
}
