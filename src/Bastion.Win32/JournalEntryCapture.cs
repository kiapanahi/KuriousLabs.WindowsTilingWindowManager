using System.Diagnostics.CodeAnalysis;
using Bastion.Core;
using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// Builds one <see cref="JournalEntry"/> from a live <see cref="HWND"/> — the "capture" half of
/// GitHub issue #8's write-ahead journal (DESIGN.md §3.7), paired with <see cref="HwndJournalWriter"/>
/// for the "write, then act" ordering and <see cref="HwndJournalRestorer"/> for the reverse
/// direction. Takes <paramref name="workspace"/>/<paramref name="identity"/> as parameters rather
/// than resolving them itself: both are already known to whatever future caller (GitHub issue #15's
/// Workspace Manager) is about to hide a specific window out of a specific
/// <see cref="Bastion.Core.WorkspaceKey"/>-keyed workspace, via the Window Registry (GitHub issue
/// #3) it already consulted to decide to hide this window in the first place — re-deriving either
/// here would duplicate work this type has no way to do more cheaply or more correctly.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and intended to be " +
        "called by GitHub issue #15's Workspace Manager once Bastion-owned workspaces exist. Same " +
        "documented CA1812 false-positive shape as PlacementExecutor/Coalescer/WindowSystemAdapter.")]
internal sealed class JournalEntryCapture(
    IWindowProcessIdReader pidReader,
    IJournalPlacementSystem placementSystem,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Captures <paramref name="hwnd"/>'s current PID and <c>WINDOWPLACEMENT</c> into a
    /// <see cref="JournalEntry"/> tagged <see cref="JournalCornerPreference.Unset"/> (GitHub issue
    /// #34 not yet built). Returns <see langword="false"/> if either read fails (e.g. the window
    /// vanished between whatever produced <paramref name="hwnd"/> and this call) — a routine race,
    /// not exceptional.
    /// </summary>
    public bool TryCapture(HWND hwnd, WorkspaceKey workspace, WindowIdentity identity, [MaybeNullWhen(false)] out JournalEntry entry)
    {
        uint? pid = pidReader.TryReadProcessId(hwnd);
        if (pid is null || !placementSystem.TryCapturePlacement(hwnd, out JournalWindowPlacement placement))
        {
            entry = null;
            return false;
        }

        entry = new JournalEntry(
            (long)(IntPtr)hwnd,
            pid.Value,
            workspace,
            placement,
            identity,
            JournalCornerPreference.Unset,
            timeProvider.GetUtcNow());
        return true;
    }
}
