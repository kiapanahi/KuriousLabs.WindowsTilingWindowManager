using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Bastion.Core;

namespace Bastion.Layout;

/// <summary>
/// DESIGN.md §6's three-step min-size conflict ladder -- redistribute along the split axis, then
/// bounded overlap, then auto-float -- implemented as a pure post-processing correction over an
/// already-solved placement list, never inside the solving step itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design decision (GitHub issue #6), stated explicitly per that issue's own instruction.</b>
/// <see cref="SplitTreeLayout"/>'s <c>ClampRatio</c> is deliberately local-only: it clamps a single
/// split's ratio against one flat <see cref="LayoutConstraints"/> constant, never an aggregate
/// computed from a subtree's actual contents, specifically to preserve <see cref="SplitTree"/>'s
/// subtree-locality guarantee (see that method's own remarks). Two options existed for where this
/// issue's <em>aggregate-correct</em>, per-window ladder could run: (A) extend
/// <see cref="SplitTreeLayout"/> itself to accept a per-window constraint lookup instead of one flat
/// value (keeping the clamp itself local-only, but still touching that type's established, reviewed,
/// deliberately-narrow contract), or (B) implement the entire ladder -- including step 1 -- as a
/// standalone post-processing stage that runs after <see cref="SplitTreeLayout.Solve"/> (or any
/// other <see cref="ILayoutEngine"/> implementation) has already produced its placements, operating
/// purely on the solved rects' geometry with no dependency on <see cref="SplitTree"/>'s internal
/// shape at all.
/// </para>
/// <para>
/// <b>This type implements (B), for three reasons.</b> First, it keeps <see cref="SplitTreeLayout"/>
/// -- reviewed, tested, and deliberately scoped in GitHub issues #37/#4 -- completely untouched, so
/// its subtree-locality guarantee needs no re-verification; this file adds no dependency on that
/// type at all, and neither it nor its tests are modified by this change. Second, it matches this
/// issue's own acceptance-criteria framing -- "this issue defines the cache's own update/decay API,
/// not the Executor's call site" -- of a cache (and, by the same logic, a ladder) that is a
/// standalone component other code calls into, not something baked into one specific solver. Third,
/// and most concretely, it makes the ladder engine-agnostic: because it consumes only
/// <see cref="WindowPlacement"/> rects (the common output shape every <see cref="ILayoutEngine"/>
/// produces) plus a caller-supplied per-window minimum lookup, the exact same implementation works
/// unchanged for the future master-stack/monocle/manual-split-tree engines (DESIGN.md §12) without
/// each needing its own constraint-lookup plumbing the way option (A) would have required per
/// engine.
/// </para>
/// <para>
/// <b>Step 1's scope: single, full-span neighbor redistribution only.</b> "Redistributing along the
/// split axis" is reinterpreted here, geometry-only, as: find the <em>one</em> other placement that
/// shares the constrained window's full extent on the perpendicular axis and touches it along the
/// deficient axis (the geometric signature of two tree siblings produced by a single split), and
/// move exactly the deficit from one to the other. If no such single, full-span neighbor exists (the
/// border is shared by more than one neighbor, only partially overlaps, or does not exist at all --
/// e.g. the window is already alone along that edge), step 1 declines for that axis and steps 2/3
/// decide instead. This is a deliberate scope boundary, not a missing case: DESIGN.md §6 itself only
/// asks for a best-effort redistribution step before falling back to overlap/float, and a fully
/// general N-neighbor border-redistribution solver would be exactly the kind of aggregate,
/// cross-subtree computation <see cref="SplitTreeLayout"/>'s own design rejected.
/// </para>
/// <para>
/// <b>Where the per-window minimums come from is deliberately this type's caller's problem, not this
/// type's.</b> <c>effectiveMinSizes</c> (see <see cref="Resolve"/>) is a plain lookup; in production
/// it would be populated by resolving each window's <c>Bastion.Core.RuleKey</c> and querying
/// <c>Bastion.Core.EffectiveMinSizeCache.GetEffectiveMinSize</c> -- but that resolution step is
/// explicitly out of this issue's scope (no wiring to the Reconciler/Executor), so this type has no
/// reference to <c>EffectiveMinSizeCache</c> or <c>RuleKey</c> at all. A window absent from
/// <c>effectiveMinSizes</c>, or whose <paramref name="defaultMinimum"/> already exceeds its cache
/// entry, falls back to <paramref name="defaultMinimum"/> -- the same flat floor the caller already
/// passed to <see cref="ILayoutEngine.Solve"/>/<see cref="SplitTreeLayout.Solve"/>, so this ladder
/// can never redistribute a neighbor's space away below the guarantee the solve step already gave
/// it.
/// </para>
/// </remarks>
public static class MinSizeConflictLadder
{
    private const double Epsilon = 1e-6;

