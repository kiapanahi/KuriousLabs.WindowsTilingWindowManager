using Bastion.Core;

namespace Bastion.Layout;

/// <summary>
/// A pure function from an ordered set of windows and layout parameters to solved placements.
/// </summary>
/// <remarks>
/// Implementations must be side-effect-free and Win32-free (DESIGN.md §3.5, §6; pure-core
/// skill): no <see cref="System.DateTime.Now"/>, no I/O, no adapter types. This is what lets
/// Tier 1 (docs/engineering/testing.md §3) run property tests unmodified on Linux CI.
/// </remarks>
public interface ILayoutEngine
{
    /// <summary>
    /// Solves placements for <paramref name="windows"/> within <paramref name="workArea"/>.
    /// </summary>
    /// <param name="windows">Windows in the engine's own significant order (e.g. stack order).</param>
    /// <param name="workArea">The monitor work area (already excludes the taskbar, etc.).</param>
    /// <param name="constraints">Minimum-size floor every placement must respect.</param>
    /// <param name="gaps">Outer/inner gap sizes to apply.</param>
    /// <returns>One placement per input window, in no particular order.</returns>
    IReadOnlyList<WindowPlacement> Solve(
        IReadOnlyList<WindowId> windows,
        Rect workArea,
        LayoutConstraints constraints,
        LayoutGaps gaps);
}
