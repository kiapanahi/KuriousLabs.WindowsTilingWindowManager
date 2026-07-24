using Bastion.Core;

namespace Bastion.Layout;

/// <summary>
/// A window DESIGN.md §6's ladder step 3 excluded from the tiled arrangement entirely because its
/// effective minimum size exceeds <see cref="MinSizeConflictLadderOptions.MaxTolerableFraction"/> of
/// the work area on some axis.
/// </summary>
/// <remarks>
/// <see cref="Reason"/> is a default, generic toast-reason string. DESIGN.md §6's own example
/// ("Spotify won't shrink below 800 px — floated") names the app, which requires resolved window
/// identity (<c>Bastion.Win32.WindowIdentityResolver</c>/<c>RuleKeyResolver</c>) this project has no
/// access to (<c>Bastion.Layout</c> is Win32-free by design, DESIGN.md §3/§10). Substituting a
/// friendlier, app-named string is a future caller's job -- alongside the not-yet-built toast
/// delivery itself (GitHub issue #19) -- not this type's; this is only the data.
/// </remarks>
/// <param name="WindowId">The window this decision concerns.</param>
/// <param name="RequiredMinimum">The effective minimum size that triggered auto-float.</param>
/// <param name="Reason">A human-readable, default reason string suitable for a toast notification.</param>
/// <remarks>
/// No <c>[StructLayout(LayoutKind.Auto)]</c> here, unlike most other value-type DTOs in this repo
/// (<see cref="WindowPlacement"/>, <c>Bastion.Core.LayoutConstraints</c>, etc.) -- that attribute is
/// applied consistently only to structs whose fields are all value types (opting into
/// runtime-chosen field packing); every reference-type-holding DTO in this codebase
/// (<c>Bastion.Core.WorkspaceKey</c>, <c>Bastion.Core.RuleKey</c>, <c>Bastion.Win32.WindowIdentity</c>,
/// <c>Bastion.Win32.WindowManageabilityInfo</c>) omits it, and <see cref="Reason"/>'s <see cref="string"/>
/// field makes this one of those.
/// </remarks>
public readonly record struct AutoFloatDecision(WindowId WindowId, LayoutConstraints RequiredMinimum, string Reason);
