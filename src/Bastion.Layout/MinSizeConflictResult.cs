using System.Collections.Immutable;
using Bastion.Core;

namespace Bastion.Layout;

/// <summary>
/// <see cref="MinSizeConflictLadder.Resolve"/>'s output: DESIGN.md §6's three-step ladder applied as
/// a post-processing correction over an <see cref="ILayoutEngine"/>'s already-solved placements --
/// see <see cref="MinSizeConflictLadder"/>'s own remarks for why this runs entirely outside the
/// solving step itself.
/// </summary>
/// <remarks>
/// <para>
/// Invariant: every <see cref="WindowId"/> from the input placement list appears in exactly one of
/// <see cref="Placements"/> or <see cref="AutoFloats"/>, never both, never neither.
/// <see cref="Overlaps"/> is a purely-informational subset of <see cref="Placements"/>'s window IDs
/// -- <see cref="BoundedOverlapPlacement.Bounds"/> is not authoritative over what
/// <see cref="Placements"/> itself records for the same window (they always agree; it exists only
/// so a caller does not need to cross-reference <see cref="Placements"/> to find the overlapping
/// windows).
/// </para>
/// <para>
/// <b>An auto-floated window's vacated screen space is not redistributed to its former neighbors
/// within this single result.</b> This is a correction pass over placements already solved with the
/// floated window still present in the tree, not a full re-solve -- reclaiming that space is the
/// job of the next full layout pass once a future caller (out of this issue's scope) removes the
/// floated window from the desired window set entirely and re-solves from scratch, matching
/// DESIGN.md §1/§3.4's iterative-convergence model (a single pass need not be perfect; the next one
/// corrects it). A caller applying only <see cref="Placements"/> from a result with a non-empty
/// <see cref="AutoFloats"/> will therefore see a temporary gap until that next pass runs.
/// </para>
/// </remarks>
public sealed record MinSizeConflictResult
{
    /// <summary>
    /// Final placements for every window still part of the tiled arrangement (i.e. every input
    /// window except those in <see cref="AutoFloats"/>), in the same relative order as the input
    /// list <see cref="MinSizeConflictLadder.Resolve"/> was given. Never <see cref="ImmutableArray{T}.IsDefault"/>.
    /// </summary>
    public required ImmutableArray<WindowPlacement> Placements { get; init; }

    /// <summary>Windows step 2 (bounded overlap) grew beyond what their neighbors could absorb -- see <see cref="BoundedOverlapPlacement"/>.</summary>
    public ImmutableArray<BoundedOverlapPlacement> Overlaps { get; init; } = ImmutableArray<BoundedOverlapPlacement>.Empty;

    /// <summary>Windows step 3 excluded from tiling entirely -- see <see cref="AutoFloatDecision"/>.</summary>
    public ImmutableArray<AutoFloatDecision> AutoFloats { get; init; } = ImmutableArray<AutoFloatDecision>.Empty;

    /// <summary>Whether any window in this result needed any ladder step beyond "already satisfied."</summary>
    public bool HasAnyConflict => !Overlaps.IsEmpty || !AutoFloats.IsEmpty;
}
