using System.Collections.Immutable;

namespace Bastion.Core;

/// <summary>
/// The Reconciler's desired arrangement: monitors → workspaces → window order (DESIGN.md §3.4).
/// An explicit, immutable snapshot — every mutating member returns a new instance; nothing here is
/// ever mutated in place.
/// </summary>
/// <remarks>
/// v0.1 scope (DESIGN.md §12): no monitor-topology service (GitHub issue #16) or multi-workspace
/// model (GitHub issue #15) exists yet, so this type's only populated key today is
/// <see cref="WorkspaceKey.Default"/> — but the dictionary shape is already what both later issues
/// grow into (more keys, not a different shape).
///
/// <para>
/// Like <c>Bastion.Layout</c>'s <c>LeafNode</c>, this record's compiler-generated equality compares
/// <see cref="Workspaces"/>/<see cref="UntiledWindows"/> by reference, not by element — two
/// structurally-identical instances built independently will not compare equal. Nothing in this
/// codebase relies on <see cref="DesiredState"/> equality for anything semantically meaningful
/// (<see cref="Reconciler"/> only ever replaces the whole snapshot, never compares two); keep it
/// that way rather than leaning on this type's default equality.
/// </para>
/// </remarks>
public sealed record DesiredState
{
    /// <summary>Every workspace this Reconciler currently has a layout for, keyed by <see cref="WorkspaceKey"/>.</summary>
    public required ImmutableDictionary<WorkspaceKey, DesiredWorkspace> Workspaces { get; init; }

    /// <summary>
    /// Windows the Reconciler has deliberately stopped tiling — DESIGN.md §3.4's reassert-budget
    /// exhaustion outcome ("Bastion adapts to the window ... or floats it"; named
    /// <c>UntiledWindows</c> rather than "Floated" because CA1720 bans identifiers containing a
    /// built-in type name, and <c>float</c> is one — see <see cref="PlacementAction.Untile"/>'s own
    /// remarks). Excluded from re-admission by <see cref="Reconciler"/>'s per-tick window-set sync
    /// until some future explicit reset (DESIGN.md §13 open question 7: "refill on focus change or
    /// only on user-initiated layout commands?" — deferred; no such trigger exists yet in this
    /// issue's scope, see GitHub issue #6).
    /// </summary>
    public ImmutableHashSet<WindowId> UntiledWindows { get; init; } = ImmutableHashSet<WindowId>.Empty;

    /// <summary>The empty desired state: no workspaces, nothing untiled.</summary>
    public static DesiredState Empty { get; } = new() { Workspaces = ImmutableDictionary<WorkspaceKey, DesiredWorkspace>.Empty };

    /// <summary>Whether <paramref name="windowId"/> is currently desired in any workspace.</summary>
    public bool ContainsWindow(WindowId windowId) => Workspaces.Values.Any(workspace => workspace.Windows.Contains(windowId));

    /// <summary>Adds or replaces the workspace registered under <paramref name="key"/>.</summary>
    public DesiredState WithWorkspace(WorkspaceKey key, DesiredWorkspace workspace) =>
        this with { Workspaces = Workspaces.SetItem(key, workspace) };

    /// <summary>
    /// Removes <paramref name="windowId"/> from whichever workspace(s) currently contain it. A
    /// no-op (returns this same instance) if it is not present anywhere.
    /// </summary>
    public DesiredState WithWindowRemoved(WindowId windowId)
    {
        ImmutableDictionary<WorkspaceKey, DesiredWorkspace> updated = Workspaces;
        foreach ((WorkspaceKey key, DesiredWorkspace workspace) in Workspaces)
        {
            if (!workspace.Windows.Contains(windowId))
            {
                continue;
            }

            updated = updated.SetItem(key, workspace with { Windows = workspace.Windows.Remove(windowId) });
        }

        return ReferenceEquals(updated, Workspaces) ? this : this with { Workspaces = updated };
    }

    /// <summary>
    /// Removes <paramref name="windowId"/> from tiling and records it in <see cref="UntiledWindows"/>
    /// — the reassert-budget-exhaustion outcome (DESIGN.md §3.4).
    /// </summary>
    public DesiredState WithWindowUntiled(WindowId windowId) =>
        WithWindowRemoved(windowId) with { UntiledWindows = UntiledWindows.Add(windowId) };
}
