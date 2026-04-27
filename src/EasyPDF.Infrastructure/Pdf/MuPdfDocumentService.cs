using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging;
using MuPDFCore;

namespace EasyPDF.Infrastructure.Pdf;

/// <summary>
/// Opens PDF files using MuPDFCore and exposes their metadata.
///
/// Thread-safety contract:
///   - OpenCore / Close hold the WRITE lock — only one at a time, no concurrent readers.
///   - UseDocument holds a READ lock — multiple concurrent renders are allowed, but
///     Close() will block until all active reads finish before disposing the native document.
///   - This prevents the use-after-free crash that occurred when Close() disposed
///     MuPDFDocument while RenderCore() / SearchPage() were still executing it.
///
/// All native MuPDF calls are routed through MuPdfDispatcher so they always run on
/// a 32 MB stack thread, preventing STATUS_STACK_BUFFER_OVERRUN (0xC0000409) crashes
/// that occur when complex PDFs overflow the default 1 MB thread-pool stack.
/// </summary>
public sealed class MuPdfDocumentService : IPdfDocumentService
{
    private readonly ILogger<MuPdfDocumentService> _logger;
    private readonly IPageCache _cache;
    private readonly MuPdfDispatcher _dispatcher;

    // NoRecursion: acquiring a second lock on the same thread is a bug, not a feature.
    private readonly ReaderWriterLockSlim _docLock = new(LockRecursionPolicy.NoRecursion);

    // volatile so IsOpen reads outside the lock see the freshest value on all CPUs.
    private volatile MuPDFDocument? _muDoc;
    private MuPDFContext? _context;

    public PdfDocument? CurrentDocument { get; private set; }
    public bool IsOpen => _muDoc is not null;

    public MuPdfDocumentService(
        ILogger<MuPdfDocumentService> logger,
        IPageCache cache,
        MuPdfDispatcher dispatcher)
    {
        _logger = logger;
        _cache = cache;
        _dispatcher = dispatcher;
    }

    public async Task<PdfDocument> OpenAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            throw new FileNotFoundException("PDF file not found.", filePath);

        await _dispatcher.RunAsync(() => OpenCore(filePath), ct);
        return CurrentDocument!;
    }

    /// <summary>
    /// Gives callers (render service, search service) safe read-locked access to the
    /// native MuPDFDocument. The callback executes while the read lock is held, so
    /// Close() cannot dispose the document until every active callback returns.
    /// </summary>
    internal T UseDocument<T>(Func<MuPDFDocument, T> callback)
    {
        _docLock.EnterReadLock();
        try
        {
            if (_muDoc is null)
                throw new InvalidOperationException("No PDF document is currently open.");
            return callback(_muDoc);
        }
        finally { _docLock.ExitReadLock(); }
    }

    private void OpenCore(string filePath)
    {
        _docLock.EnterWriteLock();
        try
        {
            CloseCore();

            _logger.LogInformation("Opening PDF: {Path}", filePath);
            _context = new MuPDFContext();
            _muDoc   = new MuPDFDocument(_context, filePath);

            var pages = BuildPageList();
            var toc   = BuildToc(_muDoc.Outline);
            var info  = new FileInfo(filePath);

            CurrentDocument = new PdfDocument(
                FilePath:        filePath,
                FileName:        Path.GetFileName(filePath),
                PageCount:       _muDoc.Pages.Count,
                FileSizeBytes:   info.Length,
                OpenedAt:        DateTime.UtcNow,
                Pages:           pages,
                TableOfContents: toc);

            _logger.LogInformation("Opened {Pages} pages", CurrentDocument.PageCount);
        }
        catch
        {
            // If MuPDFDocument construction, BuildPageList, or BuildToc throws
            // (corrupted PDF, password-protected, unsupported format), _context may
            // have been allocated but _muDoc not yet set. CloseCore disposes both,
            // leaving the service in a clean closed state so the next open can succeed.
            CloseCore();
            throw;
        }
        finally { _docLock.ExitWriteLock(); }
    }

    public void Close()
    {
        _docLock.EnterWriteLock();
        try { CloseCore(); }
        finally { _docLock.ExitWriteLock(); }
    }

    // Must only be called while the write lock is held.
    private void CloseCore()
    {
        _cache.Clear();
        _muDoc?.Dispose();
        _muDoc = null;
        _context?.Dispose();
        _context = null;
        CurrentDocument = null;
    }

    private IReadOnlyList<PdfPageInfo> BuildPageList()
    {
        var list = new List<PdfPageInfo>(_muDoc!.Pages.Count);
        for (int i = 0; i < _muDoc.Pages.Count; i++)
        {
            var bounds = _muDoc.Pages[i].Bounds;
            list.Add(new PdfPageInfo(i, bounds.X1 - bounds.X0, bounds.Y1 - bounds.Y0));
        }
        return list;
    }

    private static IReadOnlyList<TocEntry> BuildToc(MuPDFOutline outline)
    {
        var list = new List<TocEntry>();
        foreach (MuPDFOutlineItem item in outline)
        {
            var children = item.Children is not null
                ? BuildTocFromItems(item.Children, 1)
                : [];
            list.Add(new TocEntry(item.Title ?? string.Empty, item.Page, 0, children));
        }
        return list;
    }

    private static IReadOnlyList<TocEntry> BuildTocFromItems(
        IEnumerable<MuPDFOutlineItem> items, int level)
    {
        var list = new List<TocEntry>();
        foreach (var item in items)
        {
            var children = item.Children is not null
                ? BuildTocFromItems(item.Children, level + 1)
                : (IReadOnlyList<TocEntry>)[];
            list.Add(new TocEntry(item.Title ?? string.Empty, item.Page, level, children));
        }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        await _dispatcher.RunAsync(Close);
        _docLock.Dispose();
    }
}
