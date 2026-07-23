namespace Bastion.Win32;

/// <summary>
/// DESIGN.md §3.3's manageability filter predicate, verbatim: "<c>hwnd == GetAncestor(GA_ROOT)</c>;
/// <c>IsWindowVisible</c>; <c>DWMWA_CLOAKED == 0</c> ...; exstyle lacks <c>WS_EX_TOOLWINDOW</c>
/// unless <c>WS_EX_APPWINDOW</c>; <c>GetWindow(GW_OWNER) == NULL</c> unless
/// <c>WS_EX_APPWINDOW</c>; skip <c>WS_EX_NOACTIVATE</c>, empty rects, and
/// <c>GetShellWindow()</c>." Extracted as a pure predicate over <see cref="WindowManageabilityInfo"/>
/// — no live HWND, hook, or window required to exercise it — matching the "extract a small,
/// directly-callable, pure predicate" pattern <c>WinEventFilter</c> established for the WinEvent
/// pump (GitHub issue #1).
/// </summary>
/// <remarks>
/// This predicate decides <em>admission</em> only (DESIGN.md §5: "Registry runs the manageability
/// filter + rules"). It says nothing about whether an already-registered window should be
/// forgotten — <see cref="WindowRegistry"/> purges only on <c>EVENT_OBJECT_DESTROY</c>, per
/// DESIGN.md §3.3's "entries are purged only on <c>EVENT_OBJECT_DESTROY</c> — never by
/// <c>IsWindow</c> polling." In particular, a window that fails this predicate today because it
/// currently reads cloaked is exactly the DESIGN.md §3.3 case of "any nonzero cloak value → keep
/// tracked, never tile, never forget" for a window that was <em>already</em> registered before
/// going cloaked; this predicate's job is only to gate the moment of first admission (called again
/// on every SHOW/UNCLOAKED/NAMECHANGE per DESIGN.md §5, so a window that fails here today can still
/// be admitted on a later call once its state changes) — it is <see cref="WindowRegistry"/>'s
/// caller's responsibility (DESIGN.md §5's Reconciler, GitHub issue #4) never to re-run this
/// predicate as a reason to evict an entry that already exists.
/// </remarks>
internal static class WindowManageabilityFilter
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="info"/> describes a window that should be
    /// admitted into the <see cref="WindowRegistry"/>, per DESIGN.md §3.3's filter predicate.
    /// </summary>
    public static bool IsManageable(WindowManageabilityInfo info, WindowClassBlocklist blocklist)
    {
        if (!info.IsRootWindow)
        {
            return false;
        }

        if (!info.IsVisible)
        {
            return false;
        }

        // DWMWA_CLOAKED == 0 — see this type's remarks for why a cloaked window failing here does
        // not evict an already-registered entry; it only blocks first admission.
        if (info.IsCloaked)
        {
            return false;
        }

        // exstyle lacks WS_EX_TOOLWINDOW unless WS_EX_APPWINDOW.
        if (info.HasToolWindowStyle && !info.HasAppWindowStyle)
        {
            return false;
        }

        // GetWindow(GW_OWNER) == NULL unless WS_EX_APPWINDOW.
        if (info.HasOwner && !info.HasAppWindowStyle)
        {
            return false;
        }

        if (info.HasNoActivateStyle)
        {
            return false;
        }

        if (info.HasEmptyRect)
        {
            return false;
        }

        if (info.IsShellWindow)
        {
            return false;
        }

        // "The class-name blocklist ... is user-editable config, not code" — DESIGN.md §3.3.
        if (blocklist.Contains(info.ClassName))
        {
            return false;
        }

        return true;
    }
}
