using Bastion.Core;

namespace Bastion.Win32;

/// <summary>
/// Pure coordinate-space math for <see cref="PlacementExecutor"/> (DESIGN.md §3.6b-c) — deliberately
/// free of every Win32/CsWin32 type so it is unit-testable exactly like <c>Bastion.Layout</c>'s own
/// layout math, without a real window, matching the "extract a small pure predicate/function for
/// unit-testability" pattern <c>WinEventFilter</c>/<c>WinEventRootNormalizer</c> established for the
/// WinEvent pump (GitHub issue #1). Both operations take fresh per-window/per-batch readings as
/// plain parameters; neither reads or caches anything itself (DESIGN.md §3.6c: "never cached
/// per-class").
/// </summary>
internal static class PlacementCoordinateConverter
{
    /// <summary>
    /// Shifts <paramref name="desiredVisibleBounds"/> — what the Layout Engine wants the window's
    /// <em>visible</em> bounds to be — by the per-edge invisible-border delta between
    /// <paramref name="windowRect"/> (raw <c>GetWindowRect</c>, includes the invisible resize
    /// border) and <paramref name="frameBounds"/> (<c>DWMWA_EXTENDED_FRAME_BOUNDS</c>, visible
    /// bounds only), both freshly read from the same window (DESIGN.md §3.6c). The result is the
    /// <em>window</em> rect to actually hand to <c>SetWindowPos</c>/<c>DeferWindowPos</c>/
    /// <c>SetWindowPlacement</c> (all of which position the window rect, not the DWM frame bounds)
    /// so that, once the invisible border is applied by the system, the <em>visible</em> edges land
    /// exactly on <paramref name="desiredVisibleBounds"/>.
    /// </summary>
    /// <remarks>
    /// DOCUMENTED CONTRACT (verified against
    /// https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute and
    /// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getwindowrect):
    /// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> "retrieves the extended frame bounds rectangle in screen
    /// space," and <c>GetWindowRect</c>'s own remarks point here explicitly for "the visible window
    /// bounds, not including the invisible resize borders" — i.e. <paramref name="windowRect"/> and
    /// <paramref name="frameBounds"/> share one coordinate space (screen coordinates) and differ
    /// only by the invisible border, which is exactly the per-edge delta computed below.
    /// </remarks>
    public static Rect ApplyBorderCorrection(Rect desiredVisibleBounds, Rect windowRect, Rect frameBounds)
    {
        double leftDelta = windowRect.Left - frameBounds.Left;
        double topDelta = windowRect.Top - frameBounds.Top;
        double rightDelta = windowRect.Right - frameBounds.Right;
        double bottomDelta = windowRect.Bottom - frameBounds.Bottom;

        return new Rect(
            desiredVisibleBounds.Left + leftDelta,
            desiredVisibleBounds.Top + topDelta,
            desiredVisibleBounds.Right + rightDelta,
            desiredVisibleBounds.Bottom + bottomDelta);
    }

    /// <summary>
    /// Converts <paramref name="screenBounds"/> (the same screen-coordinate space as
    /// <c>GetWindowRect</c>/<c>DWMWA_EXTENDED_FRAME_BOUNDS</c>) into the "workspace coordinates"
    /// <c>WINDOWPLACEMENT.rcNormalPosition</c> requires for any top-level window <em>without</em>
    /// <c>WS_EX_TOOLWINDOW</c> (DESIGN.md §3.6b) — never for a tool window, which uses
    /// <paramref name="screenBounds"/> unconverted (see <see cref="WindowPlacementState.IsToolWindow"/>).
    /// </summary>
    /// <param name="primaryWorkArea">
    /// The primary monitor's work area in screen coordinates (<see cref="IPlacementSystem.ReadPrimaryWorkArea"/>
    /// — <c>SPI_GETWORKAREA</c>), queried once per batch, never per window.
    /// </param>
    /// <remarks>
    /// DOCUMENTED CONTRACT, composed from two separate pages (verified against both this session):
    /// (1) https://learn.microsoft.com/windows/win32/gdi/the-virtual-screen — "The primary monitor
    /// contains the origin (0,0)" of the virtual screen (the same coordinate space
    /// <c>GetWindowRect</c>/<c>DWMWA_EXTENDED_FRAME_BOUNDS</c> use); (2)
    /// https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-windowplacement — "Workspace
    /// coordinate (0,0) is the upper-left corner of the workspace area, the area of the screen not
    /// being used by application toolbars." Composing the two: workspace-coordinate (0,0) is the
    /// primary monitor's <em>work area</em> top-left in screen coordinates — which is <em>not</em>
    /// necessarily screen (0,0) itself, if the taskbar/an appbar is docked along the primary
    /// monitor's top or left edge (docking at the bottom or right leaves the work area's own
    /// top-left unchanged). This is a plain per-batch translation (subtract the same offset from
    /// every edge), applied uniformly regardless of which monitor the target window is actually on
    /// — DESIGN.md §3.6b's caveat is precisely that workspace coordinates are <em>not</em> each
    /// monitor's own, separately-zeroed work area; a window on a secondary monitor still gets the
    /// same large virtual-screen-relative offset, shifted by this one, single, primary-monitor-work-
    /// area-relative amount.
    /// </remarks>
    public static Rect ToWorkspaceCoordinates(Rect screenBounds, Rect primaryWorkArea) =>
        new(
            screenBounds.Left - primaryWorkArea.Left,
            screenBounds.Top - primaryWorkArea.Top,
            screenBounds.Right - primaryWorkArea.Left,
            screenBounds.Bottom - primaryWorkArea.Top);
}
