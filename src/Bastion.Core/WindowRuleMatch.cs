using System.Text.Json.Serialization;

namespace Bastion.Core;

/// <summary>
/// The per-app matching keys a <see cref="WindowRule"/> tests a window's resolved identity against
/// (GitHub issue #9). Every field is independently optional and, when set, must match exactly
/// (ordinal comparison — window class names and AUMIDs are not localized); a rule may combine more
/// than one field for extra precision (e.g. a specific class name only under a specific AUMID).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately three independent optional fields, not one opaque key.</b>
/// <c>Bastion.Win32.RuleKeyResolver</c> (GitHub issue #6) already collapses a resolved
/// <c>WindowIdentity</c> into a single kind-prefixed opaque <see cref="Core.RuleKey"/> string
/// (<c>"aumid:..."</c>/<c>"exe:..."</c>/<c>"class:..."</c>) for the effective-min-size cache, where
/// one identity rung is always authoritative and the others irrelevant. A curated rules file is a
/// richer authoring surface: the person writing JSONC wants to name the field they know (an AUMID
/// from the Store, an exe path from Task Manager, a class name from Spy++) without first mentally
/// re-deriving RuleKeyResolver's prefix scheme, and may legitimately want two fields combined.
/// Reconciling this shape with <c>RuleKeyResolver</c>'s single-key model — i.e. actually matching a
/// resolved window against a loaded <see cref="WindowRulesDocument"/> — is explicitly out of scope
/// here; see <see cref="WindowRule"/>'s remarks.
/// </para>
/// <para>
/// <b><see cref="IsEmpty"/> exists because a rule matching nothing is a real authoring error</b>: an
/// all-<see langword="null"/> <see cref="WindowRuleMatch"/> would (if ever wired to a matcher)
/// silently apply its <see cref="WindowRule.Action"/> to every window, floating or ignoring the
/// entire desktop. <c>Bastion.Daemon</c>'s options validation rejects any rule whose
/// <see cref="Match"/> is empty at load time rather than let that ship silently.
/// </para>
/// </remarks>
public sealed record WindowRuleMatch
{
    /// <summary>Matches the window's resolved Application User Model ID exactly (ordinal), when set.</summary>
    public string? AppUserModelId { get; init; }

    /// <summary>Matches the owning process's full executable path exactly (ordinal), when set.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Matches the window's raw Win32 class name exactly (ordinal), when set.</summary>
    public string? ClassName { get; init; }

    /// <summary><see langword="true"/> when every field is <see langword="null"/> or empty — see this type's remarks for why that is rejected rather than silently allowed.</summary>
    /// <remarks>
    /// <see cref="JsonIgnoreAttribute"/>: a computed helper for <c>Bastion.Daemon</c>'s options
    /// validation, not wire data — without it, this read-only property would still round-trip into
    /// both the on-disk JSONC shape and <c>Bastion.Daemon.WindowRulesSchemaWriter</c>'s published
    /// schema (confirmed empirically: it appeared in an early Verify-snapshot run of the exported
    /// schema), confusing a user reading the schema into wondering whether they're expected to set it.
    /// </remarks>
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrEmpty(AppUserModelId) && string.IsNullOrEmpty(ExecutablePath) && string.IsNullOrEmpty(ClassName);
}
