using System.Collections.Concurrent;

namespace EasyPDF.Infrastructure.Pdf;

///   All work items share a single FIFO queue consumed by <paramref name="workerCount"/>
public sealed class MuPdfDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();

    public MuPdfDispatcher(int workerCount = 6, int stackBytes = 8 * 1024 * 1024)
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

    /// Enqueues <paramref name="work"/> to run on a large-stack worker thread and
   
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
