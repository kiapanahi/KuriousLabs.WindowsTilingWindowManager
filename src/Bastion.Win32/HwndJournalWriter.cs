using System.Diagnostics.CodeAnalysis;

namespace Bastion.Win32;

/// <summary>
/// Enforces DESIGN.md §3.7's write-ahead ordering: "the HWND journal entry ... is flushed ...
/// <i>before</i> any hide call is issued." (GitHub issue #8.)
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope call (see the issue's own design guidance for why this is stated explicitly): this
/// issue does not wire a real "hide a window" call site.</b> Bastion-owned workspaces — the actual
/// hide/move-away operation this writer's ordering contract protects — are GitHub issue #15 (v0.2,
/// DESIGN.md §12), not yet built. There is therefore no live production caller of
/// <see cref="RecordThenActAsync"/> as of this change; this type <em>is</em> the deliverable this
/// issue's acceptance criteria asks for — the journal-writer component and its ordering
/// <em>contract</em> — verified by <c>HwndJournalWriterTests</c>'s write-before-hide test against a
/// fake <see cref="IHwndJournalStore"/> that can prove the ordering rather than merely assert it by
/// construction. Issue #15's Workspace Manager is the intended future caller:
/// <c>writer.RecordThenActAsync(entry, ct => system.HideAsync(hwnd, ct))</c> per window being hidden.
/// </para>
/// <para>
/// <b>The ordering is structural, not a documentation convention the caller must remember.</b>
/// <paramref name="action"/> — the Win32 hide/move-away call — is passed <em>in</em> to
/// <see cref="RecordThenActAsync"/> as a continuation rather than being something the caller invokes
/// itself afterward; there is no way to reach <paramref name="action"/> without going through this
/// method, and this method never invokes it until the journal write it awaited has completed. A
/// caller cannot "forget to await the journal write first" the way it could with two separately
/// callable methods.
/// </para>
/// <para>
/// <b>Read-modify-write, not "caller hands in the whole document."</b> This keeps the API usable by
/// a future one-window-at-a-time hide loop (issue #15's likely shape) without that caller having to
/// hand-manage <see cref="JournalDocument"/> state itself. <see cref="IHwndJournalStore"/> is
/// documented not thread-safe / sequential-only (matching this type's own single-caller
/// expectation), so no lock is taken around the read-modify-write here.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today; intended to be called by " +
        "GitHub issue #15's Workspace Manager once Bastion-owned workspaces exist. Same documented " +
        "CA1812 false-positive shape as PlacementExecutor/Coalescer/WindowSystemAdapter.")]
internal sealed class HwndJournalWriter(IHwndJournalStore store)
{
    /// <summary>
    /// Appends <paramref name="entry"/> to the journal, marks it dirty, durably writes it, and
    /// <em>only then</em> invokes <paramref name="action"/> — the write-ahead ordering contract
    /// this type exists to provide. If the write throws, <paramref name="action"/> is never
    /// invoked at all (a failed journal write must not be followed by an unrecorded hide).
    /// </summary>
    public async Task RecordThenActAsync(JournalEntry entry, Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(action);

        JournalDocument current = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        JournalDocument updated = current with { Dirty = true, Entries = current.Entries.Add(entry) };
        await store.WriteAsync(updated, cancellationToken).ConfigureAwait(false);

        await action(cancellationToken).ConfigureAwait(false);
    }
}
