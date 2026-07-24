using System.Runtime.InteropServices;
using Bastion.Core;

namespace Bastion.Layout;

/// <summary>
/// A window DESIGN.md §6's ladder step 1 fully satisfied by redistributing space from a single
/// full-span neighbor -- no overlap (<see cref="BoundedOverlapPlacement"/>) or auto-float
/// (<see cref="AutoFloatDecision"/>) was needed.
/// </summary>
/// <remarks>
/// Tracked as its own outcome (Codex review finding on this PR) rather than silently folded into
/// <see cref="MinSizeConflictResult.Placements"/> with no trace: <see cref="MinSizeConflictResult.HasAnyConflict"/>
/// promises "any ladder step beyond already satisfied," and a successful step 1 redistribution is
/// exactly that -- it moved multiple windows' bounds away from what the solver originally produced,
/// even though nothing overlaps and nothing floated.
/// </remarks>
/// <param name="WindowId">The window step 1 satisfied.</param>
/// <param name="Bounds">Its redistributed (non-overlapping) bounds -- also what <see cref="MinSizeConflictResult.Placements"/> records for the same window.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct RedistributedPlacement(WindowId WindowId, Rect Bounds);
