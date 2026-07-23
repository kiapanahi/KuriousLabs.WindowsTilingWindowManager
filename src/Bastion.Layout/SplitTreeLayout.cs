using Bastion.Core;

namespace Bastion.Layout;

/// <summary>
/// Solves a <see cref="SplitTree"/> into placements. Kept standalone rather than folded into
/// <c>DwindleLayoutEngine</c> because DESIGN.md §6's manual split-tree engine (i3-style manual
/// split commands, a later milestone) solves over the same tree shape and is described in its own
/// issue as "the most direct consumer" of it — it reuses this solver rather than reimplementing it.
/// </summary>
public static class SplitTreeLayout
{
    /// <summary>
    /// Solves <paramref name="tree"/> within <paramref name="workArea"/>, applying
    /// <paramref name="gaps"/> and a best-effort <paramref name="constraints"/> floor.
    /// </summary>
    /// <remarks>
    /// Min-size handling here is deliberately weaker than DESIGN.md §6's full three-step conflict
    /// ladder. Every split clamps its ratio so neither child gets less than the flat
    /// <paramref name="constraints"/> floor directly along that one split's own axis — and the
    /// minimum used at every node is always that same constant, never an aggregate computed from
    /// a child subtree's actual contents. An aggregating version (a subtree's reported minimum
    /// growing with its contents) was designed first and rejected: it breaks subtree-locality,
    /// because inserting into one subtree changes that subtree's aggregate minimum, which cascades
    /// through every ancestor's clamp decision and changes the rect handed to an untouched
    /// sibling — exactly the perturbation <see cref="SplitTree"/> exists to prevent. The trade-off
    /// of the flat-constant approach: a leaf several levels deep can still be squeezed below the
    /// minimum by a chain of ancestor splits that each, in isolation, only ever see the flat
    /// constant. True aggregate-correct redistribution — and the bounded-overlap/auto-float steps
    /// of the ladder — belongs to the learned, per-window constraint cache (DESIGN.md §6), a
    /// separate, later piece of work.
    /// </remarks>
    public static IReadOnlyList<WindowPlacement> Solve(SplitTree tree, Rect workArea, LayoutConstraints constraints, LayoutGaps gaps)
    {
        ArgumentNullException.ThrowIfNull(tree);

        List<WindowPlacement> placements = new(tree.Windows.Count);
        if (tree.Root is not null)
        {
            SolveCore(tree.Root, workArea.Deflate(gaps.Outer), constraints, gaps, placements, depth: 0);
        }

        return placements;
    }

    private static void SolveCore(SplitTreeNode node, Rect area, LayoutConstraints constraints, LayoutGaps gaps, List<WindowPlacement> placements, int depth)
    {
        if (depth > SplitTree.MaxDepth)
        {
            // Fail soft, not loud: collapse whatever remains into the current rect instead of
            // throwing. This runs on the Reconciler's hot path (DESIGN.md §3.4) — an unhandled
            // exception here has a far worse blast radius than a degraded layout for a
            // pathologically deep tree that should never occur in practice (pure-core skill's
            // "bound depth explicitly and fail soft" rule). The flatten below is iterative, not
            // recursive, so it can't itself blow the stack no matter how deep the remainder is.
            foreach (WindowId windowId in FlattenIteratively(node))
            {
                placements.Add(new WindowPlacement(windowId, area));
            }

            return;
        }

        switch (node)
        {
            case LeafNode leaf:
                foreach (WindowId windowId in leaf.Windows)
                {
                    placements.Add(new WindowPlacement(windowId, area));
                }

                break;

            case SplitNode split:
                double ratio = ClampRatio(split, area, constraints, gaps);
                (Rect firstArea, Rect secondArea) = SplitRect(area, split.Orientation, ratio, gaps.Inner);

                SolveCore(split.First, firstArea, constraints, gaps, placements, depth + 1);
                SolveCore(split.Second, secondArea, constraints, gaps, placements, depth + 1);
                break;
        }
    }

    /// <summary>
    /// Divides <paramref name="area"/> along <paramref name="orientation"/>'s axis at
    /// <paramref name="ratio"/>, leaving <paramref name="gap"/> of dead space between the two
    /// halves. Internal (not private) so <c>Bastion.Layout.Tests</c> can property-test this
    /// arithmetic in isolation — a single split's self-consistency (first + gap + second exactly
    /// reconstitutes the parent) — without walking a whole solved tree.
    /// </summary>
    internal static (Rect First, Rect Second) SplitRect(Rect area, SplitOrientation orientation, double ratio, double gap)
    {
        double halfGap = gap / 2.0;

        if (orientation == SplitOrientation.Horizontal)
        {
            double splitX = area.Left + (area.Width * ratio);
            return (area with { Right = splitX - halfGap }, area with { Left = splitX + halfGap });
        }

        double splitY = area.Top + (area.Height * ratio);
        return (area with { Bottom = splitY - halfGap }, area with { Top = splitY + halfGap });
    }

    private static double ClampRatio(SplitNode split, Rect area, LayoutConstraints constraints, LayoutGaps gaps)
    {
        bool horizontal = split.Orientation == SplitOrientation.Horizontal;
        double available = (horizontal ? area.Width : area.Height) - gaps.Inner;
        double min = horizontal ? constraints.MinWidth : constraints.MinHeight;

        if (available <= 0 || min <= 0)
        {
            return split.Ratio;
        }

        if (min * 2 > available)
        {
            // Both sides can't have the minimum at once. DESIGN.md §6's ladder steps 2/3 (bounded
            // overlap, auto-float) own this case; here, split proportionally to each side's
            // demand rather than claim a minimum guarantee this local clamp can't keep.
            return 0.5;
        }

        double minRatio = min / available;
        return Math.Clamp(split.Ratio, minRatio, 1.0 - minRatio);
    }

    private static IEnumerable<WindowId> FlattenIteratively(SplitTreeNode root)
    {
        Stack<SplitTreeNode> stack = new();
        stack.Push(root);

        while (stack.Count > 0)
        {
            SplitTreeNode node = stack.Pop();
            switch (node)
            {
                case LeafNode leaf:
                    foreach (WindowId windowId in leaf.Windows)
                    {
                        yield return windowId;
                    }

                    break;

                case SplitNode split:
                    stack.Push(split.Second);
                    stack.Push(split.First);
                    break;
            }
        }
    }
}
