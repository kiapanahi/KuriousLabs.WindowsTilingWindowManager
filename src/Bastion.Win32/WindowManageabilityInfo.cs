namespace Bastion.Win32;

/// <summary>
/// The fields of a live window's state that <see cref="WindowManageabilityFilter"/> needs to
/// decide manageability — DESIGN.md §3.3's filter predicate, expressed as a flat, HWND-free DTO
/// so the predicate itself is unit-testable against a synthetic value (see
/// <c>WindowManageabilityFilterTests</c>) rather than only reachable via a real window. Mirrors
/// the "extract a small pure predicate for unit-testability" pattern <c>WinEventFilter</c>/
/// <c>WinEventRootNormalizer</c> established for the WinEvent pump (GitHub issue #1).
/// </summary>
/// <remarks>
/// Gathering this DTO from a live HWND is <see cref="WindowManageabilityInfoReader"/>'s job — this
/// type has no knowledge of how its fields were produced. Every field is a primitive, so
/// compiler-generated record equality is safe here (no collection-typed field to trip the
/// reference-equality pitfall records have for <c>List&lt;T&gt;</c>/array members).
/// </remarks>
/// <param name="IsRootWindow">
/// <see langword="true"/> if <c>hwnd == GetAncestor(hwnd, GA_ROOT)</c> — DESIGN.md §3.3's first
/// filter condition. A WinEvent's target may be a child/owned window; only the root is ever a
/// registry candidate.
/// </param>
/// <param name="IsVisible"><c>IsWindowVisible</c>'s result.</param>
/// <param name="IsCloaked">
/// <see langword="true"/> if <c>DWMWA_CLOAKED</c> currently reads nonzero. Per DESIGN.md §3.3/§4,
/// the specific cloak <em>reason</em> is deliberately never inspected beyond this zero/nonzero
/// read — see <see cref="WindowManageabilityInfoReader"/>'s remarks for the exact citation.
/// </param>
/// <param name="HasOwner"><c>GetWindow(hwnd, GW_OWNER) != NULL</c>.</param>
/// <param name="HasToolWindowStyle">Extended style includes <c>WS_EX_TOOLWINDOW</c>.</param>
/// <param name="HasAppWindowStyle">Extended style includes <c>WS_EX_APPWINDOW</c>.</param>
/// <param name="HasNoActivateStyle">Extended style includes <c>WS_EX_NOACTIVATE</c>.</param>
/// <param name="HasEmptyRect">
/// <see langword="true"/> if the window's bounding rectangle has zero width or height (or could
/// not be read) — DESIGN.md §3.3's "skip ... empty rects" condition, catching the
/// zero-sized-hidden-window pattern Electron/Chromium windows exhibit at <c>CREATE</c> (§9's
/// Electron/Chromium row).
/// </param>
/// <param name="IsShellWindow"><c>hwnd == GetShellWindow()</c>.</param>
/// <param name="ClassName">
/// The window's class name, checked against the injected <see cref="WindowClassBlocklist"/>
/// (DESIGN.md §3.3's "class name blocklist ... is user-editable config, not code").
/// </param>
internal readonly record struct WindowManageabilityInfo(
    bool IsRootWindow,
    bool IsVisible,
    bool IsCloaked,
    bool HasOwner,
    bool HasToolWindowStyle,
    bool HasAppWindowStyle,
    bool HasNoActivateStyle,
    bool HasEmptyRect,
    bool IsShellWindow,
    string ClassName);
