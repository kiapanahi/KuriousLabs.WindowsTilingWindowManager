using System.Collections.Immutable;

namespace Bastion.Core;

/// <summary>
/// The whole contents of one rules file — either the shipped, curated community rules file or the
/// user's own overlay (GitHub issue #9; DESIGN.md §3.9, §9). Both files parse into this exact same
/// type; <see cref="Merge"/> is the pure, object-graph-level combination step
/// <c>docs/engineering/json-ipc-config.md</c> §2 requires in place of ever text-merging the two
/// JSONC documents.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Rules"/> defaults to <see cref="ImmutableArray{T}.Empty"/>, deliberately not
/// <see langword="required"/>.</b> Unlike <c>Bastion.Win32.JournalDocument.Entries</c> (which is
/// <see langword="required"/> because a missing journal file is handled one level up, before
/// deserialization ever runs), a rules file — especially the user's overlay — is expected to
/// legitimately omit the <c>rules</c> property entirely (a user with zero customizations, or a
/// freshly-created empty <c>{}</c> file). <c>System.Text.Json</c>'s source-generated deserializer
/// leaves a non-<see langword="required"/> property at its declared initializer when the JSON omits
/// it, so an omitted <c>rules</c> key here safely produces <see cref="ImmutableArray{T}.Empty"/>,
/// never a null-backed <c>default(ImmutableArray&lt;T&gt;)</c> — the standing footgun
/// <c>docs/engineering/daemon-architecture.md</c> §5 warns about.
/// </para>
/// <para>
/// <b>Custom <see cref="Equals(WindowRulesDocument?)"/>/<see cref="GetHashCode"/>, matching
/// <c>JournalDocument</c>'s established precedent</b>: <see cref="ImmutableArray{T}"/>'s own
/// <c>Equals</c> compares the backing array by reference, not content, so record-synthesized
/// equality over a <see cref="Rules"/> field would compare unequal for two independently-parsed but
/// content-identical documents (e.g. one read from the shipped file, one from a test fixture with
/// matching content). <see cref="Enumerable.SequenceEqual{TSource}(IEnumerable{TSource}?, IEnumerable{TSource}?)"/>
/// delegates to each <see cref="WindowRule"/>'s own (ordinary, no-collection-typed-member) record
/// equality per element instead.
/// </para>
/// </remarks>
public sealed record WindowRulesDocument
{
    /// <summary>Every rule this document defines, in file order. Always non-default — see this type's remarks.</summary>
    public ImmutableArray<WindowRule> Rules { get; init; } = ImmutableArray<WindowRule>.Empty;

    /// <summary>The empty document: no rules. What a missing or entirely-blank rules file parses to.</summary>
    public static WindowRulesDocument Empty { get; } = new();

    /// <summary>
    /// Combines <paramref name="shipped"/> (the curated community rules file) with
    /// <paramref name="overlay"/> (the user's own file) at the object-graph level: last write wins
    /// per named rule (<see cref="WindowRule.Name"/>), never a text/string merge of the two source
    /// files (<c>docs/engineering/json-ipc-config.md</c> §2). A rule present in only one side is
    /// kept as-is; a rule present in both is replaced wholesale by <paramref name="overlay"/>'s
    /// version, in <paramref name="shipped"/>'s original position — so a user override doesn't
    /// reorder the shipped list, and a genuinely new overlay-only rule is appended after it.
    /// </summary>
    /// <remarks>
    /// <b>A name repeated within a single source collapses to its last occurrence, not both</b> —
    /// including when the <em>other</em> source is entirely empty. An earlier implementation
    /// special-cased an empty <paramref name="overlay"/>/<paramref name="shipped"/> by returning the
    /// non-empty side completely unprocessed, which (caught in review, alongside the underlying
    /// duplicate-handling bug this remark otherwise describes) skipped de-duplication entirely
    /// whenever the *other* side happened to be empty — precisely the case a bare, no-overlay-yet
    /// install exercises on every startup. <see cref="AddOrReplace"/> now runs unconditionally over
    /// both sides for exactly this reason: the later occurrence of a repeated name always replaces
    /// the earlier slot in place, whether the repeat is within one source or across both, and
    /// whether or not the other source has any rules at all — so a name never appears more than
    /// once in the result under any combination of inputs.
    /// </remarks>
    public static WindowRulesDocument Merge(WindowRulesDocument shipped, WindowRulesDocument overlay)
    {
        ArgumentNullException.ThrowIfNull(shipped);
        ArgumentNullException.ThrowIfNull(overlay);

        if (shipped.Rules.IsEmpty && overlay.Rules.IsEmpty)
        {
            return Empty;
        }

        var indexByName = new Dictionary<string, int>(shipped.Rules.Length, StringComparer.Ordinal);
        ImmutableArray<WindowRule>.Builder builder = ImmutableArray.CreateBuilder<WindowRule>(shipped.Rules.Length + overlay.Rules.Length);

        AddOrReplace(shipped.Rules, indexByName, builder);
        AddOrReplace(overlay.Rules, indexByName, builder);

        return new WindowRulesDocument { Rules = builder.ToImmutable() };
    }

