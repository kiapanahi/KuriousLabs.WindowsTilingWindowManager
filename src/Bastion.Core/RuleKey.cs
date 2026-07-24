namespace Bastion.Core;

/// <summary>
/// Identifies the class of window <see cref="EffectiveMinSizeCache"/> learns an effective minimum
/// size for -- deliberately keyed by identity that survives HWND recycling (relaunching the same
/// app produces the same key), never by <see cref="WindowId"/> (DESIGN.md §6: "persisted per
/// rule-key with decay").
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope call, stated explicitly per GitHub issue #6's design guidance.</b> DESIGN.md's own
/// "rule-key" vocabulary presumes the curated-rules/JSONC config system (GitHub issue #9), which
/// does not exist yet and defines no concrete rule-key type today. Rather than block this cache on
/// that later issue, this type is a minimal, reasonable stand-in: an opaque string identifier,
/// matching whatever identity signal is cheaply available today from <c>Bastion.Win32</c>'s
/// <c>WindowIdentityResolver</c> (an AUMID or exe-path string, GitHub issue #3) or, failing that, a
/// window's class name -- see <c>Bastion.Win32.RuleKeyResolver</c> for the concrete (currently
/// unwired) mapping. When issue #9 lands a real, richer rule-key concept (glob patterns, per-rule
/// overrides, etc.), <see cref="EffectiveMinSizeCache"/>'s shape (a dictionary keyed by this type)
/// does not need to change to grow into that -- only how callers populate <see cref="Value"/> does.
/// Mirrors <see cref="WorkspaceKey"/>'s identical "deliberately minimal ahead of a later, richer
/// type" precedent.
/// </para>
/// <para>
/// Equality and hashing are the compiler-generated, value-based ones for a single
/// <see cref="string"/> field -- ordinal (<see cref="string.Equals(string?)"/>'s own default), since
/// <see cref="Value"/> is never meant to be culture-compared.
/// </para>
/// </remarks>
/// <param name="Value">The opaque rule-key identity string -- see this type's remarks for how one is derived today.</param>
public readonly record struct RuleKey(string Value)
{
    public override string ToString() => $"RuleKey({Value})";
}
