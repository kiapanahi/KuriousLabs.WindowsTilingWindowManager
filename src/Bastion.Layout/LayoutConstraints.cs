using System.Runtime.InteropServices;

namespace Bastion.Layout;

/// <summary>
/// Minimum-size floor a layout must respect when placing a window.
/// </summary>
/// <remarks>
/// TODO(DESIGN.md §3.5, §6): the real constraint cache is per-window (each HWND's reported
/// <c>WM_GETMINMAXINFO</c> floor), not a single repo-wide constant; this flat value is a
/// placeholder until the constraint-cache adapter lands in <c>Bastion.Win32</c>.
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly record struct LayoutConstraints(double MinWidth, double MinHeight);
