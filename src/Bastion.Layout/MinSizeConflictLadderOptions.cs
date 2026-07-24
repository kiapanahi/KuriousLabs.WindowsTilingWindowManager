namespace Bastion.Layout;

/// <summary>
/// Config-tunable <see cref="MinSizeConflictLadder"/> behavior (DESIGN.md §6's three-step min-size
/// conflict ladder), mirroring <c>Bastion.Core</c>'s <c>ReconcilerOptions</c>/
/// <c>EffectiveMinSizeCacheOptions</c>'s own "documented default, not a hidden magic number" shape.
/// </summary>
public sealed record MinSizeConflictLadderOptions
{
    /// <summary>
    /// The fraction of the work area's corresponding dimension beyond which a window's effective
    /// minimum size is considered too large to reasonably even overlap onto the tiled arrangement,
    /// triggering step 3 (auto-float) instead of step 2 (bounded overlap) -- DESIGN.md §6: "if the
    /// minimum exceeds the configured tolerable fraction of the work area, auto-float." Evaluated
    /// per axis independently: a window whose required minimum width or height alone exceeds this
    /// fraction of the work area's corresponding dimension floats, even if the other axis is small.
    /// Must be in (0, 1].
    /// </summary>
    public double MaxTolerableFraction { get; init; } = 0.9;

    /// <summary>The shipped default configuration.</summary>
    public static MinSizeConflictLadderOptions Default { get; } = new();
}
