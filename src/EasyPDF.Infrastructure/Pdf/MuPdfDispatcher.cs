using System.Collections.Concurrent;

namespace EasyPDF.Infrastructure.Pdf;

/// <summary>
/// A fixed pool of background threads each with a 32 MB stack.
///
/// Why this exists:
///   MuPDF's content-stream interpreter recurses deeply when processing PDFs that
///   contain nested XObjects, form streams, or media annotations. The default .NET
///   thread-pool stack is 1 MB, which is not enough — the CRT detects the overflow
///   via its stack-cookie check and raises STATUS_STACK_BUFFER_OVERRUN (0xC0000409),
///   terminating the process before any managed exception handler can fire.
///
///   Every MuPDF native call (open, render, bounds lookup, dispose) must go through
///   this dispatcher so that it always executes on a 32 MB stack thread.
///
/// Concurrency model:
///   All work items share a single FIFO queue consumed by <paramref name="workerCount"/>
///   threads. Callers use their own SemaphoreSlim to limit how many items they enqueue
///   concurrently (see MuPdfRenderService). The dispatcher itself imposes no additional
///   throttling — it just guarantees the right stack size.
/// </summary>
public sealed class MuPdfDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();

    public MuPdfDispatcher(int workerCount = 6, int stackBytes = 32 * 1024 * 1024)
    {
        for (int i = 0; i < workerCount; i++)
        {
            var t = new Thread(WorkerLoop, stackBytes)
            {
                IsBackground = true,
                Name = $"MuPDF-Worker-{i + 1}"
            };
            t.Start();
        }
    }

    /// <summary>
    /// Enqueues <paramref name="work"/> to run on a large-stack worker thread and
    /// returns a Task that completes (or faults/cancels) when the work finishes.
    /// </summary>
    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        _queue.Add(() =>
        {
            if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
            try   { tcs.TrySetResult(work()); }
            catch (OperationCanceledException oce) { tcs.TrySetCanceled(oce.CancellationToken); }
            catch (Exception ex)                   { tcs.TrySetException(ex); }
        });

        // Register AFTER enqueue so the worker's own check handles the case where
        // cancellation happens while the item is still waiting in the queue.
        if (ct.CanBeCanceled)
            ct.Register(static s => ((TaskCompletionSource<T>)s!).TrySetCanceled(), tcs);

        return tcs.Task;
    }

    public Task RunAsync(Action work, CancellationToken ct = default) =>
        RunAsync<bool>(() => { work(); return true; }, ct);

    private void WorkerLoop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
            action();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _queue.Dispose();
    }
}
