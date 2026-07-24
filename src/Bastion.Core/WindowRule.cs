using System.ComponentModel.DataAnnotations;

namespace Bastion.Core;

/// <summary>
/// One named entry in a <see cref="WindowRulesDocument"/> (GitHub issue #9): a match criterion plus
/// the <see cref="WindowRuleAction"/> to apply to every window it matches.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Name"/> is the merge key.</b> <see cref="WindowRulesDocument.Merge"/> treats two
/// rules with the same <see cref="Name"/> — one from the shipped curated file, one from the user's
/// overlay — as "the same rule, last write wins": the overlay's whole <see cref="WindowRule"/>
/// replaces the shipped one wholesale rather than merging field-by-field (per
/// <c>docs/engineering/json-ipc-config.md</c> §2's "merged at the object-graph level... never
/// text-merged" — the object-graph unit being merged is the whole named rule, not its individual
/// properties). A shipped seed entry a user wants to override therefore just needs a user-file rule
/// with the identical <see cref="Name"/>.
/// </para>
/// <para>
/// <b>Validated via <see cref="RequiredAttribute"/> on <see cref="Name"/>, not by hand-rolled logic
/// here.</b> A plain <c>System.ComponentModel.DataAnnotations</c> attribute — inert metadata, no
/// reflection invocation, no I/O — so it costs this pure-core project nothing at rest. The
/// reflection-free enforcement happens in <c>Bastion.Daemon</c>'s <c>[OptionsValidator]</c>-generated
/// validator (<c>docs/engineering/daemon-architecture.md</c> §4), which recurses into each
/// <see cref="WindowRule"/> via <c>[ValidateEnumeratedItems]</c> on the options type's rule
/// collection. <see langword="required"/> alone (via <c>System.Text.Json</c>'s own enforcement)
/// already rejects a rule silently missing the property, so <see cref="RequiredAttribute"/> here
/// exists to additionally reject a rule that supplies an empty/whitespace-only name — its default
/// <see cref="RequiredAttribute.AllowEmptyStrings"/> is <see langword="false"/>, so it alone (with
/// no separate <see cref="MinLengthAttribute"/>) already rejects <see langword="null"/>, <c>""</c>,
/// and whitespace-only values for a <see langword="string"/> member; a separate
/// <c>[MinLength(1)]</c> would be both redundant and, applied to <see cref="Name"/> here, triggers a
/// spurious IL2026 trim warning under this project's <c>IsAotCompatible=true</c> build (its
/// constructor is unconditionally <c>[RequiresUnreferencedCode]</c>, regardless of the target being
/// a plain <see langword="string"/>).
/// </para>
/// <para>
/// <b>Consuming this against a resolved window identity is out of scope for this issue.</b> This
/// type (and <see cref="WindowRulesDocument"/>) is the config-loading substrate GitHub issue #9
/// builds; matching a live window against a loaded, merged <see cref="WindowRulesDocument"/> — and
/// reconciling <see cref="Match"/>'s multi-field shape with <c>Bastion.Win32.RuleKeyResolver</c>'s
/// single opaque <see cref="RuleKey"/> — is deliberately left for whichever future issue wires rules
/// into the Reconciler/manageability filter.
/// </para>
/// </remarks>
public sealed record WindowRule
{
    /// <summary>The stable identifier this rule is merged/overridden by (see this type's remarks). Never empty/whitespace-only.</summary>
    [Required]
    public required string Name { get; init; }

    /// <summary>The per-app match criteria a window must satisfy for this rule to apply.</summary>
    public required WindowRuleMatch Match { get; init; }

    /// <summary>The classification applied to every window this rule matches.</summary>
    public required WindowRuleAction Action { get; init; }

    /// <summary>Optional free-text authoring note (e.g. why this app is floated/ignored) — surfaced verbatim, never parsed.</summary>
    public string? Notes { get; init; }
}