    /// <summary>
    /// Appends each rule in <paramref name="rules"/> to <paramref name="builder"/>, or — if
    /// <paramref name="indexByName"/> already has an entry for its <see cref="WindowRule.Name"/> —
    /// replaces that existing slot in place instead of appending a second occurrence. See
    /// <see cref="Merge"/>'s remarks for why this single helper deliberately handles both the
    /// within-one-source and across-both-sources duplicate cases identically.
    /// </summary>
    private static void AddOrReplace(ImmutableArray<WindowRule> rules, Dictionary<string, int> indexByName, ImmutableArray<WindowRule>.Builder builder)
    {
        foreach (WindowRule rule in rules)
        {
            if (indexByName.TryGetValue(rule.Name, out int existingIndex))
            {
                builder[existingIndex] = rule;
            }
            else
            {
                indexByName[rule.Name] = builder.Count;
                builder.Add(rule);
            }
        }
    }

    /// <summary>
    /// Every business-rule problem with this (already successfully deserialized) document's rules,
    /// beyond what <see langword="required"/>-member enforcement alone catches during deserialization
    /// itself: a present-but-empty/whitespace-only <see cref="WindowRule.Name"/>, and a
    /// <see cref="WindowRule.Match"/> with no criteria at all (which would match every window —
    /// see <see cref="WindowRuleMatch.IsEmpty"/>'s own remarks). Empty when every rule is valid.
    /// </summary>
    /// <remarks>
    /// <b>Consulted identically by <c>Bastion.Daemon</c>'s startup load and its hot-reload path</b> —
    /// a rule violating either check is rejected the same way regardless of which one loaded it
    /// (caught in review: an earlier design enforced these two checks only via the
    /// <c>Microsoft.Extensions.Options</c> pipeline, which the hot-reload path does not go through
    /// at all, so a hot-reloaded rule with no match criteria would have silently been accepted and
    /// published even though the identical rule would have failed at startup).
    /// </remarks>
    public IEnumerable<string> ValidateRules()
    {
        foreach (WindowRule rule in Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                yield return "A rule has an empty or whitespace-only name.";
            }

            if (rule.Match.IsEmpty)
            {
                yield return $"Rule '{rule.Name}' has no match criteria (appUserModelId, executablePath, or className) and would match every window.";
            }
        }
    }

    /// <summary>Structural equality — see this type's remarks for why <see cref="Rules"/> needs <see cref="Enumerable.SequenceEqual{TSource}(IEnumerable{TSource}?, IEnumerable{TSource}?)"/> rather than <see cref="ImmutableArray{T}"/>'s own reference-comparing <c>Equals</c>.</summary>
    public bool Equals(WindowRulesDocument? other) => other is not null && Rules.SequenceEqual(other.Rules);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        foreach (WindowRule rule in Rules)
        {
            hash.Add(rule);
        }

        return hash.ToHashCode();
    }
}
