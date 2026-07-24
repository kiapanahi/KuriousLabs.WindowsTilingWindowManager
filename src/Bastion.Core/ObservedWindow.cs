using System.Runtime.InteropServices;

namespace Bastion.Core;

/// <summary>
/// The last authoritative read for one managed window (DESIGN.md §3.4) — <see cref="IWindowSystem"/>'s
/// per-window output.
/// </summary>
/// <param name="WindowId">The window this reading describes.</param>
/// <param name="FrameBounds">
/// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> — the visible bounds <see cref="ILayoutEngine"/> output is
/// directly comparable to (DESIGN.md §6: "All engine rects are visible bounds"). This is what the
/// Reconciler diffs desired placements against.
/// </param>
/// <param name="WindowRect">
/// The raw <c>GetWindowRect</c> reading, DPI-virtualized and inclusive of any invisible resize
/// border (unlike <paramref name="FrameBounds"/>). Not used by this issue's own diffing — carried
/// through for the future Placement Executor's per-edge border-delta computation (DESIGN.md
/// §3.6c, GitHub issue #5), which needs both readings to translate a desired visible-bounds rect
/// into <c>SetWindowPos</c> coordinates.
/// </param>
/// <param name="IsCloaked">
/// <c>DWMWA_CLOAKED != 0</c>. Per DESIGN.md §3.3/§4, a cloaked window is kept in
/// <see cref="IWindowSystem.ReadAllAsync"/>'s result (never forgotten) but must never be tiled —
/// the Reconciler excludes any window with this set from <see cref="DesiredState"/>'s workspaces
/// without ever purging it from the observed snapshot.
/// </param>
/// <param name="IsIconic">Whether the window is currently minimized (<c>IsIconic</c>).</param>
/// <param name="IsZoomed">Whether the window is currently maximized (<c>IsZoomed</c>).</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ObservedWindow(
    WindowId WindowId,
    Rect FrameBounds,
    Rect WindowRect,
    bool IsCloaked,
    bool IsIconic,
    bool IsZoomed);
