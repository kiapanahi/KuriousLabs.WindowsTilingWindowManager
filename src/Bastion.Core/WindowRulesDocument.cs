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
    public static WindowRulesDocument Merge(WindowRulesDocument shipped, WindowRulesDocument overlay)
    {
        ArgumentNullException.ThrowIfNull(shipped);
        ArgumentNullException.ThrowIfNull(overlay);

        if (overlay.Rules.IsEmpty)
        {
            return shipped;
        }

        if (shipped.Rules.IsEmpty)
        {
            return overlay;
        }

        var indexByName = new Dictionary<string, int>(shipped.Rules.Length, StringComparer.Ordinal);
        ImmutableArray<WindowRule>.Builder builder = ImmutableArray.CreateBuilder<WindowRule>(shipped.Rules.Length + overlay.Rules.Length);
        foreach (WindowRule rule in shipped.Rules)
        {
            // A later same-named entry within a single file (an authoring mistake, not something
            // this issue validates against) simply wins over an earlier one here — deterministic,
            // never throws, matching this method's overall "last write wins" framing.
            indexByName[rule.Name] = builder.Count;
            builder.Add(rule);
        }

        foreach (WindowRule rule in overlay.Rules)
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

        return new WindowRulesDocument { Rules = builder.ToImmutable() };
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
