using System.Runtime.InteropServices;

namespace Bastion.Core;

/// <summary>
/// One instruction in a convergence pass's output plan — GitHub issue #4's deliverable. Scope
/// boundary (DESIGN.md §3.6): this is a <em>plan</em>, never executed here. The Placement Executor
/// (GitHub issue #5) is the sole consumer that turns <see cref="Move"/> instructions into
/// <c>SetWindowPos</c>/<c>DeferWindowPos</c> calls (with its own border-delta/coordinate-space
/// translation, hang-probe, and verify-after-move machinery) and <see cref="Untile"/> instructions
/// into simply leaving the window alone.
/// </summary>
/// <param name="WindowId">The window this instruction concerns.</param>
/// <param name="Action">What to do.</param>
/// <param name="TargetBounds">
/// The desired visible bounds for a <see cref="PlacementAction.Move"/> instruction, in the same
/// coordinate space as <see cref="ObservedWindow.FrameBounds"/> (DESIGN.md §6: "all engine rects
/// are visible bounds"; the Executor alone translates to <c>SetWindowPos</c> coordinates, §3.6c).
/// <see langword="null"/> for <see cref="PlacementAction.Untile"/> — there is nothing to move to.
/// </param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct PlacementInstruction(WindowId WindowId, PlacementAction Action, Rect? TargetBounds)
{
    /// <summary>Creates a <see cref="PlacementAction.Move"/> instruction targeting <paramref name="targetBounds"/>.</summary>
    public static PlacementInstruction Move(WindowId windowId, Rect targetBounds) =>
        new(windowId, PlacementAction.Move, targetBounds);

    /// <summary>Creates a <see cref="PlacementAction.Untile"/> instruction.</summary>
    public static PlacementInstruction Untile(WindowId windowId) =>
        new(windowId, PlacementAction.Untile, null);
}
