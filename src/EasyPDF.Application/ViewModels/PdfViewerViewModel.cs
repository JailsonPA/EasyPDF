using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using EasyPDF.Core.Rendering;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EasyPDF.Application.ViewModels;

public sealed partial class PdfViewerViewModel : ObservableObject, IDisposable
{
    private const double MinScale = 0.1;
    private const double MaxScale = 8.0;
    private const double ZoomStep = 0.25;
    private const double DefaultScale = 1.5; // ~108 DPI — comfortable default
    // Continuous-zoom inputs (wheel, manual %) snap to this granularity. Keeps the cache key
    // space bounded so revisiting a zoom level is a cache hit instead of a fresh render.
    private const double ScaleBucket = 0.05;

    private static double BucketScale(double value) =>
        Math.Round(value / ScaleBucket) * ScaleBucket;

    private readonly IPdfRenderService _renderService;
    private readonly ITextExtractionService _textService;
    private readonly IAnnotationRepository _annotationRepo;
    private readonly IPdfExportService? _exportService;
    private readonly IPdfAnnotationWriter? _annotationWriter;
    private readonly IPreferencesRepository _prefsRepo;
    private readonly ILogger<PdfViewerViewModel> _logger;
    private readonly Dictionary<int, CancellationTokenSource> _pendingRenders = new();
    private bool _applyingFitToWidth;
    private bool _applyingFitToPage;

    // Skips persistence during the constructor when initial values come from saved prefs —
    // otherwise the field assignments would write the same value back to disk on every load.
    private bool _suppressPrefsPersistence = true;

