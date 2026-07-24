using Bastion.Core;
using Xunit;

namespace Bastion.Core.Tests;

/// <summary>
/// GitHub issue #9's pure object-graph merge (<see cref="WindowRulesDocument.Merge"/>) — the
/// "last write wins per named rule, never text-merged" contract of
/// <c>docs/engineering/json-ipc-config.md</c> §2.
/// </summary>
/// <remarks>
/// Example-based facts rather than an FsCheck property suite (docs/engineering/testing.md §3,
/// the <c>pure-core</c> skill's usual preference): <see cref="WindowRulesDocument.Merge"/>'s entire
/// behavior space is a small, fully-enumerable set of cases (disjoint names, a same-named
/// collision, either side empty, ordering) — building a custom FsCheck <c>Arbitrary</c> for
/// records with <see langword="required"/> members (<c>SplitTreeGenerators</c>'s own
/// hand-rolled-generator precedent in <c>Bastion.Layout.Tests</c>) would add real complexity for a
/// function this size without covering any case these facts don't already assert directly.
/// </remarks>
public sealed class WindowRulesDocumentTests
{
    [Fact]
    public void MergeKeepsBothSidesInOrderWhenRuleNamesAreDisjoint()
    {
        WindowRulesDocument shipped = DocumentOf(Rule("shipped-a", WindowRuleAction.Ignore), Rule("shipped-b", WindowRuleAction.Floating));
        WindowRulesDocument overlay = DocumentOf(Rule("user-a", WindowRuleAction.Manage));

        var merged = WindowRulesDocument.Merge(shipped, overlay);

        Assert.Equal(["shipped-a", "shipped-b", "user-a"], merged.Rules.Select(r => r.Name));
    }

    [Fact]
    public void MergeReplacesShippedRuleWholesaleWhenOverlayHasTheSameName()
    {
        WindowRulesDocument shipped = DocumentOf(Rule("spotify", WindowRuleAction.Floating, notes: "shipped default"));
        WindowRulesDocument overlay = DocumentOf(Rule("spotify", WindowRuleAction.Manage, notes: "user override"));

        var merged = WindowRulesDocument.Merge(shipped, overlay);

        WindowRule only = Assert.Single(merged.Rules);
        Assert.Equal(WindowRuleAction.Manage, only.Action);
        Assert.Equal("user override", only.Notes);
    }

    [Fact]
    public void MergeKeepsShippedPositionForACollidingNameRatherThanReordering()
    {
        WindowRulesDocument shipped = DocumentOf(
            Rule("first", WindowRuleAction.Ignore),
            Rule("second", WindowRuleAction.Ignore),
            Rule("third", WindowRuleAction.Ignore));
        WindowRulesDocument overlay = DocumentOf(Rule("second", WindowRuleAction.Manage));

        var merged = WindowRulesDocument.Merge(shipped, overlay);

        Assert.Equal(["first", "second", "third"], merged.Rules.Select(r => r.Name));
        Assert.Equal(WindowRuleAction.Manage, merged.Rules[1].Action);
    }

    [Fact]
    public void MergeReturnsShippedUnchangedWhenOverlayIsEmpty()
    {
        WindowRulesDocument shipped = DocumentOf(Rule("only", WindowRuleAction.Ignore));

        var merged = WindowRulesDocument.Merge(shipped, WindowRulesDocument.Empty);

        Assert.Equal(shipped, merged);
    }

    [Fact]
    public void MergeReturnsOverlayUnchangedWhenShippedIsEmpty()
    {
        WindowRulesDocument overlay = DocumentOf(Rule("only", WindowRuleAction.Floating));

        var merged = WindowRulesDocument.Merge(WindowRulesDocument.Empty, overlay);

        Assert.Equal(overlay, merged);
    }

    [Fact]
    public void MergeReturnsEmptyWhenBothSidesAreEmpty()
    {
        var merged = WindowRulesDocument.Merge(WindowRulesDocument.Empty, WindowRulesDocument.Empty);

        Assert.Empty(merged.Rules);
    }

    [Fact]
    public void EqualsComparesSeparatelyConstructedButContentIdenticalDocumentsAsEqual()
    {
        // Mirrors JournalDocument's own tested footgun (docs/engineering/daemon-architecture.md §5,
        // JournalDocument.cs remarks): ImmutableArray<T>'s default Equals compares the backing array
        // by reference, so two independently-built-but-content-identical documents must go through
        // WindowRulesDocument's own SequenceEqual-based Equals to compare equal at all.
        WindowRulesDocument left = DocumentOf(Rule("only", WindowRuleAction.Ignore, notes: "note"));
        WindowRulesDocument right = DocumentOf(Rule("only", WindowRuleAction.Ignore, notes: "note"));

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void EmptyHasNoRules()
    {
        Assert.Empty(WindowRulesDocument.Empty.Rules);
    }

    private static WindowRulesDocument DocumentOf(params WindowRule[] rules) => new() { Rules = [.. rules] };

    private static WindowRule Rule(string name, WindowRuleAction action, string? notes = null) => new()
    {
        Name = name,
        Match = new WindowRuleMatch { ExecutablePath = $@"C:\Apps\{name}.exe" },
        Action = action,
        Notes = notes,
    };
}
