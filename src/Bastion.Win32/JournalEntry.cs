using Bastion.Core;

namespace Bastion.Win32;

/// <summary>
/// One write-ahead journal row: everything needed to force-restore a single window's
/// pre-management placement, per DESIGN.md §3.7's exact entry shape — "window → workspace →
/// pre-management <c>WINDOWPLACEMENT</c> → corner-preference state" (GitHub issue #8).
/// </summary>
/// <param name="HwndValue">
/// The window's raw <c>HWND</c> numeric value at journal-write time, widened to <see cref="long"/>
/// (System.Text.Json has no built-in <c>IntPtr</c>/<c>nint</c> converter — see
/// https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/supported-types#unsupported-types
/// — so the adapter boundary converts explicitly; see <see cref="JournalEntryCapture"/> and
/// <see cref="HwndJournalRestorer"/>). <b>Honesty note (DESIGN.md §9's HWND-recycling row):</b> a
/// value read back from a journal written before a crash cannot be trusted to still name the same
/// window — the owning process may have exited, or a new unrelated window may have reused the
/// numeric value. <see cref="ProcessId"/> exists specifically so a restorer can catch this rather
/// than blindly calling <c>SetWindowPlacement</c> on whatever this number happens to resolve to now
/// (mirroring how <c>WindowRegistry.TryGetHwnd</c> already revalidates PID before trusting a cached
/// HWND).
/// </param>
/// <param name="ProcessId">
/// The process id that owned the window at journal-write time (<c>GetWindowThreadProcessId</c>,
/// already read via <see cref="IWindowProcessIdReader"/> elsewhere in this assembly). The restore
/// path re-reads the <em>current</em> owning PID for <see cref="HwndValue"/> and refuses to restore
/// on a mismatch — see <paramref name="HwndValue"/>'s remarks.
/// </param>
/// <param name="Workspace">
/// Which (Bastion-owned) workspace this window belonged to. v0.1 has only
/// <see cref="Bastion.Core.WorkspaceKey.Default"/> in play (single workspace per monitor, DESIGN.md
/// §12) — this field exists so the v0.2 multi-workspace journal (GitHub issue #15) extends this
/// same entry shape rather than needing a new one; this issue does not implement multi-workspace
/// selection.
/// </param>
/// <param name="PreManagementPlacement">The window's <c>WINDOWPLACEMENT</c> as it was immediately before Bastion touched it.</param>
/// <param name="Identity">
/// The window's best-resolved <see cref="WindowIdentity"/> at journal-write time — not load-bearing
/// for the restore mechanics themselves (those key on <paramref name="HwndValue"/>/
/// <paramref name="ProcessId"/>), but valuable for <c>bastionc restore-windows</c>'s own console
/// output and any future <c>bastion doctor</c>/watchdog diagnostics (DESIGN.md §9) that want to say
/// <i>which</i> window an entry refers to.
/// </param>
/// <param name="CornerPreference">Schema slot for GitHub issue #34 — see <see cref="JournalCornerPreference"/>'s own remarks. Always <see cref="JournalCornerPreference.Unset"/> as of this issue.</param>
/// <param name="JournaledAtUtc">When this entry was written — diagnostic only, not consulted by any restore decision in this issue.</param>
internal sealed record JournalEntry(
    long HwndValue,
    uint ProcessId,
    WorkspaceKey Workspace,
    JournalWindowPlacement PreManagementPlacement,
    WindowIdentity Identity,
    JournalCornerPreference CornerPreference,
    DateTimeOffset JournaledAtUtc);
