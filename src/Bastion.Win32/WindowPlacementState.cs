using System.Runtime.InteropServices;

namespace Bastion.Win32;

/// <summary>
/// The live window-state reads <see cref="PlacementExecutor"/> needs to pick its placement branch
/// (DESIGN.md §3.6b): whether to restore directly into the tile via <c>SetWindowPlacement</c>, and
/// which coordinate space that call needs.
/// </summary>
/// <param name="IsIconic"><c>IsIconic</c> — the window is minimized.</param>
/// <param name="IsZoomed"><c>IsZoomed</c> — the window is maximized.</param>
/// <param name="IsArranged">
/// <c>IsWindowArranged</c> — the window is snapped (DESIGN.md §9's "User snaps a window" row).
/// Documented as mutually exclusive with iconic/maximized, but read independently here rather than
/// assumed, matching the documented example in <c>IsWindowArranged</c>'s own remarks.
/// </param>
/// <param name="IsToolWindow">
/// Extended style includes <c>WS_EX_TOOLWINDOW</c> — selects the screen-coordinates path for
/// <c>rcNormalPosition</c> rather than workspace coordinates (DESIGN.md §3.6b).
/// </param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct WindowPlacementState(bool IsIconic, bool IsZoomed, bool IsArranged, bool IsToolWindow)
{
    /// <summary>
    /// Whether any of <see cref="IsIconic"/>/<see cref="IsZoomed"/>/<see cref="IsArranged"/> holds —
    /// DESIGN.md §3.6b's trigger for restoring directly into the tile via <c>SetWindowPlacement</c>
    /// rather than a plain move.
    /// </summary>
    public bool NeedsStateNormalization => IsIconic || IsZoomed || IsArranged;
}
