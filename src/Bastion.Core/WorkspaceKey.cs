namespace Bastion.Core;

/// <summary>
/// Identifies one (monitor, workspace) pairing <see cref="DesiredState"/> tracks a layout for.
/// </summary>
/// <remarks>
/// DESIGN.md §3.4 describes <see cref="DesiredState"/> as "monitors → workspaces → layout trees."
/// v0.1's scope (DESIGN.md §12) is a single workspace per monitor with neither the monitor
/// topology service (GitHub issue #16) nor multi-workspace support (GitHub issue #15) built yet,
/// so this type is deliberately a minimal, opaque-ish label rather than a real monitor/workspace
/// identity — <see cref="Default"/> is the only key the Reconciler auto-populates today. Once
/// issue #16 lands a real monitor identity (DESIGN.md §8's <c>StableMonitorId</c>/EDID-keyed
/// persistence) and issue #15 adds multiple workspaces per monitor, callers key this type on that
/// richer identity instead — the <em>shape</em> of <see cref="DesiredState"/> (a dictionary keyed
/// by this type) does not need to change to grow into that, only the population of keys does.
/// </remarks>
public readonly record struct WorkspaceKey(string Name)
{
    /// <summary>
    /// The single workspace key v0.1's Reconciler auto-populates with newly-admitted windows
    /// (see <see cref="Reconciler"/>'s remarks) — there is no monitor-assignment policy yet to
    /// pick a different one.
    /// </summary>
    public static WorkspaceKey Default { get; } = new("default");

    public override string ToString() => $"WorkspaceKey({Name})";
}
