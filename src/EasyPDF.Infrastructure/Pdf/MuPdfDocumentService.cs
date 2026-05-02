using EasyPDF.Core;
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
    private static readonly TimeSpan WriteLockTimeout = TimeSpan.FromSeconds(5);

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

    public async Task<PdfDocument> OpenAsync(string filePath, string? password = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            throw new FileNotFoundException("PDF file not found.", filePath);

        await _dispatcher.RunAsync(() => OpenCore(filePath, password), ct);
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

    private void OpenCore(string filePath, string? password)
    {
        if (!_docLock.TryEnterWriteLock(WriteLockTimeout))
            throw new TimeoutException("Could not acquire write lock to open PDF — a render operation may be stuck.");

        try
        {
            CloseCore();

            _logger.LogInformation("Opening PDF: {Path}", filePath);
            _context = new MuPDFContext();
            // Maximum anti-aliasing (0–8 scale). MuPDF default is lower and produces
            // visibly jaggy text and edges — setting all three to 8 eliminates this.
            _context.AntiAliasing        = 8;
            _context.TextAntiAliasing    = 8;
            _context.GraphicsAntiAliasing = 8;
            _muDoc   = new MuPDFDocument(_context, filePath);

            // Handle password-protected PDFs.
            // MuPDFDocument opens even for encrypted files; we must call TryUnlock
            // before accessing pages, otherwise BuildPageList will produce corrupt data.
            if (_muDoc.EncryptionState == EncryptionState.Encrypted)
            {
                bool unlocked = !string.IsNullOrEmpty(password) && _muDoc.TryUnlock(password);
                if (!unlocked)
                    throw new PdfPasswordRequiredException(isWrongPassword: !string.IsNullOrEmpty(password));
            }

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
                TableOfContents: toc,
                PdfVersion:      ReadPdfVersion(filePath),
                IsEncrypted:     _muDoc.EncryptionState == EncryptionState.Unlocked,
                IsRestricted:    _muDoc.RestrictionState == RestrictionState.Restricted);

            _logger.LogInformation("Opened {Pages} pages", CurrentDocument.PageCount);
        }
        catch
        {
            // CloseCore disposes both _context and _muDoc, leaving the service clean
            // so the next open attempt can succeed.
            CloseCore();
            throw;
        }
        finally { _docLock.ExitWriteLock(); }
    }

    public void Close()
    {
        if (!_docLock.TryEnterWriteLock(WriteLockTimeout))
            throw new TimeoutException("Could not acquire write lock to close PDF — a render operation may be stuck.");
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
            var page   = _muDoc.Pages[i];
            var bounds = page.Bounds;
            var links  = ExtractLinks(page, bounds.X0, bounds.Y0);
            list.Add(new PdfPageInfo(i, bounds.X1 - bounds.X0, bounds.Y1 - bounds.Y0) { Links = links });
        }
        return list;
    }

    private static IReadOnlyList<PdfLink> ExtractLinks(MuPDFPage page, float originX, float originY)
    {
        try
        {
            using var muLinks = page.Links;
            if (muLinks.Count == 0) return [];

            var result = new List<PdfLink>(muLinks.Count);
            for (int j = 0; j < muLinks.Count; j++)
            {
                var link = muLinks[j];
                var a    = link.ActiveArea;
                var area = new PdfRect(a.X0 - originX, a.Y0 - originY,
                                       a.X1 - a.X0,    a.Y1 - a.Y0);
                if (area.Width <= 0 || area.Height <= 0) continue;

                // PageNumber is the overall (chapter-independent) 0-based page index.
                PdfLinkDestination? dest = link.Destination switch
                {
                    MuPDFInternalLinkDestination d => new PdfLinkDestination.Internal(d.PageNumber),
                    MuPDFExternalLinkDestination d when !string.IsNullOrEmpty(d.Uri)
                                                   => new PdfLinkDestination.External(d.Uri),
                    _                              => null,
                };
                if (dest is not null)
                    result.Add(new PdfLink(area, dest));
            }
            return result;
        }
        catch { return []; }
    }

    private static IReadOnlyList<TocEntry> BuildToc(MuPDFOutline outline)
    {
        var list = new List<TocEntry>();
        foreach (MuPDFOutlineItem item in outline)
        {
            var children = item.Children is not null
                ? BuildTocFromItems(item.Children, 1)
                : [];
            // PageNumber is the 0-based absolute page index in the document.
            // Page is chapter-relative and only differs for reflowable formats (EPUB).
            list.Add(new TocEntry(item.Title ?? string.Empty, item.PageNumber, 0, children));
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
            list.Add(new TocEntry(item.Title ?? string.Empty, item.PageNumber, level, children));
        }
        return list;
    }

    private static string ReadPdfVersion(string filePath)
    {
        try
        {
            Span<byte> buf = stackalloc byte[16];
            using var f = File.OpenRead(filePath);
            int read = f.Read(buf);
            string header = System.Text.Encoding.ASCII.GetString(buf[..read]);
            if (header.StartsWith("%PDF-", StringComparison.Ordinal))
            {
                int end = header.IndexOfAny(['\r', '\n', ' '], 5);
                string ver = end > 5 ? header[5..end] : header[5..];
                return "PDF " + ver.Trim();
            }
        }
        catch { }
        return "Unknown";
    }

    public async ValueTask DisposeAsync()
    {
        await _dispatcher.RunAsync(Close);
        _docLock.Dispose();
    }
}
