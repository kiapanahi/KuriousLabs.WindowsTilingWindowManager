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
/// <b>Preserve the first entry per window (Codex review finding on this PR).</b> A window can be
/// hidden more than once in a session (e.g. switched away from, restored, switched away from
/// again). Appending unconditionally on every call would record the window's <em>Bastion-managed</em>
/// placement at the second hide as if it were "pre-management" state — <see cref="HwndJournalRestorer"/>
/// replays entries in journal order, so a crash between the second hide and its own restore would
/// apply the true original placement and then immediately overwrite it with the managed one before
/// dropping both rows, defeating the entire point of "pre-management." <see cref="RecordThenActAsync"/>
/// therefore only appends when no entry already exists for this exact window (matched on
/// <see cref="JournalEntry.HwndValue"/> and <see cref="JournalEntry.ProcessId"/> together, the same
/// pairing <see cref="HwndJournalRestorer"/> uses for its own HWND-recycling recheck) — the entry
/// already on disk is left untouched, since it already correctly captures the window's true
/// pre-management placement, and the hide action still proceeds normally.
/// </para>
/// <para>
/// <b>Cross-process serialization (Codex review finding on this PR).</b> The whole read-decide-write
/// journal step <em>and</em> the hide action are performed while holding <paramref name="journalLock"/> —
/// not just the file I/O. Without this, <c>bastionc restore-windows</c> running concurrently in a
/// different process could read the just-written entry and force-restore it (clearing the entry as
/// "handled") <em>before</em> this method's own hide action has actually run, leaving the window
/// hidden immediately afterward with no journal row left to recover it. Holding the lock across the
/// entire method — including the hide call — means a concurrent restorer either runs to completion
/// entirely before this method starts (seeing no entry to race against) or blocks until this whole
/// "journal, then hide" sequence has finished (at which point the window really is hidden, and
/// restoring its entry is correct). See <see cref="HwndJournalLock"/>'s own remarks for why this is
/// a named <see cref="Semaphore"/>, not a <see cref="Mutex"/>.
/// </para>
/// <para>
/// <b>Read-modify-write, not "caller hands in the whole document."</b> This keeps the API usable by
/// a future one-window-at-a-time hide loop (issue #15's likely shape) without that caller having to
/// hand-manage <see cref="JournalDocument"/> state itself.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today; intended to be called by " +
        "GitHub issue #15's Workspace Manager once Bastion-owned workspaces exist. Same documented " +
        "CA1812 false-positive shape as PlacementExecutor/Coalescer/WindowSystemAdapter.")]
internal sealed class HwndJournalWriter(IHwndJournalStore store, IHwndJournalLock journalLock)
{
    /// <summary>
    /// If no entry already exists for this exact window, appends <paramref name="entry"/> to the
    /// journal, marks it dirty, and durably writes it; either way, <em>only then</em> invokes
    /// <paramref name="action"/> — the write-ahead ordering contract this type exists to provide.
    /// The whole operation, including <paramref name="action"/>, runs under the cross-process
    /// journal lock (see this type's remarks). If a journal write throws, <paramref name="action"/>
    /// is never invoked at all (a failed journal write must not be followed by an unrecorded hide).
    /// </summary>
    public async Task RecordThenActAsync(JournalEntry entry, Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(action);

        using (await journalLock.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            JournalDocument current = await store.ReadAsync(cancellationToken).ConfigureAwait(false);

            bool alreadyJournaled = current.Entries.Any(
                e => e.HwndValue == entry.HwndValue && e.ProcessId == entry.ProcessId);
            if (!alreadyJournaled)
            {
                JournalDocument updated = current with { Dirty = true, Entries = current.Entries.Add(entry) };
                await store.WriteAsync(updated, cancellationToken).ConfigureAwait(false);
            }

            await action(cancellationToken).ConfigureAwait(false);
        }
    }
}
