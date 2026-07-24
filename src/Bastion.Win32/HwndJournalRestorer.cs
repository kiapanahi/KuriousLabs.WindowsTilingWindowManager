using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// Force-restores every window in the write-ahead journal (GitHub issue #8, DESIGN.md §3.7:
/// "<c>bastion restore-windows</c> force-restores everything even with the daemon dead; clean
/// shutdown restores all windows first"). The single implementation both <c>bastionc
/// restore-windows</c> (<c>Bastion.Cli</c>'s <c>Program.cs</c>, a different assembly with
/// <c>InternalsVisibleTo</c> access to this one) and the daemon-shutdown hook
/// (<see cref="JournalRestoreOnShutdownService"/>) call — "force-restore" has exactly one meaning
/// in this codebase, independent of which process invokes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>HWND-recycling defensiveness (DESIGN.md §9's honesty note, this issue's design guidance).</b>
/// A journal entry written before a crash, read back after a restart, cannot trust that
/// <see cref="JournalEntry.HwndValue"/> still refers to the same window — the owning process may
/// have exited, or a new unrelated window may have reused the numeric value. Every restore
/// re-reads the <em>current</em> owning PID for that HWND value and compares it against
/// <see cref="JournalEntry.ProcessId"/> before ever calling <c>SetWindowPlacement</c> — the same
/// live-PID recheck <c>WindowRegistry.TryGetHwnd</c> already performs for its own cached
/// HWND-to-<c>WindowId</c> mapping (GitHub issue #5's review-driven fix), applied here to a
/// value that additionally survived a process restart rather than merely an in-process cache.
/// </para>
/// <para>
/// <b>Dirty-flag / journal-entry retention on partial failure.</b> An entry whose window is simply
/// gone (<see cref="JournalRestoreOutcomeKind.SkippedWindowGone"/>) or whose HWND was recycled
/// (<see cref="JournalRestoreOutcomeKind.SkippedHwndRecycled"/>) is removed from the journal
/// regardless of outcome — there is nothing a future restore attempt could do differently for
/// either case, so retaining either entry would only accumulate permanent cruft. An entry whose
/// <c>SetWindowPlacement</c> call itself failed (<see cref="JournalRestoreOutcomeKind.Failed"/> —
/// e.g. a UIPI-blocked elevated window, DESIGN.md §3.6/§9) is <em>kept</em>, since a later retry
/// (the user closes the elevated app, or a future elevated-daemon mode reaches it) could still
/// succeed. <see cref="JournalDocument.Dirty"/> is cleared to <see langword="false"/> only once the
/// remaining entry list is empty — DESIGN.md's "clean shutdown restores all windows first" reading
/// of "clean" as "nothing outstanding," not merely "an attempt was made."
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and by Bastion.Cli's " +
        "Program.cs (a different assembly) for `bastionc restore-windows`; intended to also be " +
        "called from JournalRestoreOnShutdownService once Bastion.Daemon's composition root is " +
        "wired (GitHub issue #10). Same documented CA1812 false-positive shape as " +
        "PlacementExecutor/Coalescer/WindowSystemAdapter.")]
internal sealed class HwndJournalRestorer(IHwndJournalStore store, IJournalPlacementSystem placementSystem, IWindowProcessIdReader pidReader)
{
    /// <summary>
    /// Attempts every currently-journaled entry once, in journal order, and durably rewrites the
    /// journal to keep only the ones that still need a future retry (see this type's remarks).
    /// Returns one outcome per attempted entry, in the same order.
    /// </summary>
    public async Task<ImmutableArray<JournalRestoreOutcome>> RestoreAllAsync(CancellationToken cancellationToken = default)
    {
        JournalDocument document = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (document.Entries.IsEmpty)
        {
            return ImmutableArray<JournalRestoreOutcome>.Empty;
        }

        ImmutableArray<JournalRestoreOutcome>.Builder outcomes = ImmutableArray.CreateBuilder<JournalRestoreOutcome>(document.Entries.Length);
        ImmutableArray<JournalEntry>.Builder remaining = ImmutableArray.CreateBuilder<JournalEntry>();

        foreach (JournalEntry entry in document.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            JournalRestoreOutcome outcome = RestoreOne(entry);
            outcomes.Add(outcome);
            if (outcome.Kind == JournalRestoreOutcomeKind.Failed)
            {
                remaining.Add(entry);
            }
        }

        ImmutableArray<JournalEntry> remainingEntries = remaining.ToImmutable();
        await store.WriteAsync(
            new JournalDocument { Dirty = !remainingEntries.IsEmpty, Entries = remainingEntries },
            cancellationToken).ConfigureAwait(false);

        return outcomes.ToImmutable();
    }

    private JournalRestoreOutcome RestoreOne(JournalEntry entry)
    {
        // CA2020: an unchecked long->IntPtr conversion no longer throws on overflow as of .NET 7,
        // so `checked` is the correct way to restore the old throw-on-overflow behavior for a value
        // parsed from a file this process does not fully trust (a hand-edited/corrupted journal).
        // On every architecture Bastion actually ships (x64/arm64 -- both 64-bit, DESIGN.md §10),
        // IntPtr and long are both 8 bytes, so this can never genuinely overflow for any value this
        // codebase itself ever wrote via JournalEntryCapture's own long<-IntPtr<-HWND capture; it
        // exists purely so a corrupted journal value throws loudly instead of silently truncating
        // to a different, wrong HWND that this process would then call SetWindowPlacement on.
        HWND hwnd;
        checked
        {
            hwnd = (HWND)(IntPtr)entry.HwndValue;
        }

        uint? currentPid = pidReader.TryReadProcessId(hwnd);
        if (currentPid is null)
        {
            return JournalRestoreOutcome.SkippedWindowGone(entry);
        }

        if (currentPid.Value != entry.ProcessId)
        {
            return JournalRestoreOutcome.SkippedHwndRecycled(entry);
        }

        PlacementCallResult result = placementSystem.ApplyWindowPlacement(hwnd, entry.PreManagementPlacement);
        return result.Success
            ? JournalRestoreOutcome.Restored(entry)
            : JournalRestoreOutcome.Failed(entry, result.ErrorCode);
    }
}
