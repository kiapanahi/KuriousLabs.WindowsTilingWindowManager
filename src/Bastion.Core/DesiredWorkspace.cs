using System.Collections.Immutable;

namespace Bastion.Core;

/// <summary>
/// One workspace's desired arrangement: an ordered window stack plus the layout inputs
/// <see cref="ILayoutEngine.Solve"/> needs to turn it into placements (DESIGN.md §3.4/§6).
/// </summary>
/// <remarks>
/// <see cref="Windows"/> is a flat, ordered list — not a persisted <c>SplitTree</c> — deliberately:
/// <see cref="ILayoutEngine.Solve"/>'s public signature takes "windows in the engine's own
/// significant order," and reusing that exact seam (rather than reaching past it to
/// <c>SplitTreeLayout.Solve</c> directly) keeps the Reconciler engine-agnostic per DESIGN.md §3.9's
/// pluggable-<see cref="ILayoutEngine"/> extensibility model, at the cost of
/// <c>DwindleLayoutEngine</c>'s already-documented, already-accepted per-tick tree rebuild (see its
/// own remarks) rather than carrying <c>SplitTree</c>'s incremental-reuse benefit across ticks.
/// Revisit only once a second, alternative engine actually exists (DESIGN.md §12's v0.3 manual
/// split-tree/master-stack/monocle engines) and profiling shows the rebuild is real overhead.
///
/// <para>
/// <see cref="ImmutableArray{T}"/>'s default equality is reference-based, not element-wise — see
/// <see cref="DesiredState"/>'s own remarks; the same caveat applies here and is likewise not
/// relied upon anywhere.
/// </para>
/// </remarks>
public sealed record DesiredWorkspace
{
    /// <summary>Windows in this workspace, in the order <see cref="ILayoutEngine.Solve"/> should treat as significant.</summary>
    public required ImmutableArray<WindowId> Windows { get; init; }

    /// <summary>The monitor work area (already excludes the taskbar, etc.) this workspace solves against.</summary>
    public required Rect WorkArea { get; init; }

    /// <summary>Minimum-size floor every placement in this workspace must respect.</summary>
    public LayoutConstraints Constraints { get; init; }

    /// <summary>Outer/inner gap sizes to apply when solving this workspace.</summary>
    public LayoutGaps Gaps { get; init; }
}
