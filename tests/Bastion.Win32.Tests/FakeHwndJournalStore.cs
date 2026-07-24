namespace Bastion.Win32.Tests;

/// <summary>
/// In-memory <see cref="IHwndJournalStore"/> fake for <see cref="HwndJournalWriterTests"/> and
/// <see cref="HwndJournalRestorerTests"/> — matches docs/engineering/testing.md §5's Tier-2 seam
/// shape. <see cref="GateNextWrite"/> lets a test prove <see cref="HwndJournalWriter"/>'s
/// write-before-act ordering rather than merely assert it by construction (the acceptance
/// criteria's "a fake adapter that would fail the test if hide is observed before the journal
/// write").
/// </summary>
internal sealed class FakeHwndJournalStore : IHwndJournalStore
{
    private JournalDocument _document = JournalDocument.Empty;
    private TaskCompletionSource? _writeGate;

    /// <summary>Every document actually passed to <see cref="WriteAsync"/>, in call order.</summary>
    public List<JournalDocument> WrittenDocuments { get; } = [];

    /// <summary>Invoked synchronously, inside <see cref="WriteAsync"/>, the moment a write is considered durable — before that call returns.</summary>
    public Action<JournalDocument>? OnWriteCompleted { get; set; }

    /// <summary>
    /// Makes the <em>next</em> <see cref="WriteAsync"/> call not complete until
    /// <paramref name="gate"/>'s task completes — lets a test observe "the write is still pending"
    /// as a distinct, provable state rather than assuming ordering from mere call sequence.
    /// </summary>
    public void GateNextWrite(TaskCompletionSource gate) => _writeGate = gate;

    /// <inheritdoc/>
    public Task<JournalDocument> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_document);

    /// <inheritdoc/>
    public async Task WriteAsync(JournalDocument document, CancellationToken cancellationToken = default)
    {
        if (_writeGate is { } gate)
        {
            _writeGate = null;
            await gate.Task.ConfigureAwait(false);
        }

        _document = document;
        WrittenDocuments.Add(document);
        OnWriteCompleted?.Invoke(document);
    }
}
