namespace EasyPDF.Application;

/// <summary>
/// One reversible operation on the annotation list. Stored as a pair of async actions
/// (Do / Undo) rather than a class hierarchy — callers build commands inline from the
/// ViewModel's existing private "Apply" methods. Keeps each command self-contained;
/// the history class doesn't need to know about annotation types.
/// </summary>
public sealed record AnnotationCommand(
    Func<CancellationToken, Task> Do,
    Func<CancellationToken, Task> Undo);

/// <summary>
/// Bounded undo/redo stack for annotation mutations. Per-tab, cleared when the tab's
/// document changes (since IDs don't carry across documents — undoing a removal of a
/// previous doc's annotation would re-add into the wrong document).
/// </summary>
public sealed class AnnotationHistory
{
    // 100 actions is well past anything a human will undo in one session and keeps
    // memory bounded across long editing sessions.
    private const int MaxHistorySize = 100;

    // LinkedList is used as a deque: push at Head (AddFirst), trim from Tail (RemoveLast).
    private readonly LinkedList<AnnotationCommand> _undo = new();
    private readonly LinkedList<AnnotationCommand> _redo = new();

    public event EventHandler? StateChanged;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// Executes a new command (the user's action). Pushes it onto the undo stack and
    /// clears the redo stack — branching the history at this point.
    public async Task ExecuteAsync(AnnotationCommand cmd, CancellationToken ct = default)
    {
        await cmd.Do(ct);
        _undo.AddFirst(cmd);
        if (_undo.Count > MaxHistorySize) _undo.RemoveLast();
        _redo.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task UndoAsync(CancellationToken ct = default)
    {
        if (_undo.Count == 0) return;
        var cmd = _undo.First!.Value;
        _undo.RemoveFirst();
        await cmd.Undo(ct);
        _redo.AddFirst(cmd);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RedoAsync(CancellationToken ct = default)
    {
        if (_redo.Count == 0) return;
        var cmd = _redo.First!.Value;
        _redo.RemoveFirst();
        await cmd.Do(ct);
        _undo.AddFirst(cmd);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0) return;
        _undo.Clear();
        _redo.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
