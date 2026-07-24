namespace Bastion.Core;

/// <summary>
/// A pure function from an ordered set of windows and layout parameters to solved placements.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be side-effect-free and Win32-free (DESIGN.md §3.5, §6; pure-core
/// skill): no <see cref="System.DateTime.Now"/>, no I/O, no adapter types. This is what lets
/// Tier 1 (docs/engineering/testing.md §3) run property tests unmodified on Linux CI.
/// </para>
/// <para>
/// Originally declared in <c>Bastion.Layout</c> alongside its own implementations; relocated to
/// this project by GitHub issue #4 because the Reconciler (this project) is this interface's other
/// essential consumer (constructor-injected, so it can call <see cref="Solve"/> once per
/// convergence pass — DESIGN.md §3.4) and <c>Bastion.Layout</c> already references
/// <c>Bastion.Core</c> for <see cref="WindowId"/>, so the reverse reference needed to keep this
/// interface there would have been circular. This is also the architecturally correct home for a
/// DESIGN.md §3.9 "in-process <see cref="ILayoutEngine"/> plugin" contract: <c>Bastion.Core</c>
/// defines the pluggable abstraction (and its supporting <see cref="Rect"/>/
/// <see cref="LayoutConstraints"/>/<see cref="LayoutGaps"/>/<see cref="WindowPlacement"/> data
/// types), and <c>Bastion.Layout</c> supplies Bastion's own shipped implementations
/// (<c>DwindleLayoutEngine</c> today; master-stack/monocle/manual-split-tree later) — a third-party
/// plugin author depends only on this project, never on Bastion's own built-in engine library.
/// </para>
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