    /// <summary>
    /// Applies the three-step ladder to <paramref name="placements"/>. Windows already meeting
    /// their required minimum (<paramref name="defaultMinimum"/>, raised per-window by any entry in
    /// <paramref name="effectiveMinSizes"/>) pass through unchanged.
    /// </summary>
    /// <param name="placements">An <see cref="ILayoutEngine"/>'s already-solved output.</param>
    /// <param name="defaultMinimum">
    /// The same flat floor the caller already solved <paramref name="placements"/> with (e.g.
    /// <see cref="SplitTreeLayout.Solve"/>'s own <c>constraints</c> parameter). Applies to every
    /// window absent from <paramref name="effectiveMinSizes"/>, and is never undercut by a smaller
    /// entry there -- the effective requirement for any window is always at least this floor.
    /// </param>
    /// <param name="effectiveMinSizes">
    /// Per-window minimum size, e.g. resolved from <c>EffectiveMinSizeCache</c> by a caller outside
    /// this project. A window with no entry (or a smaller one than <paramref name="defaultMinimum"/>)
    /// is still bound by <paramref name="defaultMinimum"/>.
    /// </param>
    /// <param name="workArea">The same work area <paramref name="placements"/> was solved within -- needed for step 3's tolerable-fraction test.</param>
    /// <param name="options">Ladder thresholds; <see cref="MinSizeConflictLadderOptions.Default"/> if omitted.</param>
    public static MinSizeConflictResult Resolve(
        IReadOnlyList<WindowPlacement> placements,
        LayoutConstraints defaultMinimum,
        IReadOnlyDictionary<WindowId, LayoutConstraints> effectiveMinSizes,
        Rect workArea,
        MinSizeConflictLadderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(effectiveMinSizes);

        MinSizeConflictLadderOptions resolvedOptions = options ?? MinSizeConflictLadderOptions.Default;
        if (!double.IsFinite(resolvedOptions.MaxTolerableFraction) || resolvedOptions.MaxTolerableFraction <= 0 || resolvedOptions.MaxTolerableFraction > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.MaxTolerableFraction, "MinSizeConflictLadderOptions.MaxTolerableFraction must be in (0, 1].");
        }

        if (placements.Count == 0)
        {
            return new MinSizeConflictResult { Placements = ImmutableArray<WindowPlacement>.Empty };
        }

        var working = new Dictionary<WindowId, Rect>(placements.Count);
        foreach (WindowPlacement placement in placements)
        {
            working[placement.WindowId] = placement.Bounds;
        }

        ImmutableArray<RedistributedPlacement>.Builder redistributions = ImmutableArray.CreateBuilder<RedistributedPlacement>();
        ImmutableArray<BoundedOverlapPlacement>.Builder overlaps = ImmutableArray.CreateBuilder<BoundedOverlapPlacement>();
        ImmutableArray<AutoFloatDecision>.Builder autoFloats = ImmutableArray.CreateBuilder<AutoFloatDecision>();

        foreach (WindowPlacement placement in placements)
        {
            ProcessWindow(working, placement.WindowId, defaultMinimum, effectiveMinSizes, workArea, resolvedOptions, redistributions, overlaps, autoFloats);
        }

