using EasyPDF.Application;
using Xunit;

namespace EasyPDF.Tests;

/// <summary>
/// Tests for the undo/redo stack. Commands are no-op lambdas that just record their
/// invocation order — the focus is on stack mechanics, not the commands themselves.
/// </summary>
public sealed class AnnotationHistoryTests
{
    private static AnnotationCommand TracerCommand(List<string> log, string tag) =>
        new(
            Do:   _ => { log.Add($"do:{tag}");   return Task.CompletedTask; },
            Undo: _ => { log.Add($"undo:{tag}"); return Task.CompletedTask; });

    // ─── Empty state ──────────────────────────────────────────────────────────

    [Fact]
    public void NewHistory_CanUndoFalse_CanRedoFalse()
    {
        var h = new AnnotationHistory();
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public async Task UndoOnEmpty_IsNoOp()
    {
        var h = new AnnotationHistory();
        await h.UndoAsync();
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public async Task RedoOnEmpty_IsNoOp()
    {
        var h = new AnnotationHistory();
        await h.RedoAsync();
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    // ─── Execute → Undo → Redo cycle ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_RunsDoAndEnablesUndo()
    {
        var log = new List<string>();
        var h = new AnnotationHistory();

        await h.ExecuteAsync(TracerCommand(log, "A"));

        Assert.Equal(["do:A"], log);
        Assert.True(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public async Task UndoAsync_RunsUndoAndEnablesRedo()
    {
        var log = new List<string>();
        var h = new AnnotationHistory();

        await h.ExecuteAsync(TracerCommand(log, "A"));
        await h.UndoAsync();

        Assert.Equal(["do:A", "undo:A"], log);
        Assert.False(h.CanUndo);
        Assert.True(h.CanRedo);
    }

    [Fact]
    public async Task RedoAsync_ReplaysDoAndPushesBackToUndo()
    {
        var log = new List<string>();
        var h = new AnnotationHistory();

        await h.ExecuteAsync(TracerCommand(log, "A"));
        await h.UndoAsync();
        await h.RedoAsync();

        Assert.Equal(["do:A", "undo:A", "do:A"], log);
        Assert.True(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    // ─── Multiple commands maintain LIFO order ────────────────────────────────

    [Fact]
    public async Task MultipleExecutes_UndoIsLifo()
    {
        var log = new List<string>();
        var h = new AnnotationHistory();

        await h.ExecuteAsync(TracerCommand(log, "A"));
        await h.ExecuteAsync(TracerCommand(log, "B"));
        await h.ExecuteAsync(TracerCommand(log, "C"));
        await h.UndoAsync();
        await h.UndoAsync();

        // Undo runs in reverse order: C first, then B.
        Assert.Equal(["do:A", "do:B", "do:C", "undo:C", "undo:B"], log);
    }

    // ─── Redo stack is cleared by a new action ────────────────────────────────

    [Fact]
    public async Task NewExecute_ClearsRedoStack()
    {
        var log = new List<string>();
        var h = new AnnotationHistory();

        await h.ExecuteAsync(TracerCommand(log, "A"));
        await h.UndoAsync();
        Assert.True(h.CanRedo);

        // A new action after an undo branches history — the redo becomes unreachable.
        await h.ExecuteAsync(TracerCommand(log, "B"));
        Assert.False(h.CanRedo);
    }

    // ─── Clear ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_EmptiesBothStacks()
    {
        var h = new AnnotationHistory();

        await h.ExecuteAsync(TracerCommand([], "A"));
        await h.ExecuteAsync(TracerCommand([], "B"));
        await h.UndoAsync();

        Assert.True(h.CanUndo);
        Assert.True(h.CanRedo);

        h.Clear();

        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    // ─── StateChanged event ───────────────────────────────────────────────────

    [Fact]
    public async Task StateChanged_FiresOnExecute()
    {
        var h = new AnnotationHistory();
        int fires = 0;
        h.StateChanged += (_, _) => fires++;

        await h.ExecuteAsync(TracerCommand([], "A"));

        Assert.Equal(1, fires);
    }

    [Fact]
    public async Task StateChanged_FiresOnUndoAndRedo()
    {
        var h = new AnnotationHistory();
        await h.ExecuteAsync(TracerCommand([], "A"));

        int fires = 0;
        h.StateChanged += (_, _) => fires++;

        await h.UndoAsync();
        await h.RedoAsync();

        Assert.Equal(2, fires);
    }

    [Fact]
    public void Clear_OnEmptyHistory_DoesNotFireStateChanged()
    {
        var h = new AnnotationHistory();
        int fires = 0;
        h.StateChanged += (_, _) => fires++;

        h.Clear();

        Assert.Equal(0, fires);
    }

    // ─── Capacity cap ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UndoStack_CapsAtMaxHistorySize()
    {
        const int capacity = 100;  // MaxHistorySize constant in AnnotationHistory
        var h = new AnnotationHistory();

        // Push 120 commands — only the most-recent 100 should remain undoable.
        for (int i = 0; i < 120; i++)
            await h.ExecuteAsync(TracerCommand([], $"cmd{i}"));

        // Drain the entire undo stack.
        int undoCount = 0;
        while (h.CanUndo)
        {
            await h.UndoAsync();
            undoCount++;
        }

        Assert.Equal(capacity, undoCount);
    }
}
