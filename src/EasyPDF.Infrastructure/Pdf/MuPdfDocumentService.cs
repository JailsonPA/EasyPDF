using EasyPDF.Core;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging;
using MuPDFCore;
using MuPDFCore.StructuredText;
using System.Buffers;
using System.Security.Cryptography;

namespace EasyPDF.Infrastructure.Pdf;

public sealed class MuPdfDocumentService : IPdfDocumentService
{
    private readonly ILogger<MuPdfDocumentService> _logger;
    private readonly IPageCache _cache;
    private readonly MuPdfDispatcher _dispatcher;

    // Single mutex serializes all native MuPDF calls on this document. MuPDFCore is not
    // thread-safe for concurrent operations on the same MuPDFDocument, so even read-style
    // operations (rendering, text extraction, search) must run one at a time.
    private readonly object _docLock = new();
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(5);

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

    internal T UseDocument<T>(Func<MuPDFDocument, T> callback)
    {
        lock (_docLock)
        {
            if (_muDoc is null)
                throw new InvalidOperationException("No PDF document is currently open.");
            return callback(_muDoc);
        }
    }

    private void OpenCore(string filePath, string? password)
    {
        if (!Monitor.TryEnter(_docLock, AcquireTimeout))
            throw new TimeoutException("Could not acquire document lock to open PDF — an operation may be stuck.");

        try
        {
            CloseCore();

            _logger.LogInformation("Opening PDF: {Path}", filePath);
            _context = new MuPDFContext();
          
            _context.AntiAliasing        = 8;
            _context.TextAntiAliasing    = 8;
            _context.GraphicsAntiAliasing = 8;
            _muDoc   = new MuPDFDocument(_context, filePath);

          
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
                IsRestricted:    _muDoc.RestrictionState == RestrictionState.Restricted)
            {
                ContentHash  = TryComputeContentHash(filePath),
                HasTextLayer = ProbeTextLayer(),
            };

            _logger.LogInformation("Opened {Pages} pages", CurrentDocument.PageCount);
        }
        catch
        {
          
            CloseCore();
            throw;
        }
        finally { Monitor.Exit(_docLock); }
    }

    public void Close()
    {
        if (!Monitor.TryEnter(_docLock, AcquireTimeout))
            throw new TimeoutException("Could not acquire document lock to close PDF — an operation may be stuck.");
        try { CloseCore(); }
        finally { Monitor.Exit(_docLock); }
    }

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

    /// Samples up to the first 5 pages and counts extractable characters in their structured
    /// text. Returns true if any sampled page has more than a handful of characters — anything
    /// less is likely just a page number or stray glyph, not a real text layer. The user-visible
    /// effect: scanned PDFs (image-only) and Canva-style vector-outlined PDFs both come back
    /// false here, allowing the UI to show a "no text layer" banner instead of silently failing
    /// when the user tries to select/search/highlight.
    private bool ProbeTextLayer()
    {
        if (_muDoc is null) return false;

        const int MinCharsToConsiderTextual = 20;
        int pagesToSample = Math.Min(_muDoc.Pages.Count, 5);

        for (int i = 0; i < pagesToSample; i++)
        {
            try
            {
                using var stp = _muDoc.GetStructuredTextPage(i, false, StructuredTextFlags.None);
                int chars = 0;
                foreach (var block in stp)
                {
                    foreach (var line in block)
                    {
                        chars += line.Count;
                        if (chars > MinCharsToConsiderTextual) return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Text-layer probe failed on page {Page} — treating as missing", i);
            }
        }
        return false;
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

    /// SHA-256 of up to the first 1 MB of the file. Stable across renames/moves and cheap
    /// enough to compute on every Open (~5–15 ms on SSD). Returns null on I/O failure —
    /// callers fall back to path-only matching.
    private static string? TryComputeContentHash(string filePath)
    {
        try
        {
            const int sampleSize = 1024 * 1024;
            using var stream = File.OpenRead(filePath);
            int toRead = (int)Math.Min(sampleSize, stream.Length);
            if (toRead <= 0) return null;

            var rented = ArrayPool<byte>.Shared.Rent(toRead);
            try
            {
                int read = stream.Read(rented, 0, toRead);
                Span<byte> hash = stackalloc byte[32];
                SHA256.HashData(rented.AsSpan(0, read), hash);
                return Convert.ToHexString(hash);
            }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }
        catch { return null; }
    }

    private static string ReadPdfVersion(string filePath)
    {
        try
        {
            Span<byte> buf = stackalloc byte[1024];
            using var f = File.OpenRead(filePath);
            int read = f.Read(buf);
            ReadOnlySpan<byte> data = buf[..read];

            // Search for "%PDF-" anywhere in the first KB. Some PDFs carry a UTF-8 BOM, a
            // signed-document wrapper, or other prefix bytes before the canonical header.
            int markerIdx = data.IndexOf("%PDF-"u8);
            if (markerIdx < 0) return "Unknown";

            var afterMarker = data[(markerIdx + 5)..];
            int delimIdx = afterMarker.IndexOfAny((byte)'\r', (byte)'\n', (byte)' ');
            var versionBytes = delimIdx >= 0
                ? afterMarker[..delimIdx]
                : afterMarker[..Math.Min(8, afterMarker.Length)];
            return "PDF " + System.Text.Encoding.ASCII.GetString(versionBytes).Trim();
        }
        catch { }
        return "Unknown";
    }

    public async ValueTask DisposeAsync()
    {
        await _dispatcher.RunAsync(Close);
    }
}