        return new MinSizeConflictResult
        {
            Placements = BuildFinalPlacements(placements, working),
            Redistributions = redistributions.ToImmutable(),
            Overlaps = overlaps.ToImmutable(),
            AutoFloats = autoFloats.ToImmutable(),
        };
    }

    /// <summary>
    /// Runs the whole ladder for one window: skip entirely if its current tile already satisfies
    /// its requirement (regardless of how large that requirement is relative to
    /// <paramref name="workArea"/> -- Codex review finding on this PR: a lone/monocle tile that
    /// already meets an oversized cache-learned minimum has no conflict to resolve and must not be
    /// floated); otherwise auto-float (step 3) if the requirement is unreasonable relative to
    /// <paramref name="workArea"/>, else attempt step 1 on each deficient axis and fall back to
    /// step 2 (bounded overlap) for whatever step 1 could not resolve. Mutates
    /// <paramref name="working"/> and appends to <paramref name="redistributions"/>/
    /// <paramref name="overlaps"/>/<paramref name="autoFloats"/> as needed.
    /// </summary>
    private static void ProcessWindow(
        Dictionary<WindowId, Rect> working,
        WindowId id,
        LayoutConstraints defaultMinimum,
        IReadOnlyDictionary<WindowId, LayoutConstraints> effectiveMinSizes,
        Rect workArea,
        MinSizeConflictLadderOptions options,
        ImmutableArray<RedistributedPlacement>.Builder redistributions,
        ImmutableArray<BoundedOverlapPlacement>.Builder overlaps,
        ImmutableArray<AutoFloatDecision>.Builder autoFloats)
    {
        LayoutConstraints required = Required(id, defaultMinimum, effectiveMinSizes);
        if (required.MinWidth <= 0 && required.MinHeight <= 0)
        {
            return;
        }

        Rect current = working[id];
        double widthDeficit = required.MinWidth - current.Width;
        double heightDeficit = required.MinHeight - current.Height;
        if (widthDeficit <= Epsilon && heightDeficit <= Epsilon)
        {
            // Already satisfied by its current tile -- no conflict exists, so the tolerable-fraction
            // test below (which is about whether the *requirement* is reasonable, not whether a
            // deficit exists) must never run for this window at all.
            return;
        }

        if (ExceedsTolerableFraction(required, workArea, options))
        {
            autoFloats.Add(new AutoFloatDecision(id, required, BuildAutoFloatReason(required, workArea)));
            working.Remove(id);
            return;
        }

        bool widthRedistributed = widthDeficit > Epsilon && TryRedistribute(working, id, SplitOrientation.Horizontal, widthDeficit, defaultMinimum, effectiveMinSizes);
        bool heightRedistributed = heightDeficit > Epsilon && TryRedistribute(working, id, SplitOrientation.Vertical, heightDeficit, defaultMinimum, effectiveMinSizes);

        current = working[id];
        bool stillDeficient = required.MinWidth - current.Width > Epsilon || required.MinHeight - current.Height > Epsilon;
        if (stillDeficient)
        {
            Rect inflated = InflateToMinimum(current, required, workArea);
            working[id] = inflated;
            overlaps.Add(new BoundedOverlapPlacement(id, inflated));
        }
        else if (widthRedistributed || heightRedistributed)
        {
            redistributions.Add(new RedistributedPlacement(id, current));
        }
    }

    /// <summary>Rebuilds the input order, dropping any window <see cref="ProcessWindow"/> removed from <paramref name="working"/> (auto-floated) and applying whatever bounds it ended up with otherwise.</summary>
    private static ImmutableArray<WindowPlacement> BuildFinalPlacements(IReadOnlyList<WindowPlacement> placements, Dictionary<WindowId, Rect> working)
    {
        ImmutableArray<WindowPlacement>.Builder finalPlacements = ImmutableArray.CreateBuilder<WindowPlacement>(working.Count);
        foreach (WindowPlacement placement in placements)
        {
            if (working.TryGetValue(placement.WindowId, out Rect finalBounds))
            {
                finalPlacements.Add(new WindowPlacement(placement.WindowId, finalBounds));
            }
        }

        return finalPlacements.MoveToImmutable();
    }

    /// <summary>The effective requirement for <paramref name="id"/>: at least <paramref name="defaultMinimum"/>, raised further by any <paramref name="effectiveMinSizes"/> entry.</summary>
    private static LayoutConstraints Required(WindowId id, LayoutConstraints defaultMinimum, IReadOnlyDictionary<WindowId, LayoutConstraints> effectiveMinSizes)
    {
        LayoutConstraints specific = effectiveMinSizes.GetValueOrDefault(id);
        return new LayoutConstraints(
            Math.Max(defaultMinimum.MinWidth, specific.MinWidth),
            Math.Max(defaultMinimum.MinHeight, specific.MinHeight));
    }

    private static bool ExceedsTolerableFraction(LayoutConstraints required, Rect workArea, MinSizeConflictLadderOptions options) =>
        required.MinWidth > options.MaxTolerableFraction * workArea.Width
        || required.MinHeight > options.MaxTolerableFraction * workArea.Height;

    private static string BuildAutoFloatReason(LayoutConstraints required, Rect workArea) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Window requires at least {required.MinWidth:0}x{required.MinHeight:0}px, exceeding the tolerable share of the {workArea.Width:0}x{workArea.Height:0}px work area — floated.");

    /// <summary>
    /// Attempts step 1 for one axis of <paramref name="id"/>'s deficit: find the single, full-span
    /// neighbor along <paramref name="axis"/> (<see cref="TryFindFullSpanNeighbor"/>) and, if
    /// shrinking it by <paramref name="deficit"/> would not push it below its own required minimum,
    /// move exactly that much space from the neighbor to <paramref name="id"/>. Mutates
    /// <paramref name="working"/> in place and returns <see langword="true"/> on success; leaves it
    /// untouched and returns <see langword="false"/> on failure (falling through to steps 2/3 is the
    /// caller's job).
    /// </summary>
    private static bool TryRedistribute(
        Dictionary<WindowId, Rect> working,
        WindowId id,
        SplitOrientation axis,
        double deficit,
        LayoutConstraints defaultMinimum,
        IReadOnlyDictionary<WindowId, LayoutConstraints> effectiveMinSizes)
    {
        Rect current = working[id];
        if (!TryFindFullSpanNeighbor(working, id, current, axis, out WindowId neighborId, out bool neighborIsAfter))
        {
            return false;
        }

        Rect neighborRect = working[neighborId];
        LayoutConstraints neighborRequired = Required(neighborId, defaultMinimum, effectiveMinSizes);
        double neighborRequiredSize = axis == SplitOrientation.Horizontal ? neighborRequired.MinWidth : neighborRequired.MinHeight;
        double neighborCurrentSize = axis == SplitOrientation.Horizontal ? neighborRect.Width : neighborRect.Height;
        double neighborSizeAfterShrink = neighborCurrentSize - deficit;

        if (neighborSizeAfterShrink <= 0 || neighborSizeAfterShrink < neighborRequiredSize - Epsilon)
        {
            // The axis cannot absorb it -- steps 2/3 decide instead.
            return false;
        }

        if (neighborIsAfter)
        {
            working[id] = AdjustEdge(current, axis, isLeadingEdge: false, delta: deficit);
            working[neighborId] = AdjustEdge(neighborRect, axis, isLeadingEdge: true, delta: deficit);
        }
        else
        {
            working[id] = AdjustEdge(current, axis, isLeadingEdge: true, delta: -deficit);
            working[neighborId] = AdjustEdge(neighborRect, axis, isLeadingEdge: false, delta: -deficit);
        }

        return true;
    }

    /// <summary>
    /// Searches <paramref name="working"/> for the closest other placement that is full-span
    /// adjacent to <paramref name="current"/> along <paramref name="axis"/>, on either side.
    /// Returns <see langword="false"/> if none exists.
    /// </summary>
    private static bool TryFindFullSpanNeighbor(
        Dictionary<WindowId, Rect> working,
        WindowId id,
        Rect current,
        SplitOrientation axis,
        out WindowId neighborId,
        out bool neighborIsAfter)
    {
        bool found = false;
        double bestDistance = double.PositiveInfinity;
        neighborId = default;
        neighborIsAfter = false;

        foreach (KeyValuePair<WindowId, Rect> candidate in working)
        {
            if (candidate.Key == id || !IsFullSpanNeighbor(current, candidate.Value, axis))
            {
                continue;
            }

            double gapAfter = axis == SplitOrientation.Horizontal ? candidate.Value.Left - current.Right : candidate.Value.Top - current.Bottom;
            double gapBefore = axis == SplitOrientation.Horizontal ? current.Left - candidate.Value.Right : current.Top - candidate.Value.Bottom;

            if (gapAfter >= -Epsilon && gapAfter < bestDistance)
            {
                (bestDistance, neighborId, neighborIsAfter, found) = (gapAfter, candidate.Key, true, true);
            }

            if (gapBefore >= -Epsilon && gapBefore < bestDistance)
            {
                (bestDistance, neighborId, neighborIsAfter, found) = (gapBefore, candidate.Key, false, true);
            }
        }

        return found;
    }

    /// <summary>Whether <paramref name="b"/> shares <paramref name="a"/>'s full extent on the axis perpendicular to <paramref name="axis"/> -- the geometric signature of two tree siblings from a single split.</summary>
    private static bool IsFullSpanNeighbor(Rect a, Rect b, SplitOrientation axis) =>
        axis == SplitOrientation.Horizontal
            ? AlmostEqual(a.Top, b.Top) && AlmostEqual(a.Bottom, b.Bottom)
            : AlmostEqual(a.Left, b.Left) && AlmostEqual(a.Right, b.Right);

    /// <summary>Shifts one edge of <paramref name="rect"/> along <paramref name="axis"/> by <paramref name="delta"/> (positive = further in the increasing-coordinate direction).</summary>
    private static Rect AdjustEdge(Rect rect, SplitOrientation axis, bool isLeadingEdge, double delta) =>
        (axis, isLeadingEdge) switch
        {
            (SplitOrientation.Horizontal, true) => rect with { Left = rect.Left + delta },
            (SplitOrientation.Horizontal, false) => rect with { Right = rect.Right + delta },
            (SplitOrientation.Vertical, true) => rect with { Top = rect.Top + delta },
            (SplitOrientation.Vertical, false) => rect with { Bottom = rect.Bottom + delta },
            _ => throw new UnreachableException(),
        };

    /// <summary>
    /// Step 2: grows <paramref name="current"/> to at least <paramref name="required"/> on whichever
    /// axis (or axes) still fall short, centered on its original position, then translates (never
    /// shrinks) it to stay within <paramref name="workArea"/> where a simple translation achieves
    /// that -- DESIGN.md §6 permits overlapping sibling tiles here, but there is no reason to also
    /// let the window drift off the visible work area entirely when a translation avoids it.
    /// </summary>
    private static Rect InflateToMinimum(Rect current, LayoutConstraints required, Rect workArea)
    {
        double width = Math.Max(current.Width, required.MinWidth);
        double height = Math.Max(current.Height, required.MinHeight);

        double centerX = (current.Left + current.Right) / 2.0;
        double centerY = (current.Top + current.Bottom) / 2.0;

        double left = ClampPosition(centerX - (width / 2.0), width, workArea.Left, workArea.Right);
        double top = ClampPosition(centerY - (height / 2.0), height, workArea.Top, workArea.Bottom);

        return new Rect(left, top, left + width, top + height);
    }

    /// <summary>Translates a single <paramref name="position"/>/<paramref name="size"/> pair to stay within <c>[areaStart, areaEnd]</c> without ever shrinking <paramref name="size"/>.</summary>
    private static double ClampPosition(double position, double size, double areaStart, double areaEnd)
    {
        if (position < areaStart)
        {
            return areaStart;
        }

        return position + size > areaEnd ? areaEnd - size : position;
    }

    private static bool AlmostEqual(double a, double b) => Math.Abs(a - b) < Epsilon;
}
