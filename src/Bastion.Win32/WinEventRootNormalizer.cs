using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// Normalizes a WinEvent's target window to its root ancestor for DESIGN.md §3.1's "filter,
/// normalize (<c>GetAncestor(GA_ROOT)</c>), enqueue" callback contract, without ever discarding a
/// perfectly good window identity because of a documented-but-unspecified <c>GetAncestor</c>
/// failure. Extracted out of <see cref="WinEventPumpService"/>'s native callback as a small,
/// directly-callable, pure function — no live HWND, hook, or window is required to exercise it —
/// so the null-ancestor fallback is independently unit-testable (see
/// <c>WinEventRootNormalizerTests</c>) rather than only reachable via a Tier 3 real-window
/// integration test.
/// </summary>
internal static class WinEventRootNormalizer
{
    /// <summary>
    /// Returns <paramref name="rootAncestor"/>, unless it is <see cref="HWND.IsNull"/>, in which
    /// case returns <paramref name="originalHwnd"/> instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetAncestor</c> is documented only to return NULL "if the function fails" — no further
    /// detail on which failures (verified against
    /// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getancestor; the
    /// desktop-window special case its docs also call out does not apply here, since every caller
    /// of this method — <see cref="WinEventPumpService"/>'s <c>OnWinEvent</c> — already filtered
    /// out a null <paramref name="originalHwnd"/> via <see cref="WinEventFilter.IsRelevantWindowEvent"/>).
    /// One concrete way that documented-but-unspecified failure manifests in practice: DESIGN.md
    /// §3.1's hooks are all <c>WINEVENT_OUTOFCONTEXT</c>, so the callback for a window's own
    /// <c>EVENT_OBJECT_DESTROY</c> can run after the window is already gone, and a
    /// <c>GetAncestor(GA_ROOT)</c> query against it then fails and returns NULL — even though
    /// <paramref name="originalHwnd"/> is a perfectly good identity for whichever window the
    /// event was actually about.
    /// </para>
    /// <para>
    /// Deliberately a general "did GetAncestor fail" fallback, not a branch on
    /// <c>eventId == EVENT_OBJECT_DESTROY</c>: the failure above is a property of querying a
    /// window that is already gone, not of the DESTROY event specifically, so any other event
    /// type that raced the same way (e.g. a very late out-of-context delivery for some other
    /// event on a window destroyed in the meantime) gets the same correct treatment without a
    /// second special case to maintain.
    /// </para>
    /// <para>
    /// Note for DESIGN.md §3.3's future Window Registry (GitHub issue #3): its admission filter
    /// only ever tracks a window for which <c>hwnd == GetAncestor(GA_ROOT)</c> held at admission
    /// time — i.e. root windows. A DESTROY event for a non-root window was therefore never a
    /// registry entry, so preserving its identity here doesn't change what the registry purges.
    /// What this fix actually protects is the (common, load-bearing) case where the destroyed
    /// window <em>was</em> a tracked root window: that is exactly the window whose purge-on-destroy
    /// this normalization makes reachable at all. It also keeps the future Coalescer's (GitHub
    /// issue #2) per-HWND coalescing from conflating unrelated null-root events under one
    /// HWND-zero bucket, regardless of whether the window involved was ever root.
    /// </para>
    /// </remarks>
    public static HWND NormalizeRoot(HWND originalHwnd, HWND rootAncestor) =>
        rootAncestor.IsNull ? originalHwnd : rootAncestor;
}