    private sealed class NoOpPreferences : IPreferencesRepository
    {
        public UserPreferences Get() => new();
        public Task SaveAsync(UserPreferences prefs, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── Estado de edição ───────────────────────────────────────────────────────
    [ObservableProperty]
    private EditMode _currentEditMode = EditMode.None;

    [ObservableProperty]
    private string _inkColor = "#FF2563EB";   // azul padrão

    [ObservableProperty]
    private double _inkThickness = 3.0;

    // Página e pontos do traço ink em andamento
    private int _activeInkPageIndex = -1;
    private readonly List<PdfPoint> _activeInkPdfPoints = [];

    // Progresso de export (0–100)
    [ObservableProperty]
    private int _exportProgress;

    [ObservableProperty]
    private bool _isExporting;

    private IReadOnlyList<SearchResult> _searchResults = [];
    private int _activeResultIndex = -1;

    private List<Annotation> _annotations = [];

    /// Read-only snapshot of the current annotations. Used by the print preview / writer
    /// who need to know what to bake onto each page. Returned as IReadOnlyList so callers
    /// can iterate without risking mutation of the internal list.
    public IReadOnlyList<Annotation> Annotations => _annotations;

    private string? _documentPath;
    private string? _documentHash;
    private IReadOnlyList<PdfRect>? _lastSelectionQuads;
    private int _lastSelectionPageIndex = -1;

    /// Bounded undo/redo stack for annotation mutations on the active document.
    /// Cleared on every <see cref="LoadDocument"/> — IDs from a previous PDF would
    /// be meaningless against the new one.
    private readonly AnnotationHistory _history = new();

    private string? _selectedText;
    public string? SelectedText => _selectedText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageDisplay))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoForward))]
    private int _currentPageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercent))]
    private double _scale = DefaultScale;

    [ObservableProperty]
    private int _pageCount;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isFitToWidth = true;

    [ObservableProperty]
    private bool _isFitToPage;

    /// True when the loaded PDF has no extractable text on any of the sampled pages
    /// (scanned image-only or vector-outlined). The view binds a banner to this so the
    /// user understands why selection/search/highlights don't work.
    [ObservableProperty]
    private bool _isTextLayerMissing;

    public BulkObservableCollection<PageViewModel> Pages { get; } = new();

    public string CurrentPageDisplay => $"{CurrentPageIndex + 1} / {PageCount}";
    public string ZoomPercent => $"{Scale * 100:F0}%";
    public bool CanGoBack => CurrentPageIndex > 0;
    public bool CanGoForward => CurrentPageIndex < PageCount - 1;

    public event EventHandler<int>? ScrollToPageRequested;
    public event EventHandler<(double TopFrac, double HeightFrac)>? ViewportChanged;

    public event EventHandler? FitToWidthRequested;
    public event EventHandler? FitToPageRequested;

    public void SetViewport(double topFrac, double heightFrac) =>
        ViewportChanged?.Invoke(this, (topFrac, heightFrac));

    public PdfViewerViewModel(
        IPdfRenderService renderService,
        ITextExtractionService textService,
        IAnnotationRepository annotationRepo,
        ILogger<PdfViewerViewModel> logger,
        IPdfExportService? exportService = null,
        IPreferencesRepository? prefsRepo = null,
        IPdfAnnotationWriter? annotationWriter = null)
    {
        _renderService    = renderService;
        _textService      = textService;
        _annotationRepo   = annotationRepo;
        _exportService    = exportService;
        _annotationWriter = annotationWriter;
        _prefsRepo        = prefsRepo ?? new NoOpPreferences();
        _logger           = logger;

        // Apply saved prefs by writing the backing fields directly — bypasses property setters
        // so the OnXxxChanged partial methods don't try to save the loaded value back to disk.
        var prefs = _prefsRepo.Get();
        _inkColor     = prefs.DefaultInkColor;
        _inkThickness = prefs.DefaultInkThickness;
        _isFitToWidth = prefs.DefaultZoomMode == ZoomMode.FitToWidth;
        _isFitToPage  = prefs.DefaultZoomMode == ZoomMode.FitToPage;
        _suppressPrefsPersistence = false;

        // Push history state into observable properties so menu items can bind to CanUndo/CanRedo.
        _history.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };
    }

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    public Task UndoAsync(CancellationToken ct = default) => _history.UndoAsync(ct);

    [RelayCommand(CanExecute = nameof(CanRedo))]
    public Task RedoAsync(CancellationToken ct = default) => _history.RedoAsync(ct);

    /// Snapshot of the current zoom UI state (Manual / FitToWidth / FitToPage).
    private ZoomMode CurrentZoomMode =>
        IsFitToWidth ? ZoomMode.FitToWidth :
        IsFitToPage  ? ZoomMode.FitToPage  : ZoomMode.Manual;

    /// Reads-then-writes via the repo so we only ever overwrite the one field that
    /// actually changed. Skipped during construction (prefs were just loaded, no diff yet).
    private void PersistPref(Func<UserPreferences, UserPreferences> mutate)
    {
        if (_suppressPrefsPersistence) return;
        var current  = _prefsRepo.Get();
        var updated  = mutate(current);
        if (updated == current) return;  // record equality — no-op when nothing changed
        _ = _prefsRepo.SaveAsync(updated);
    }

    partial void OnIsFitToWidthChanged(bool value) =>
        PersistPref(p => p with { DefaultZoomMode = CurrentZoomMode });

    partial void OnIsFitToPageChanged(bool value) =>
        PersistPref(p => p with { DefaultZoomMode = CurrentZoomMode });

    partial void OnInkColorChanged(string value) =>
        PersistPref(p => p with { DefaultInkColor = value });

    partial void OnInkThicknessChanged(double value) =>
        PersistPref(p => p with { DefaultInkThickness = value });

    public void LoadDocument(PdfDocument document)
    {
        var vms = document.Pages.Select(p =>
        {
            var vm = new PageViewModel(p.Index, p.WidthPt, p.HeightPt, p.Links);
            vm.Scale = Scale;
            return vm;
        });
        Pages.ReplaceAll(vms); // 1 Reset notification instead of N Add notifications

        // History from a previous document is meaningless here — IDs don't transfer.
        _history.Clear();

        IsTextLayerMissing = !document.HasTextLayer;
        PageCount = document.PageCount;
        CurrentPageIndex = 0;
        IsLoading = false;
        ErrorMessage = null;

        if (IsFitToWidth)
            FitToWidthRequested?.Invoke(this, EventArgs.Empty);
        else if (IsFitToPage)
            FitToPageRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadAnnotationsAsync(string documentPath, string? contentHash = null, CancellationToken ct = default)
    {
        _documentPath = documentPath;
        _documentHash = contentHash;
        _annotations = (await _annotationRepo.GetAllAsync(documentPath, contentHash, ct)).ToList();
        ApplyAnnotationHighlights();
        ApplyAllNoteAnnotations();
        ApplyAllInkStrokes();

        // Pages may have already rendered before annotations were loaded.
        // Rebake the bitmap in place if it's available; otherwise the bake will happen
        // automatically the next time the page renders.
        foreach (int idx in _annotations
            .Where(a => a.Type == AnnotationType.Highlight || a.Type == AnnotationType.Underline)
            .Select(a => a.PageIndex)
            .Distinct())
        {
            await RebakePageBitmapAsync(idx, ct);
        }
    }

    public void Clear()
    {
        CancelAllPendingRenders();
        ClearSelection();
        Pages.Clear();
        PageCount = 0;
        CurrentPageIndex = 0;
        _searchResults = [];
        _activeResultIndex = -1;
        _annotations = [];
        _history.Clear();
        _documentPath = null;
        _documentHash = null;
    }


    /// <paramref name="dpiScale"/> is the screen's physical-to-logical pixel ratio,

    public async Task OnPageBecameVisibleAsync(int pageIndex, double dpiScale = 1.0, CancellationToken viewCt = default)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        var pageVm = Pages[pageIndex];
        if (pageVm.RenderedPage is not null && !pageVm.IsStale) return; // already rendered at current scale


        if (_pendingRenders.TryGetValue(pageIndex, out var old))
        {
            old.Cancel();
            old.Dispose();
            _pendingRenders.Remove(pageIndex);
        }


        var cts = CancellationTokenSource.CreateLinkedTokenSource(viewCt);
        _pendingRenders[pageIndex] = cts;

        try
        {
            if (!pageVm.IsStale)
                pageVm.IsRendering = true;  // suppress spinner when old bitmap is still visible
            var rendered = await _renderService.RenderPageAsync(pageIndex, Scale, dpiScale, cts.Token);

            var pageAnnotations = _annotations
                .Where(a => a.PageIndex == pageIndex
                         && (a.Type == AnnotationType.Highlight || a.Type == AnnotationType.Underline))
                .ToList();
            double physicalScale = Scale * dpiScale;

            var bakedImage = await Task.Run(() =>
            {
                var src = pageAnnotations.Count > 0
                    ? BakeHighlights(rendered, pageAnnotations, physicalScale)
                    : rendered;
                return BuildFrozenBitmap(src);
            }, cts.Token);

            pageVm.IsStale = false;
            pageVm.RenderedPage = rendered;   // mantém o original (sem bake) para refazer o bake mais tarde
            pageVm.PageImage = bakedImage;

        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Render failed for page {Page}", pageIndex);
            pageVm.IsStale = false;
            pageVm.RenderedPage = null;
            pageVm.PageImage = null;
            pageVm.HasError = true;
        }
        finally
        {
            pageVm.IsRendering = false;

            if (_pendingRenders.TryGetValue(pageIndex, out var current) && ReferenceEquals(current, cts))
            {
                _pendingRenders.Remove(pageIndex);
                cts.Dispose();
            }

        }
    }


    public async Task<RenderedPage?> RenderPageForClipboardAsync(int pageIndex, CancellationToken ct = default)
    {
        const double clipboardDpi = 150.0;
        try
        {
            return await _renderService.RenderPageAsync(pageIndex, clipboardDpi / 72.0, 1.0, ct)
                                       .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Clipboard render failed for page {Page}", pageIndex);
            return null;
        }
    }

    public void FitToWidth(double viewportWidth)
    {
        if (Pages.Count == 0 || viewportWidth <= 0) return;
        var firstPage = Pages[0];
        if (firstPage.WidthPt <= 0) return;
        _applyingFitToWidth = true;
        try
        {
            // 20 = scrollbar/padding safety margin; 16 = Border Margin="8" × 2 sides (ControlWidth offset)
        Scale = Math.Clamp((viewportWidth - 36) / firstPage.WidthPt, MinScale, MaxScale);
            IsFitToWidth = true;
        }
        finally
        {
            _applyingFitToWidth = false;
        }
    }

    public void FitToPage(double viewportHeight)
    {
        if (Pages.Count == 0 || viewportHeight <= 0) return;
        var firstPage = Pages[0];
        if (firstPage.HeightPt <= 0) return;
        _applyingFitToPage = true;
        try
        {
            // 32 = 16 (shadow margin) + 8 (page top+bottom margin) + 8 (buffer)
            Scale = Math.Clamp((viewportHeight - 32) / firstPage.HeightPt, MinScale, MaxScale);
            IsFitToPage = true;
        }
        finally
        {
            _applyingFitToPage = false;
        }
    }

    partial void OnScaleChanged(double value)
    {
        if (!_applyingFitToWidth)
            IsFitToWidth = false;
        if (!_applyingFitToPage)
            IsFitToPage = false;
        CancelAllPendingRenders();
        foreach (var p in Pages)
        {
            p.Scale = value;
            if (p.RenderedPage is not null)
                p.IsStale = true;  // keep old bitmap visible; fresh render will clear this
        }
        if (_searchResults.Count > 0)
            ApplySearchHighlights();
        if (_annotations.Count > 0)
        {
            ApplyAnnotationHighlights();
            ApplyAllNoteAnnotations();
            ApplyAllInkStrokes();
        }
    }


    public void UpdateSearchHighlights(IReadOnlyList<SearchResult> results, int activeIndex)
    {
        _searchResults = results;
        _activeResultIndex = activeIndex;
        ApplySearchHighlights();
    }

    public void ClearSearchHighlights()
    {
        _searchResults = [];
        _activeResultIndex = -1;
        foreach (var p in Pages)
            p.SearchHighlights = [];
    }

    private void ApplySearchHighlights()
    {
        foreach (var p in Pages)
            p.SearchHighlights = [];

        foreach (var group in _searchResults
            .Select((r, i) => (result: r, globalIndex: i))
            .GroupBy(x => x.result.PageIndex))
        {
            if (group.Key < 0 || group.Key >= Pages.Count) continue;
            var pageVm = Pages[group.Key];

            pageVm.SearchHighlights = group
                .SelectMany(x => x.result.Quads.Select(
                    q => ConvertQuad(q, pageVm.Scale, x.globalIndex == _activeResultIndex)))
                .ToList();
        }
    }

    // ─── Annotations ──────────────────────────────────────────────────────────

    public async Task AddAnnotationAsync(AnnotationType type, AnnotationColor color, CancellationToken ct = default)
    {
        if (_documentPath is null || _lastSelectionQuads is null || _lastSelectionQuads.Count == 0) return;

        var annotation = new Annotation(
            Guid.NewGuid(),
            _documentPath,
            _lastSelectionPageIndex,
            type,
            color,
            _lastSelectionQuads,
            DateTime.UtcNow) { ContentHash = _documentHash };

        var cmd = new AnnotationCommand(
            Do:   c => ApplyAddAnnotationAsync(annotation, c),
            Undo: c => ApplyRemoveAnnotationAsync(annotation.Id, c));

        await _history.ExecuteAsync(cmd, ct);
        ClearSelection();
    }

    /// Replaces the color of an existing Highlight or Underline annotation. No-op for
    /// other types (Ink uses StrokeColor hex; Note has no color) or when the new color
    /// matches the current one.
    public async Task ChangeAnnotationColorAsync(Guid id, AnnotationColor newColor, CancellationToken ct = default)
    {
        int idx = _annotations.FindIndex(a => a.Id == id);
        if (idx < 0) return;

        var existing = _annotations[idx];
        if (existing.Type is not (AnnotationType.Highlight or AnnotationType.Underline)) return;
        if (existing.Color == newColor) return;

        var oldColor = existing.Color;
        var cmd = new AnnotationCommand(
            Do:   c => ApplyChangeColorAsync(id, newColor, c),
            Undo: c => ApplyChangeColorAsync(id, oldColor, c));

        await _history.ExecuteAsync(cmd, ct);
    }

    public async Task RemoveAnnotationAsync(Guid id, CancellationToken ct = default)
    {
        var snapshot = _annotations.FirstOrDefault(a => a.Id == id);
        if (snapshot is null) return;

        // Capture the full record so Undo can re-insert it with the same id, page,
        // quads, ink points, etc. The repo's INSERT OR REPLACE handles re-add atomically.
        var cmd = new AnnotationCommand(
            Do:   c => ApplyRemoveAnnotationAsync(snapshot.Id, c),
            Undo: c => ApplyAddAnnotationAsync(snapshot, c));

        await _history.ExecuteAsync(cmd, ct);
    }

    // ─── Apply primitives (called by history Do/Undo) ─────────────────────────

    private async Task ApplyAddAnnotationAsync(Annotation ann, CancellationToken ct)
    {
        await _annotationRepo.AddAsync(ann, ct);
        if (!_annotations.Any(a => a.Id == ann.Id))
            _annotations.Add(ann);
        await RefreshPageVisualsAsync(ann.PageIndex, ann.Type, ct);
    }

    private async Task ApplyRemoveAnnotationAsync(Guid id, CancellationToken ct)
    {
        var ann = _annotations.FirstOrDefault(a => a.Id == id);
        if (ann is null) return;
        await _annotationRepo.RemoveAsync(id, ct);
        _annotations.Remove(ann);
        await RefreshPageVisualsAsync(ann.PageIndex, ann.Type, ct);
    }

    private async Task ApplyChangeColorAsync(Guid id, AnnotationColor color, CancellationToken ct)
    {
        int idx = _annotations.FindIndex(a => a.Id == id);
        if (idx < 0) return;
        var existing = _annotations[idx];
        if (existing.Color == color) return;

        var updated = existing with { Color = color };
        await _annotationRepo.AddAsync(updated, ct);   // INSERT OR REPLACE by id
        _annotations[idx] = updated;
        await RefreshPageVisualsAsync(updated.PageIndex, updated.Type, ct);
    }

    private async Task RefreshPageVisualsAsync(int pageIndex, AnnotationType type, CancellationToken ct)
    {
        switch (type)
        {
            case AnnotationType.Highlight:
            case AnnotationType.Underline:
                ApplyAnnotationHighlightsForPage(pageIndex);
                await RebakePageBitmapAsync(pageIndex, ct);
                break;
            case AnnotationType.Ink:
                ApplyInkStrokesForPage(pageIndex);
                break;
            case AnnotationType.Note:
                ApplyNoteAnnotationsForPage(pageIndex);
                break;
        }
    }

    /// Re-aplica o bake do highlight/underline ao bitmap atual sem chamar o MuPDF de novo.
    /// Usa o RenderedPage original (não-baked) que está na PageViewModel como fonte.
    /// Se a página ainda não foi renderizada, retorna sem efeito — o bake acontecerá no próximo render.
    private async Task RebakePageBitmapAsync(int pageIndex, CancellationToken ct = default)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        var pageVm = Pages[pageIndex];
        var rendered = pageVm.RenderedPage;
        if (rendered is null) return;

        var pageAnns = _annotations
            .Where(a => a.PageIndex == pageIndex
                     && (a.Type == AnnotationType.Highlight || a.Type == AnnotationType.Underline))
            .ToList();

        double physicalScale = pageVm.Scale * rendered.DpiScale;

        try
        {
            var bakedImage = await Task.Run(() =>
            {
                var src = pageAnns.Count > 0
                    ? BakeHighlights(rendered, pageAnns, physicalScale)
                    : rendered;
                return BuildFrozenBitmap(src);
            }, ct);

            pageVm.PageImage = bakedImage;
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyAnnotationHighlights()
    {
        foreach (var p in Pages)
            p.AnnotationHighlights = [];

        foreach (var group in _annotations.GroupBy(a => a.PageIndex))
        {
            if (group.Key < 0 || group.Key >= Pages.Count) continue;
            var pageVm = Pages[group.Key];
            pageVm.AnnotationHighlights = group
                .SelectMany(a => a.Quads.Select(q => new AnnotationHighlightRect(
                    a.Id,
                    q.X * pageVm.Scale, q.Y * pageVm.Scale,
                    q.Width * pageVm.Scale, q.Height * pageVm.Scale,
                    a.Color, a.Type == AnnotationType.Underline)))
                .ToList();
        }
    }

    private void ApplyAnnotationHighlightsForPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        var pageVm = Pages[pageIndex];
        pageVm.AnnotationHighlights = _annotations
            .Where(a => a.PageIndex == pageIndex)
            .SelectMany(a => a.Quads.Select(q => new AnnotationHighlightRect(
                a.Id,
                q.X * pageVm.Scale, q.Y * pageVm.Scale,
                q.Width * pageVm.Scale, q.Height * pageVm.Scale,
                a.Color, a.Type == AnnotationType.Underline)))
            .ToList();
    }

    // ─── Text selection ────────────────────────────────────────────────────────

    public void ClearSelection()
    {
        _selectedText = null;
        _lastSelectionQuads = null;
        _lastSelectionPageIndex = -1;
        foreach (var p in Pages) p.SelectionHighlights = [];
    }

    public async Task ExtractSelectionAsync(
        PageViewModel pageVm,
        float startX, float startY,
        float endX,   float endY,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _textService.ExtractSelectionAsync(
                pageVm.PageIndex,
                new PdfPoint(startX, startY),
                new PdfPoint(endX, endY),
                ct);


            ct.ThrowIfCancellationRequested();
            ApplySelectionResult(pageVm, result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Text selection extraction failed on page {Page}", pageVm.PageIndex);
            pageVm.SelectionHighlights = [];
        }
    }

    public async Task ExtractAtPointAsync(
        PageViewModel pageVm,
        float x, float y,
        TextSelectionUnit unit,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _textService.ExtractAtPointAsync(
                pageVm.PageIndex,
                new PdfPoint(x, y),
                unit,
                ct);

            ct.ThrowIfCancellationRequested();
            ApplySelectionResult(pageVm, result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Text point extraction failed on page {Page}", pageVm.PageIndex);
            pageVm.SelectionHighlights = [];
        }
    }

    private void ApplySelectionResult(PageViewModel pageVm, TextSelection? result)
    {
        if (result is null || result.Quads.Count == 0)
        {
            _lastSelectionQuads = null;
            _lastSelectionPageIndex = -1;
            pageVm.SelectionHighlights = [];
            return;
        }

        _lastSelectionQuads = result.Quads;
        _lastSelectionPageIndex = pageVm.PageIndex;

        pageVm.SelectionHighlights = result.Quads
            .Select(q => new HighlightRect(
                q.X * pageVm.Scale, q.Y * pageVm.Scale,
                q.Width * pageVm.Scale, q.Height * pageVm.Scale,
                false))
            .ToList();

        _selectedText = result.Text;
    }

    private static HighlightRect ConvertQuad(PdfRect q, double scale, bool isActive) =>
        new(q.X * scale, q.Y * scale, q.Width * scale, q.Height * scale, isActive);

    /// Builds a frozen BitmapSource from a RenderedPage's pixel data. Frozen bitmaps are
    /// thread-safe and immutable, so we can construct them on any background thread and
    /// hand the result over to the UI binding with zero further marshalling work.
    private static ImageSource BuildFrozenBitmap(RenderedPage page)
    {
        var format = page.BitsPerPixel == 32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24;
        double dpi = 96.0 * page.DpiScale;
        var bitmap = BitmapSource.Create(
            page.Width, page.Height,
            dpi, dpi,
            format, null,
            page.PixelData, page.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// Composites highlight + underline annotations onto a fresh copy of the page's pixel
    /// buffer, delegating per-annotation pixel math to <see cref="AnnotationBaker"/>.
    /// Returns a new <see cref="RenderedPage"/> backed by the cloned buffer; the cached/
    /// original page passed in is never mutated.
    private static RenderedPage BakeHighlights(
        RenderedPage page,
        IEnumerable<Annotation> annotations,
        double physicalScale)
    {
        byte[] pixels = (byte[])page.PixelData.Clone();

        foreach (var ann in annotations)
        {
            if (ann.Type == AnnotationType.Highlight)
                AnnotationBaker.BakeHighlight(pixels, page, ann, physicalScale);
            else if (ann.Type == AnnotationType.Underline)
                AnnotationBaker.BakeUnderline(pixels, page, ann, physicalScale);
        }

        return page with { PixelData = pixels };
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousPage()
    {
        if (CanGoBack)
        {
            CurrentPageIndex--;
            ScrollToPageRequested?.Invoke(this, CurrentPageIndex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void NextPage()
    {
        if (CanGoForward)
        {
            CurrentPageIndex++;
            ScrollToPageRequested?.Invoke(this, CurrentPageIndex);
        }
    }

    [RelayCommand]
    private void GoToPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount) return;
        CurrentPageIndex = pageIndex;
        ScrollToPageRequested?.Invoke(this, CurrentPageIndex);
    }

    [RelayCommand]
    private void FitToWidthMode() => IsFitToWidth = true; // View applies FitToWidth via PropertyChanged

    [RelayCommand]
    private void FitToPageMode() => IsFitToPage = true; // View applies FitToPage via PropertyChanged

    [RelayCommand]
    private void ZoomIn() => Scale = Math.Min(MaxScale, Scale + ZoomStep);

    [RelayCommand]
    private void ZoomOut() => Scale = Math.Max(MinScale, Scale - ZoomStep);

    [RelayCommand]
    private void ResetZoom() => Scale = DefaultScale;

    [RelayCommand]
    private void ZoomTo(double percent) =>
        Scale = BucketScale(Math.Clamp(percent / 100.0, MinScale, MaxScale));

    [RelayCommand]
    private void RotateCurrentPageClockwise()
    {
        if (CurrentPageIndex >= 0 && CurrentPageIndex < Pages.Count)
            Pages[CurrentPageIndex].Rotation = (Pages[CurrentPageIndex].Rotation + 90) % 360;
    }

    [RelayCommand]
    private void RotateCurrentPageCounterClockwise()
    {
        if (CurrentPageIndex >= 0 && CurrentPageIndex < Pages.Count)
            Pages[CurrentPageIndex].Rotation = (Pages[CurrentPageIndex].Rotation + 270) % 360;
    }

    [RelayCommand]
    private void FirstPage() => GoToPage(0);

    [RelayCommand]
    private void LastPage() => GoToPage(PageCount - 1);

    public void HandleMouseWheelZoom(bool zoomIn, double delta)
    {
        // Touchpad inertial scroll can produce deltas in the thousands. Without a cap, a
        // single event would multiply Scale by 3-4× and feel jarring. 240 ≈ 2 wheel notches,
        // which is the largest "deliberate" magnitude a user produces in one event.
        double clampedDelta = Math.Min(Math.Abs(delta), 240);
        double factor = 1.0 + (clampedDelta / 1200.0);
        double next = zoomIn ? Scale * factor : Scale / factor;
        double bucketed = BucketScale(Math.Clamp(next, MinScale, MaxScale));

        // Tiny wheel deltas can round back to current scale — force advance by one bucket
        // so every wheel notch produces visible feedback.
        if (bucketed == Scale)
            bucketed = BucketScale(Math.Clamp(Scale + (zoomIn ? ScaleBucket : -ScaleBucket), MinScale, MaxScale));

        Scale = bucketed;
    }

    private void CancelAllPendingRenders()
    {
        foreach (var cts in _pendingRenders.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _pendingRenders.Clear();
    }

    public void Dispose() => CancelAllPendingRenders();

    // ══════════════════════════════════════════════════════════════════════════
    // EDIÇÃO — Note, Ink, Export
    // ══════════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private void SetEditMode(EditMode mode) =>
        CurrentEditMode = CurrentEditMode == mode ? EditMode.None : mode;

    [RelayCommand]
    private void SetInkColor(string colorHex) => InkColor = colorHex;

    // Mirror of the right-click "Highlight" submenu: paints the current text selection
    // with the chosen colour. Bails silently if there's no live selection — the menu
    // command can fire without a valid selection (no enable-when-selected binding).
    [RelayCommand]
    private async Task HighlightSelectionAsync(string colorName)
    {
        if (_lastSelectionQuads is null || _lastSelectionQuads.Count == 0) return;
        var color = colorName switch
        {
            "Yellow" => AnnotationColor.Yellow,
            "Green"  => AnnotationColor.Green,
            "Pink"   => AnnotationColor.Pink,
            "Blue"   => AnnotationColor.Blue,
            "Red"    => AnnotationColor.Red,
            _        => AnnotationColor.Yellow
        };
        await AddAnnotationAsync(AnnotationType.Highlight, color);
    }

    // ─── Nota de texto ────────────────────────────────────────────────────────

    /// <summary>Chamado pela View quando o usuário clica na página no modo Note.</summary>
    public async Task AddNoteAtPointAsync(
        PageViewModel pageVm,
        double pdfX, double pdfY,
        string text,
        CancellationToken ct = default)
    {
        if (_documentPath is null || string.IsNullOrWhiteSpace(text)) return;

        // Uma única rect de 120×80pt centrada no clique
        var rect = new PdfRect((float)(pdfX - 60), (float)(pdfY - 40), 120f, 80f);
        var ann  = new Annotation(
            Guid.NewGuid(), _documentPath, pageVm.PageIndex,
            AnnotationType.Note, AnnotationColor.Yellow, [rect],
            DateTime.UtcNow, NoteContent: text) { ContentHash = _documentHash };

        var cmd = new AnnotationCommand(
            Do:   c => ApplyAddAnnotationAsync(ann, c),
            Undo: c => ApplyRemoveAnnotationAsync(ann.Id, c));

        await _history.ExecuteAsync(cmd, ct);
    }

    public async Task EditNoteAsync(Guid id, string newText, CancellationToken ct = default)
    {
        int idx = _annotations.FindIndex(a => a.Id == id);
        if (idx < 0) return;

        var existing = _annotations[idx];
        if (existing.Type != AnnotationType.Note) return;
        if (existing.NoteContent == newText) return;   // no-op edit, skip history pollution

        var oldContent = existing.NoteContent ?? string.Empty;
        var cmd = new AnnotationCommand(
            Do:   c => ApplyEditNoteAsync(id, newText, c),
            Undo: c => ApplyEditNoteAsync(id, oldContent, c));

        await _history.ExecuteAsync(cmd, ct);
    }

    private async Task ApplyEditNoteAsync(Guid id, string newText, CancellationToken ct)
    {
        int idx = _annotations.FindIndex(a => a.Id == id);
        if (idx < 0) return;
        var existing = _annotations[idx];
        if (existing.NoteContent == newText) return;

        var updated = existing with { NoteContent = newText };
        await _annotationRepo.AddAsync(updated, ct);   // INSERT OR REPLACE by id — one round-trip
        _annotations[idx] = updated;
        ApplyNoteAnnotationsForPage(updated.PageIndex);
    }

    private void ApplyNoteAnnotationsForPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        var pageVm = Pages[pageIndex];
        pageVm.NoteAnnotations = _annotations
            .Where(a => a.PageIndex == pageIndex && a.Type == AnnotationType.Note)
            .Select(a =>
            {
                var r = a.Quads[0];
                return new NoteAnnotationViewModel(
                    a.Id,
                    (r.X + r.Width / 2) * pageVm.Scale,
                    (r.Y + r.Height / 2) * pageVm.Scale,
                    a.NoteContent ?? string.Empty);
            })
            .ToList();
    }

    private void ApplyAllNoteAnnotations()
    {
        foreach (var p in Pages) p.NoteAnnotations = [];
        foreach (var group in _annotations.Where(a => a.Type == AnnotationType.Note)
                                           .GroupBy(a => a.PageIndex))
        {
            if (group.Key < 0 || group.Key >= Pages.Count) continue;
            var pageVm = Pages[group.Key];
            pageVm.NoteAnnotations = group.Select(a =>
            {
                var r = a.Quads[0];
                return new NoteAnnotationViewModel(
                    a.Id,
                    (r.X + r.Width / 2) * pageVm.Scale,
                    (r.Y + r.Height / 2) * pageVm.Scale,
                    a.NoteContent ?? string.Empty);
            }).ToList();
        }
    }

    // ─── Ink / Desenho livre ──────────────────────────────────────────────────

    /// <summary>Inicia um novo traço na página. Chamado no MouseDown (modo Ink).</summary>
    public void BeginInkStroke(PageViewModel pageVm, double pdfX, double pdfY)
    {
        _activeInkPageIndex = pageVm.PageIndex;
        _activeInkPdfPoints.Clear();
        _activeInkPdfPoints.Add(new PdfPoint((float)pdfX, (float)pdfY));
    }

    /// <summary>Acrescenta um ponto ao traço em andamento. Chamado no MouseMove (modo Ink).</summary>
    public void ContinueInkStroke(PageViewModel pageVm, double pdfX, double pdfY)
    {
        if (_activeInkPageIndex != pageVm.PageIndex) return;
        _activeInkPdfPoints.Add(new PdfPoint((float)pdfX, (float)pdfY));
    }

    /// <summary>Finaliza o traço e persiste como Annotation. Chamado no MouseUp (modo Ink).</summary>
    public async Task CommitInkStrokeAsync(PageViewModel pageVm, CancellationToken ct = default)
    {
        if (_documentPath is null || _activeInkPdfPoints.Count < 2)
        {
            _activeInkPdfPoints.Clear();
            return;
        }

        // Bounding box do traço como Quads[0]
        float x0 = _activeInkPdfPoints.Min(p => p.X);
        float y0 = _activeInkPdfPoints.Min(p => p.Y);
        float x1 = _activeInkPdfPoints.Max(p => p.X);
        float y1 = _activeInkPdfPoints.Max(p => p.Y);
        var bbox = new PdfRect(x0, y0, x1 - x0, y1 - y0);

        var ann = new Annotation(
            Guid.NewGuid(), _documentPath, pageVm.PageIndex,
            AnnotationType.Ink, AnnotationColor.Blue, [bbox],
            DateTime.UtcNow,
            InkPoints: _activeInkPdfPoints.ToList(),
            InkThickness: InkThickness,
            StrokeColor: InkColor) { ContentHash = _documentHash };

        _activeInkPdfPoints.Clear();
        _activeInkPageIndex = -1;

        var cmd = new AnnotationCommand(
            Do:   c => ApplyAddAnnotationAsync(ann, c),
            Undo: c => ApplyRemoveAnnotationAsync(ann.Id, c));

        await _history.ExecuteAsync(cmd, ct);
    }

    private void ApplyInkStrokesForPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        var pageVm = Pages[pageIndex];
        pageVm.InkStrokes = _annotations
            .Where(a => a.PageIndex == pageIndex && a.Type == AnnotationType.Ink
                        && a.InkPoints?.Count > 1)
            .Select(a => new InkStrokeViewModel(
                a.Id,
                a.InkPoints!.Select(p => new DisplayPoint(p.X * pageVm.Scale, p.Y * pageVm.Scale))
                             .ToList(),
                a.InkThickness,
                a.StrokeColor ?? "#FF2563EB"))
            .ToList();
    }

    private void ApplyAllInkStrokes()
    {
        foreach (var p in Pages) p.InkStrokes = [];
        foreach (var group in _annotations.Where(a => a.Type == AnnotationType.Ink)
                                           .GroupBy(a => a.PageIndex))
        {
            if (group.Key < 0 || group.Key >= Pages.Count) continue;
            var pageVm = Pages[group.Key];
            pageVm.InkStrokes = group
                .Where(a => a.InkPoints?.Count > 1)
                .Select(a => new InkStrokeViewModel(
                    a.Id,
                    a.InkPoints!.Select(p => new DisplayPoint(p.X * pageVm.Scale, p.Y * pageVm.Scale))
                                .ToList(),
                    a.InkThickness,
                    a.StrokeColor ?? "#FF2563EB"))
                .ToList();
        }
    }

    // ─── Export ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        if (_exportService is null || _documentPath is null) return;

        // A View deve ouvir esse evento para abrir SaveFileDialog
        ExportRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task ExportToPathAsync(string outputPath, CancellationToken ct = default)
    {
        if (_exportService is null) return;

        IsExporting = true;
        ExportProgress = 0;
        try
        {
            var progress = new Progress<int>(p => ExportProgress = p);
            await _exportService.ExportAsync(outputPath, _annotations, progress: progress, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed");
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// Writes the current annotations into a copy of the source PDF as native PDF
    /// annotation objects, preserving selectable text and vectors. Differs from
    /// <see cref="ExportToPathAsync"/> which rasterizes every page.
    /// Throws on failure so callers can surface a useful error — unlike export, this
    /// can fail for legitimate reasons (encrypted PDF, signed PDF, unsupported structure)
    /// and we don't want to silently produce a missing/empty file.
    public async Task SaveAnnotationsToPdfAsync(string outputPath, CancellationToken ct = default)
    {
        if (_annotationWriter is null)
            throw new InvalidOperationException("Annotation writer not configured.");
        if (_documentPath is null)
            throw new InvalidOperationException("No document is currently open.");

        IsExporting = true;
        try
        {
            await _annotationWriter.WriteAsync(_documentPath, outputPath, _annotations, ct);
        }
        finally
        {
            IsExporting = false;
        }
    }

    public event EventHandler? ExportRequested;
}

/// <summary>
/// Per-page state visible to the ItemsControl; holds the rendered bitmap once available.
/// </summary>
public sealed partial class PageViewModel : ObservableObject
{
    public int PageIndex { get; }
    public int DisplayNumber => PageIndex + 1;
    public double WidthPt { get; }
    public double HeightPt { get; }
    public IReadOnlyList<PdfLink> Links { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayWidth))]
    [NotifyPropertyChangedFor(nameof(DisplayHeight))]
    [NotifyPropertyChangedFor(nameof(ControlWidth))]
    [NotifyPropertyChangedFor(nameof(ControlHeight))]
    [NotifyPropertyChangedFor(nameof(BitmapWidth))]
    [NotifyPropertyChangedFor(nameof(BitmapHeight))]
    private double _scale = 1.5;

    // Rotation in degrees — 0, 90, 180, or 270.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayWidth))]
    [NotifyPropertyChangedFor(nameof(DisplayHeight))]
    [NotifyPropertyChangedFor(nameof(ControlWidth))]
    [NotifyPropertyChangedFor(nameof(ControlHeight))]
    private int _rotation;

    private bool IsTransverse => Rotation == 90 || Rotation == 270;


    public double BitmapWidth  => WidthPt  * Scale;
    public double BitmapHeight => HeightPt * Scale;

    public double DisplayWidth  => IsTransverse ? HeightPt * Scale : WidthPt  * Scale;
    public double DisplayHeight => IsTransverse ? WidthPt  * Scale : HeightPt * Scale;

    public double ControlWidth  => DisplayWidth  + 16;
    public double ControlHeight => DisplayHeight + 16;

    [ObservableProperty]
    private RenderedPage? _renderedPage;

    // Pre-built frozen BitmapSource — created on a background thread so the UI binding
    // doesn't pay the BitmapSource.Create cost (tens to hundreds of ms for large pages).
    [ObservableProperty]
    private ImageSource? _pageImage;


    [ObservableProperty]
    private bool _isStale;

    [ObservableProperty]
    private bool _isRendering;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private IReadOnlyList<HighlightRect> _searchHighlights = [];

    [ObservableProperty]
    private IReadOnlyList<HighlightRect> _selectionHighlights = [];

    [ObservableProperty]
    private IReadOnlyList<AnnotationHighlightRect> _annotationHighlights = [];

    // ── Novas coleções de overlay ──────────────────────────────────────────────

    [ObservableProperty]
    private IReadOnlyList<NoteAnnotationViewModel> _noteAnnotations = [];

    [ObservableProperty]
    private IReadOnlyList<InkStrokeViewModel> _inkStrokes = [];


    public PageViewModel(int pageIndex, double widthPt, double heightPt,
                         IReadOnlyList<PdfLink>? links = null)
    {
        PageIndex = pageIndex;
        WidthPt   = widthPt;
        HeightPt  = heightPt;
        Links     = links ?? [];
    }
}
