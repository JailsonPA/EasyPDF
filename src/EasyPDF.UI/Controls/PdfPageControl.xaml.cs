using EasyPDF.Application.ViewModels;
using EasyPDF.Core.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace EasyPDF.UI.Controls;

public partial class PdfPageControl : UserControl
{
    public static readonly DependencyProperty ViewerVmProperty =
        DependencyProperty.Register(
            nameof(ViewerVm),
            typeof(PdfViewerViewModel),
            typeof(PdfPageControl),
            new PropertyMetadata(null, OnViewerVmChanged));

    private static void OnViewerVmChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageControl ctrl && e.NewValue is not null && ctrl.IsVisible)
            ctrl.TriggerRender();
    }

    public PdfViewerViewModel? ViewerVm
    {
        get => (PdfViewerViewModel?)GetValue(ViewerVmProperty);
        set => SetValue(ViewerVmProperty, value);
    }

    /// Single source of truth for the highlight color palette in this control —
    /// the right-click "Highlight" submenu and "Change color" submenu both build
    /// themselves from this list. Adding a colour means adding one tuple here
    /// (plus an entry in <c>AnnotationBaker</c> for the actual pixel math).
    private static readonly (string Label, AnnotationColor Color)[] HighlightColorChoices =
    [
        ("Yellow", AnnotationColor.Yellow),
        ("Green",  AnnotationColor.Green),
        ("Pink",   AnnotationColor.Pink),
        ("Blue",   AnnotationColor.Blue),
        ("Red",    AnnotationColor.Red),
    ];

    private CancellationTokenSource? _cts;
    private PageViewModel? _boundPage;

    private System.Windows.Point _selectionStartPdf;
    private bool _isSelecting;
    private CancellationTokenSource? _extractCts;
    private CancellationTokenSource? _liveDragCts;

    private const int DragThrottleMs = 24;
    private DateTime _lastDragExtractAt = DateTime.MinValue;
    private DispatcherTimer? _dragTrailingTimer;
    private System.Windows.Point _pendingDragPosPdf;

    private bool _isDrawingInk;
    private Polyline? _livePolyline;

    private const int InkThrottleMs = 10;
    private DateTime _lastInkSampleAt = DateTime.MinValue;

    public PdfPageControl()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    // ─── Conversão de coordenadas ──────────────────────────────────────────────

    /// Maps a mouse position obtained from <c>e.GetPosition(PageImage)</c> into
    /// page-relative PDF point coordinates (origin top-left, Y-down — same convention
    /// our annotations and quads use).
    ///
    /// <c>GetPosition(PageImage)</c> always returns coordinates in the Image's *local*
    /// (pre-rotation) space — even when the parent Grid carries a <c>LayoutTransform</c>
    /// for user-applied page rotation. So the right denominator is the un-rotated
    /// bitmap size (<c>WidthPt * Scale</c>), NEVER <c>DisplayWidth</c>, which swaps
    /// for 90°/270° rotations and was the source of selection breaking after rotate.
    private static (float pdfX, float pdfY) ToPdfCoords(System.Windows.Point imagePos, PageViewModel page)
    {
        double scale = page.Scale > 0 ? page.Scale : 1.0;
        return ((float)(imagePos.X / scale), (float)(imagePos.Y / scale));
    }

    // ─── DataContext / Visibility ──────────────────────────────────────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundPage is not null)
            _boundPage.PropertyChanged -= OnPagePropertyChanged;

        _boundPage = e.NewValue as PageViewModel;

        if (_boundPage is not null)
        {
            _boundPage.PropertyChanged += OnPagePropertyChanged;
            if ((_boundPage.RenderedPage is null || _boundPage.IsStale) && IsVisible)
                TriggerRender();
        }
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsVisible || _boundPage is null) return;

        if (e.PropertyName == nameof(PageViewModel.RenderedPage) && _boundPage.RenderedPage is null)
            TriggerRender();
        else if (e.PropertyName == nameof(PageViewModel.IsStale) && _boundPage.IsStale)
            TriggerRender();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            TriggerRender();
        else
            _cts?.Cancel();
    }

    private void TriggerRender()
    {
        if (ViewerVm is null || DataContext is not PageViewModel page) return;

        var dpi = VisualTreeHelper.GetDpi(this);

        // physicalScale = pixels per PDF point we want in the FINAL bitmap. We pass the exact
        // device-pixel resolution: source bitmap then maps 1:1 onto the screen with
        // Stretch="None" — WPF does no resample at all, no Fant filter softening on top of
        // whatever MuPDF rendered.
        //
        // The render service (MuPdfRenderService.RenderCore) handles the sharp-rendering
        // strategy internally: at low zooms it supersamples MuPDF up to ≥3.0 px/pt, then
        // box-averages back down to physicalScale, which keeps glyph edges crisp without
        // the Fant softening the prior pipeline had at 50/100/150% zoom. We don't need to
        // know about that here — just request the resolution we want to display.
        //
        // Cap (MaxPhysicalScale): keep bitmap memory bounded at extreme zoom on Hi-DPI.
        const double MaxPhysicalScale = 8.0;

        double effectiveScale = Math.Max(ViewerVm.Scale, 0.001);
        double nativePhysical = effectiveScale * dpi.DpiScaleX;
        double physicalScale  = Math.Min(nativePhysical, MaxPhysicalScale);

        double renderDpiScale = physicalScale / effectiveScale;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = ViewerVm.OnPageBecameVisibleAsync(page.PageIndex, renderDpiScale, _cts.Token);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_boundPage is not null)
            _boundPage.PropertyChanged -= OnPagePropertyChanged;

        _cts?.Cancel();
        _cts?.Dispose();
        _extractCts?.Cancel();
        _extractCts?.Dispose();
        _liveDragCts?.Cancel();
        _liveDragCts?.Dispose();

        if (_dragTrailingTimer is not null)
        {
            _dragTrailingTimer.Stop();
            _dragTrailingTimer.Tick -= OnDragTrailingTick;
            _dragTrailingTimer = null;
        }
    }

    // ─── Hit testing ──────────────────────────────────────────────────────────

    private static NoteAnnotationViewModel? FindNoteVm(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is FrameworkElement fe && fe.Tag is NoteAnnotationViewModel vm)
                return vm;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static AnnotationHighlightRect? HitTestAnnotation(PageViewModel page, System.Windows.Point imagePos)
    {
        foreach (var ann in page.AnnotationHighlights)
        {
            if (imagePos.X >= ann.X && imagePos.X <= ann.X + ann.Width &&
                imagePos.Y >= ann.Y && imagePos.Y <= ann.Y + ann.Height)
                return ann;
        }
        return null;
    }

    private static NoteAnnotationViewModel? HitTestNote(PageViewModel page, System.Windows.Point imagePos)
    {
        const double noteSize = 28.0;
        foreach (var note in page.NoteAnnotations)
        {
            if (imagePos.X >= note.X && imagePos.X <= note.X + noteSize &&
                imagePos.Y >= note.Y && imagePos.Y <= note.Y + noteSize)
                return note;
        }
        return null;
    }

    private static PdfLink? HitTestLink(PageViewModel page, System.Windows.Point imagePos)
    {
        var (pdfX, pdfY) = ToPdfCoords(imagePos, page);
        foreach (var link in page.Links)
        {
            var a = link.Area;
            if (pdfX >= a.X && pdfX <= a.X + a.Width &&
                pdfY >= a.Y && pdfY <= a.Y + a.Height)
                return link;
        }
        return null;
    }

    private void ActivateLink(PdfLink link)
    {
        switch (link.Destination)
        {
            case PdfLinkDestination.Internal dest:
                ViewerVm?.GoToPageCommand.Execute(dest.PageIndex);
                break;
            case PdfLinkDestination.External dest when !string.IsNullOrEmpty(dest.Uri):
                try { Process.Start(new ProcessStartInfo(dest.Uri) { UseShellExecute = true }); }
                catch { }
                break;
        }
    }

    // ─── Context menu ──────────────────────────────────────────────────────────

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (ViewerVm is null || DataContext is not PageViewModel page) return;

        var pos = e.GetPosition(PageImage);
        var link = HitTestLink(page, pos);
        bool hasText = !string.IsNullOrEmpty(ViewerVm.SelectedText);

        var menu = new ContextMenu();

        var copyItem = new MenuItem
        {
            Header = "Copy text",
            InputGestureText = "Ctrl+C",
            IsEnabled = hasText
        };
        copyItem.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(ViewerVm.SelectedText))
                System.Windows.Clipboard.SetText(ViewerVm.SelectedText);
        };
        menu.Items.Add(copyItem);

        var copyImageItem = new MenuItem { Header = "Copy page as image" };
        copyImageItem.Click += async (_, _) =>
        {
            var rendered = await ViewerVm.RenderPageForClipboardAsync(page.PageIndex);
            if (rendered is null) return;
            const double dpi = 150.0;
            var bitmap = BitmapSource.Create(
                rendered.Width, rendered.Height, dpi, dpi,
                PixelFormats.Bgra32, null, rendered.PixelData, rendered.Stride);
            bitmap.Freeze();
            Clipboard.SetImage(bitmap);
        };
        menu.Items.Add(copyImageItem);

        if (hasText)
        {
            menu.Items.Add(new Separator());

            var highlightMenu = new MenuItem { Header = "Highlight" };
            foreach (var (label, color) in HighlightColorChoices)
            {
                var item = new MenuItem { Header = label };
                var capturedColor = color;
                item.Click += async (_, _) =>
                    await ViewerVm.AddAnnotationAsync(AnnotationType.Highlight, capturedColor);
                highlightMenu.Items.Add(item);
            }
            menu.Items.Add(highlightMenu);

            var underlineItem = new MenuItem { Header = "Underline" };
            underlineItem.Click += async (_, _) =>
                await ViewerVm.AddAnnotationAsync(AnnotationType.Underline, AnnotationColor.Yellow);
            menu.Items.Add(underlineItem);
        }

        var annotation = HitTestAnnotation(page, pos);
        if (annotation is not null)
        {
            menu.Items.Add(new Separator());

            var changeColorItem = new MenuItem { Header = "Change color" };
            foreach (var (label, color) in HighlightColorChoices)
            {
                var sub = new MenuItem
                {
                    Header = label,
                    IsCheckable = true,
                    IsChecked = annotation.Color == color,
                    StaysOpenOnClick = false,
                };
                var capturedColor = color;
                var capturedId = annotation.Id;
                sub.Click += async (_, _) => await ViewerVm.ChangeAnnotationColorAsync(capturedId, capturedColor);
                changeColorItem.Items.Add(sub);
            }
            menu.Items.Add(changeColorItem);

            var removeItem = new MenuItem { Header = "Remove annotation" };
            var id = annotation.Id;
            removeItem.Click += async (_, _) => await ViewerVm.RemoveAnnotationAsync(id);
            menu.Items.Add(removeItem);
        }

        var note = HitTestNote(page, pos);
        if (note is not null)
        {
            menu.Items.Add(new Separator());

            var editNoteItem = new MenuItem { Header = "Editar nota" };
            var capturedNote = note;
            editNoteItem.Click += async (_, _) =>
            {
                var dialog = new EasyPDF.UI.Services.PromptWindow(
                    "Editar Nota", "Conteúdo da nota:", capturedNote.Content)
                { Owner = Window.GetWindow(this) };
                if (dialog.ShowDialog() == true && dialog.Value != capturedNote.Content)
                    await ViewerVm.EditNoteAsync(capturedNote.Id, dialog.Value ?? capturedNote.Content);
            };
            menu.Items.Add(editNoteItem);

            var removeNoteItem = new MenuItem { Header = "Remover nota" };
            var noteId = note.Id;
            removeNoteItem.Click += async (_, _) => await ViewerVm.RemoveAnnotationAsync(noteId);
            menu.Items.Add(removeNoteItem);
        }

        if (link.HasValue)
        {
            menu.Items.Add(new Separator());
            switch (link.Value.Destination)
            {
                case PdfLinkDestination.External extDest:
                    var openItem = new MenuItem { Header = "Open link" };
                    openItem.Click += (_, _) => ActivateLink(link.Value);
                    menu.Items.Add(openItem);

                    var copyLinkItem = new MenuItem { Header = "Copy link address" };
                    copyLinkItem.Click += (_, _) => System.Windows.Clipboard.SetText(extDest.Uri);
                    menu.Items.Add(copyLinkItem);
                    break;

                case PdfLinkDestination.Internal intDest:
                    var goToItem = new MenuItem { Header = $"Go to page {intDest.PageIndex + 1}" };
                    goToItem.Click += (_, _) => ActivateLink(link.Value);
                    menu.Items.Add(goToItem);
                    break;
            }
        }

        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ─── Mouse: selecção de texto, ink, notas ─────────────────────────────────

    protected override async void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ViewerVm is null || DataContext is not PageViewModel page) return;

        var noteVm = FindNoteVm(e.OriginalSource as DependencyObject);
        if (noteVm is not null)
        {
            var dialog = new EasyPDF.UI.Services.PromptWindow(
                "Ver / Editar Nota", "Conteúdo da nota:", noteVm.Content)
            { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true && dialog.Value != noteVm.Content)
                await ViewerVm.EditNoteAsync(noteVm.Id, dialog.Value ?? noteVm.Content);
            e.Handled = true;
            return;
        }

        var pos = e.GetPosition(PageImage);
        // GetPosition returns pre-rotation (bitmap-local) coords — compare against
        // BitmapWidth/Height, not DisplayWidth/Height (which swap for 90°/270°).
        if (pos.X < 0 || pos.Y < 0 || pos.X > page.BitmapWidth || pos.Y > page.BitmapHeight) return;

        // ── Eraser ────────────────────────────────────────────────────────────
        if (ViewerVm.CurrentEditMode == EditMode.Eraser)
        {
            const double threshold = 15.0;
            var nearest = page.InkStrokes
                .FirstOrDefault(s => s.Points.Any(p =>
                    (p.X - pos.X) * (p.X - pos.X) + (p.Y - pos.Y) * (p.Y - pos.Y)
                    < threshold * threshold));
            if (nearest is not null)
                await ViewerVm.RemoveAnnotationAsync(nearest.Id);
            e.Handled = true;
            return;
        }

        // ── Ink ───────────────────────────────────────────────────────────────
        if (ViewerVm.CurrentEditMode == EditMode.Ink)
        {
            var (inkX, inkY) = ToPdfCoords(pos, page);
            ViewerVm.BeginInkStroke(page, inkX, inkY);
            _isDrawingInk = true;

            _livePolyline = new Polyline
            {
                StrokeThickness = ViewerVm.InkThickness,
                StrokeLineJoin = PenLineJoin.Round,
                Stroke = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(ViewerVm.InkColor)!),
            };
            _livePolyline.Points.Add(pos);
            LiveInkCanvas.Children.Add(_livePolyline);

            CaptureMouse();
            e.Handled = true;
            return;
        }

        // ── Note ──────────────────────────────────────────────────────────────
        if (ViewerVm.CurrentEditMode == EditMode.Note)
        {
            var (noteX, noteY) = ToPdfCoords(pos, page);
            var prompt = new EasyPDF.UI.Services.PromptWindow(
                "Nova Anotação", "Texto da nota", "")
            { Owner = Window.GetWindow(this) };
            if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.Value))
                await ViewerVm.AddNoteAtPointAsync(page, noteX, noteY, prompt.Value);
            e.Handled = true;
            return;
        }

        // ── Link ──────────────────────────────────────────────────────────────
        var link = HitTestLink(page, pos);
        if (link is not null)
        {
            ActivateLink(link.Value);
            e.Handled = true;
            return;
        }

        // ── Double / triple click ─────────────────────────────────────────────
        if (e.ClickCount >= 2)
        {
            ViewerVm.ClearSelection();
            _isSelecting = false;

            _extractCts?.Cancel();
            _extractCts = new CancellationTokenSource();

            var unit = e.ClickCount >= 3
                ? EasyPDF.Core.Interfaces.TextSelectionUnit.Line
                : EasyPDF.Core.Interfaces.TextSelectionUnit.Word;

            var (px, py) = ToPdfCoords(pos, page);
            await ViewerVm.ExtractAtPointAsync(page, px, py, unit, _extractCts.Token);

            e.Handled = true;
            return;
        }

        // ── Selecção por drag ─────────────────────────────────────────────────
        ViewerVm.ClearSelection();
        var (sx, sy) = ToPdfCoords(pos, page);
        _selectionStartPdf = new System.Windows.Point(sx, sy);
        _isSelecting = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (DataContext is not PageViewModel page) return;

        var pos = e.GetPosition(PageImage);

        // ── Ink ───────────────────────────────────────────────────────────────
        if (_isDrawingInk && ViewerVm is not null)
        {
            var nowInk = DateTime.UtcNow;
            if ((nowInk - _lastInkSampleAt).TotalMilliseconds < InkThrottleMs)
            {
                e.Handled = true;
                return;
            }
            _lastInkSampleAt = nowInk;

            var (mx, my) = ToPdfCoords(pos, page);
            double pdfX = Math.Clamp(mx, 0, page.WidthPt);
            double pdfY = Math.Clamp(my, 0, page.HeightPt);
            ViewerVm.ContinueInkStroke(page, pdfX, pdfY);
            _livePolyline?.Points.Add(pos);
            e.Handled = true;
            return;
        }

        if (!_isSelecting)
        {
            Cursor = ViewerVm?.CurrentEditMode switch
            {
                EditMode.Ink => Cursors.Pen,
                EditMode.Note => Cursors.Cross,
                EditMode.Eraser => Cursors.Cross,
                _ => HitTestLink(page, pos) is not null ? Cursors.Hand : Cursors.IBeam,
            };
            return;
        }

        var (ex, ey) = ToPdfCoords(pos, page);
        double clampedX = Math.Clamp(ex, 0, page.WidthPt);
        double clampedY = Math.Clamp(ey, 0, page.HeightPt);
        _pendingDragPosPdf = new System.Windows.Point(clampedX, clampedY);

        var now = DateTime.UtcNow;
        if ((now - _lastDragExtractAt).TotalMilliseconds >= DragThrottleMs)
        {
            FireLiveDragExtract(page);
        }
        else
        {
            _dragTrailingTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DragThrottleMs) };
            _dragTrailingTimer.Tick -= OnDragTrailingTick;
            _dragTrailingTimer.Tick += OnDragTrailingTick;
            _dragTrailingTimer.Stop();
            _dragTrailingTimer.Start();
        }
    }

    private void OnDragTrailingTick(object? sender, EventArgs e)
    {
        _dragTrailingTimer?.Stop();
        if (!_isSelecting || DataContext is not PageViewModel page) return;
        FireLiveDragExtract(page);
    }

    private void FireLiveDragExtract(PageViewModel page)
    {
        if (ViewerVm is null) return;
        _lastDragExtractAt = DateTime.UtcNow;
        _liveDragCts?.Cancel();
        _liveDragCts = new CancellationTokenSource();
        LiveExtractAsync(page,
            (float)_selectionStartPdf.X, (float)_selectionStartPdf.Y,
            (float)_pendingDragPosPdf.X, (float)_pendingDragPosPdf.Y,
            _liveDragCts.Token);
    }

    private async void LiveExtractAsync(
        PageViewModel page,
        float startX, float startY,
        float endX, float endY,
        CancellationToken ct)
    {
        try
        {
            if (ViewerVm is null) return;
            await ViewerVm.ExtractSelectionAsync(page, startX, startY, endX, endY, ct);
        }
        catch (OperationCanceledException) { }
    }

    protected override async void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        // ── Ink ───────────────────────────────────────────────────────────────
        if (_isDrawingInk && DataContext is PageViewModel inkPage && ViewerVm is not null)
        {
            _isDrawingInk = false;
            ReleaseMouseCapture();

            var upPos = e.GetPosition(PageImage);
            var (fx, fy) = ToPdfCoords(upPos, inkPage);
            double finalX = Math.Clamp(fx, 0, inkPage.WidthPt);
            double finalY = Math.Clamp(fy, 0, inkPage.HeightPt);
            ViewerVm.ContinueInkStroke(inkPage, finalX, finalY);

            if (_livePolyline is not null)
            {
                LiveInkCanvas.Children.Remove(_livePolyline);
                _livePolyline = null;
            }

            await ViewerVm.CommitInkStrokeAsync(inkPage);
            e.Handled = true;
            return;
        }

        if (!_isSelecting || DataContext is not PageViewModel page || ViewerVm is null) return;

        _isSelecting = false;
        ReleaseMouseCapture();

        _dragTrailingTimer?.Stop();
        _liveDragCts?.Cancel();
        _liveDragCts = null;

        var pos = e.GetPosition(PageImage);
        var (ux, uy) = ToPdfCoords(pos, page);
        double finalEx = Math.Clamp(ux, 0, page.WidthPt);
        double finalEy = Math.Clamp(uy, 0, page.HeightPt);

        if (Math.Abs(finalEx - _selectionStartPdf.X) < 3 && Math.Abs(finalEy - _selectionStartPdf.Y) < 3)
        {
            page.SelectionHighlights = [];
            ViewerVm.ClearSelection();
            e.Handled = true;
            return;
        }

        _extractCts?.Cancel();
        _extractCts = new CancellationTokenSource();

        await ViewerVm.ExtractSelectionAsync(
            page,
            (float)_selectionStartPdf.X, (float)_selectionStartPdf.Y,
            (float)finalEx, (float)finalEy,
            _extractCts.Token);

        e.Handled = true;
    }
}