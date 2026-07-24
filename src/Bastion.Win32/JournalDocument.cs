using System.Collections.Immutable;

namespace Bastion.Win32;

/// <summary>
/// The whole write-ahead HWND journal file (<c>%LOCALAPPDATA%\Bastion\hwnd-journal.json</c>,
/// DESIGN.md §3.7, GitHub issue #8): every outstanding <see cref="JournalEntry"/> plus the
/// <see cref="Dirty"/> flag a future watchdog (a later issue) will read to detect an unclean prior
/// exit.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Entries"/> uses <c>required</c>, not a positional-record constructor parameter,
/// deliberately.</b> `docs/engineering/daemon-architecture.md` §5's standing footgun warning:
/// <c>default(ImmutableArray&lt;T&gt;)</c> wraps a <em>null</em> backing array, and touching
/// <c>.Length</c>/enumerating it null-derefs at runtime with no compile-time warning. A positional
/// record's constructor parameters silently fall back to <c>default</c> for anything a
/// deserializer doesn't populate; a <c>required</c> property instead makes
/// <c>System.Text.Json</c>'s source-generated deserializer <em>throw</em> a
/// <c>JsonException</c> on a journal file missing the <c>entries</c> property, rather than handing
/// back a document whose <see cref="Entries"/> null-derefs the moment a caller inspects it (the
/// json-ipc-config.md §1 "<c>required</c> members ... honored by both reflection and source-gen
/// deserialization" guarantee is exactly what makes this enforcement real, not just documentation).
/// </para>
/// <para>
/// Never default-construct this type (<c>new JournalDocument()</c> with no initializer would fail
/// to compile without setting both required members, which is the point) — use
/// <see cref="Empty"/> for "no outstanding journal state."
/// </para>
/// <para>
/// <b>Custom <see cref="Equals(JournalDocument?)"/>/<see cref="GetHashCode"/>, not the
/// compiler-synthesized record equality.</b> The top-level engineering guidance's own standing
/// warning: "<c>List&lt;T&gt;</c>/array members compare by reference — records holding collections
/// need a custom <c>Equals</c>." <see cref="ImmutableArray{T}"/> is no exception — its own
/// <c>Equals</c>/<c>==</c> compare the *backing array reference*, not element-by-element content
/// (confirmed empirically this session: two <see cref="JournalDocument"/> instances built from
/// separately-constructed but content-identical <see cref="Entries"/> arrays — e.g. one written to
/// disk, one read back — compared unequal under the default record equality). <see cref="Entries"/>
/// is compared via <see cref="Enumerable.SequenceEqual{TSource}(IEnumerable{TSource}?, IEnumerable{TSource}?)"/>
/// instead, which delegates to each <see cref="JournalEntry"/>'s own (ordinary, no-collection-typed-
/// member) record equality per element.
/// </para>
/// </remarks>
internal sealed record JournalDocument
{
    /// <summary>
    /// <see langword="true"/> from the moment the first entry is ever appended
    /// (<see cref="HwndJournalWriter"/>) until every outstanding entry has been successfully
    /// restored (<see cref="HwndJournalRestorer"/>) — the "unclean prior exit" signal a future
    /// watchdog issue consumes. This issue only produces the flag correctly; no watchdog reads it
    /// yet.
    /// </summary>
    public required bool Dirty { get; init; }

    /// <summary>Every window still owed a restore. Always <see cref="ImmutableArray{T}.Empty"/>, never <see langword="default"/> — see this type's remarks.</summary>
    public required ImmutableArray<JournalEntry> Entries { get; init; }

    /// <summary>The empty journal: no outstanding entries, not dirty. The state of a freshly-installed Bastion, and of a journal that has just been fully, cleanly restored.</summary>
    public static JournalDocument Empty { get; } = new() { Dirty = false, Entries = ImmutableArray<JournalEntry>.Empty };

    /// <summary>Structural equality — see this type's remarks for why <see cref="Entries"/> needs <see cref="Enumerable.SequenceEqual{TSource}(IEnumerable{TSource}?, IEnumerable{TSource}?)"/> rather than <see cref="ImmutableArray{T}"/>'s own reference-comparing <c>Equals</c>.</summary>
    public bool Equals(JournalDocument? other) =>
        other is not null && Dirty == other.Dirty && Entries.SequenceEqual(other.Entries);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(Dirty);
        foreach (JournalEntry entry in Entries)
        {
            hash.Add(entry);
        }

        return hash.ToHashCode();
    }
}
