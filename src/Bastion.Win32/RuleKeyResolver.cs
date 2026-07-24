using System.Diagnostics;
using Bastion.Core;

namespace Bastion.Win32;

/// <summary>
/// Maps a resolved <see cref="WindowIdentity"/> (DESIGN.md §3.3, GitHub issue #3) plus a window's
/// class name to the minimal <see cref="RuleKey"/> stand-in GitHub issue #6 defines ahead of the
/// curated-rules/JSONC config system (GitHub issue #9, not yet built and not built here).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope call, stated explicitly.</b> This type is intentionally not called from anywhere in
/// this change -- no daemon composition root (issue #10) or Placement Executor call site (issue
/// #5) wires it up yet, per issue #6's own explicit scope boundary. It exists so the "minimal,
/// reasonable stand-in... matching whatever identity signal is cheaply available today" the issue
/// asks for is a concrete, tested mapping rather than only a paragraph of design intent -- mirrors
/// <see cref="Bastion.Win32.PlacementExecutor.Purge"/>'s identical "ready for a future caller, not
/// wired to one yet" shape.
/// </para>
/// <para>
/// <b>Mapping.</b> An AUMID or exe-path identity (<see cref="WindowIdentityKind.Aumid"/>/
/// <see cref="WindowIdentityKind.ExePath"/>) becomes a kind-prefixed rule key over
/// <see cref="WindowIdentity.Value"/> -- the prefix keeps an AUMID string and an exe-path string
/// that happen to collide textually (implausible, but not impossible) from colliding as rule keys
/// too, and makes a persisted key self-describing once GitHub issue #9's real rules file can read
/// these back. <see cref="WindowIdentityKind.Unknown"/> (every rung of DESIGN.md §3.3's identity
/// chain failed) falls back to <paramref name="className"/> -- itself sourced from
/// <c>WindowProbe.GetClassName</c>, which already degrades to <see cref="string.Empty"/> rather
/// than throwing on its own failure, so this method never throws either; a totally unidentifiable
/// window (no identity, no class name) collapses to a single shared <c>"class:"</c> rule key rather
/// than a distinct one per such window, which is an acceptable degenerate terminal case matching
/// <see cref="WindowIdentity.Unknown"/>'s own "total, expected-possible failure" framing.
/// </para>
/// </remarks>
internal static class RuleKeyResolver
{
    /// <summary>Resolves <paramref name="identity"/>/<paramref name="className"/> to a <see cref="RuleKey"/> -- see this type's remarks for the exact mapping.</summary>
    public static RuleKey Resolve(WindowIdentity identity, string className) =>
        identity.Kind switch
        {
            WindowIdentityKind.Aumid => new RuleKey($"aumid:{identity.Value}"),
            WindowIdentityKind.ExePath => new RuleKey($"exe:{identity.Value}"),
            WindowIdentityKind.Unknown => new RuleKey($"class:{className}"),
            _ => throw new UnreachableException(),
        };
}
