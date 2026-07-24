namespace Bastion.Core;

/// <summary>What a <see cref="PlacementInstruction"/> asks the (not-yet-built) Placement Executor to do.</summary>
public enum PlacementAction
{
    /// <summary>Move/resize the window to <see cref="PlacementInstruction.TargetBounds"/>.</summary>
    Move,

    /// <summary>
    /// Stop tiling the window — DESIGN.md §3.4's reassert-budget-exhaustion outcome ("Bastion
    /// adapts to the window ... or floats it"). Named <see cref="Untile"/> rather than "Float"
    /// (CA1720 bans identifiers containing a built-in type name, and <c>float</c> is one) —
    /// DESIGN.md's own prose still calls this "floating" a window. Carries no target bounds; the
    /// window keeps whatever position it currently has.
    /// </summary>
    Untile,
}
