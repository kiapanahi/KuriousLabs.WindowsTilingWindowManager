using System.Runtime.InteropServices;
using Bastion.Core;

namespace Bastion.Layout;

/// <summary>
/// A window DESIGN.md §6's ladder step 2 grew to its effective minimum by allowing it to overlap
/// its neighbors, rather than shrinking a neighbor below its own minimum (step 1) or floating it
/// entirely (step 3: <see cref="AutoFloatDecision"/>).
/// </summary>
/// <remarks>
/// <see cref="Bounds"/> duplicates whatever <see cref="MinSizeConflictResult.Placements"/> records
/// for the same <see cref="WindowId"/> -- this type exists purely so a future bar UI (GitHub issue
/// #19, not yet built) can flag the window as overlapping without cross-referencing
/// <c>Placements</c> itself; no UI/toast delivery is implemented here (out of this issue's scope).
/// </remarks>
/// <param name="WindowId">The overlapping window.</param>
/// <param name="Bounds">Its grown, possibly-overlapping bounds.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct BoundedOverlapPlacement(WindowId WindowId, Rect Bounds);
