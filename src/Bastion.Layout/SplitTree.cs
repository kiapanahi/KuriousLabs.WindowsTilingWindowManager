using System.Collections.Immutable;
using System.Diagnostics;
using Bastion.Core;

namespace Bastion.Layout;

/// <summary>
/// A persistent (structurally-shared) n-ary split tree (DESIGN.md §3.5, §6, §12 v0.1): internal
/// nodes are horizontal/vertical splits with float ratios, leaves are ordered window stacks.
/// </summary>
/// <remarks>
/// <see cref="Insert"/>/<see cref="Remove"/> each rebuild only the path from the root to the
/// affected leaf — every sibling off that path is the exact same object reference as before. That
/// structural sharing is what makes "a single insert/remove perturbs only the affected subtree"
/// (docs/engineering/testing.md §3's <c>InsertPerturbsOnlyAffectedSubtree</c> property) a
/// consequence of the data structure, not something each operation has to prove separately —
/// <see cref="SplitTreeLayout.Solve"/> deliberately avoids anything that would break this (see its
/// own remarks on why min-size clamping is intentionally non-aggregating).
/// </remarks>
public sealed class SplitTree
{
    /// <summary>
    /// Backstop against a genuinely pathological/runaway tree, not a realistic ceiling — depth
    /// scales with window count in a chain-shaped tree, and real window counts never approach
    /// this. Every recursive traversal here is bounded by it (CLAUDE.md §5, pure-core skill).
    /// </summary>
    internal const int MaxDepth = 1024;

    public static SplitTree Empty { get; } = new(null);

    public SplitTreeNode? Root { get; }

    private SplitTree(SplitTreeNode? root) => Root = root;

    /// <summary>All windows in this tree, in an unspecified but deterministic order.</summary>
    public IReadOnlyList<WindowId> Windows
    {
        get
        {
            List<WindowId> result = [];
            if (Root is not null)
            {
                CollectWindows(Root, result, depth: 0);
            }

            return result;
        }
    }

    /// <summary>Creates a one-leaf tree holding <paramref name="windowId"/>.</summary>
    /// <exception cref="InvalidOperationException">This tree is not empty; use <see cref="Insert"/> instead.</exception>
    public SplitTree InsertFirst(WindowId windowId)
    {
        if (Root is not null)
        {
            throw new InvalidOperationException("InsertFirst is only valid on an empty tree; use Insert to add to a non-empty tree.");
        }

        return new SplitTree(new LeafNode(ImmutableArray.Create(windowId)));
    }

    /// <summary>
    /// Splits the leaf containing <paramref name="anchor"/>, giving its existing content
    /// <paramref name="ratio"/> of the split (as <see cref="SplitNode.First"/>) and placing
    /// <paramref name="newWindow"/> in a new sibling leaf (<see cref="SplitNode.Second"/>).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="anchor"/> is not present in this tree, or <paramref name="newWindow"/> already is.
    /// </exception>
    /// <exception cref="InvalidOperationException">The tree exceeds <see cref="MaxDepth"/>.</exception>
    public SplitTree Insert(WindowId anchor, WindowId newWindow, SplitOrientation orientation, double ratio = 0.5)
    {
        if (Root is null)
        {
            throw new ArgumentException($"Anchor window {anchor} not found in an empty tree.", nameof(anchor));
        }

        if (Contains(Root, newWindow, depth: 0))
        {
            throw new ArgumentException($"Window {newWindow} is already present in this tree.", nameof(newWindow));
        }

        if (!TryInsert(Root, anchor, newWindow, orientation, ratio, depth: 0, out SplitTreeNode newRoot))
        {
            throw new ArgumentException($"Anchor window {anchor} not found in this tree.", nameof(anchor));
        }

        return new SplitTree(newRoot);
    }

    /// <summary>
    /// Removes <paramref name="windowId"/>, collapsing an emptied leaf's parent so the surviving
    /// sibling is promoted up one level. A no-op (returns this same instance) if the window is not
    /// present — matches <see cref="ImmutableList{T}.Remove"/>'s documented behavior for an absent
    /// item, rather than treating a benign remove-of-already-gone race as an error.
    /// </summary>
    public SplitTree Remove(WindowId windowId)
    {
        if (Root is null)
        {
            return this;
        }

        return TryRemove(Root, windowId, depth: 0, out SplitTreeNode? newRoot)
            ? new SplitTree(newRoot)
            : this;
    }

    private static bool Contains(SplitTreeNode node, WindowId windowId, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException($"SplitTree exceeded the maximum depth of {MaxDepth}.");
        }

        return node switch
        {
            LeafNode leaf => leaf.Windows.Contains(windowId),
            SplitNode split => Contains(split.First, windowId, depth + 1) || Contains(split.Second, windowId, depth + 1),
            _ => throw new UnreachableException(),
        };
    }

    private static bool TryInsert(SplitTreeNode node, WindowId anchor, WindowId newWindow, SplitOrientation orientation, double ratio, int depth, out SplitTreeNode result)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException($"SplitTree exceeded the maximum depth of {MaxDepth}.");
        }

        switch (node)
        {
            case LeafNode leaf when leaf.Windows.Contains(anchor):
                result = new SplitNode(orientation, ratio, leaf, new LeafNode(ImmutableArray.Create(newWindow)));
                return true;

            case LeafNode:
                result = node;
                return false;

            case SplitNode split:
                if (TryInsert(split.First, anchor, newWindow, orientation, ratio, depth + 1, out SplitTreeNode newFirst))
                {
                    result = split with { First = newFirst };
                    return true;
                }

                if (TryInsert(split.Second, anchor, newWindow, orientation, ratio, depth + 1, out SplitTreeNode newSecond))
                {
                    result = split with { Second = newSecond };
                    return true;
                }

                result = split;
                return false;

            default:
                throw new UnreachableException();
        }
    }

    private static bool TryRemove(SplitTreeNode node, WindowId target, int depth, out SplitTreeNode? result)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException($"SplitTree exceeded the maximum depth of {MaxDepth}.");
        }

        switch (node)
        {
            case LeafNode leaf when leaf.Windows.Contains(target):
                ImmutableArray<WindowId> remaining = leaf.Windows.Remove(target);
                result = remaining.IsEmpty ? null : new LeafNode(remaining);
                return true;

            case LeafNode:
                result = node;
                return false;

            case SplitNode split:
                if (TryRemove(split.First, target, depth + 1, out SplitTreeNode? newFirst))
                {
                    result = newFirst is null ? split.Second : split with { First = newFirst };
                    return true;
                }

                if (TryRemove(split.Second, target, depth + 1, out SplitTreeNode? newSecond))
                {
                    result = newSecond is null ? split.First : split with { Second = newSecond };
                    return true;
                }

                result = node;
                return false;

            default:
                throw new UnreachableException();
        }
    }

    private static void CollectWindows(SplitTreeNode node, List<WindowId> result, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException($"SplitTree exceeded the maximum depth of {MaxDepth}.");
        }

        switch (node)
        {
            case LeafNode leaf:
                result.AddRange(leaf.Windows);
                break;
            case SplitNode split:
                CollectWindows(split.First, result, depth + 1);
                CollectWindows(split.Second, result, depth + 1);
                break;
        }
    }
}
