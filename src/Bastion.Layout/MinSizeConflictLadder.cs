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
/// <b>Step 1's scope: single-donor redistribution only, but tried against every eligible donor.</b>
/// "Redistributing along the split axis" is reinterpreted here, geometry-only, as: consider every
/// other placement that shares the constrained window's full extent on the perpendicular axis,
/// touches it along the deficient axis, and has nothing else physically between them (the geometric
/// signature of a tree sibling produced by a single split); among those, try the closest first, and
/// redistribute the exact deficit from the first one that has enough capacity to give it up without
/// falling below its own required minimum. Trying every eligible donor (rather than stopping at the
/// single closest one regardless of its capacity) is itself a fix -- Codex review finding on this
/// PR: a nearer donor that is already at its own minimum must not block a farther donor with ample
/// room. If none of them can absorb it, step 1 declines for that axis and steps 2/3 decide instead.
/// Never redistributing from more than one donor <em>at a time</em> for a single deficit remains a
/// deliberate scope boundary, not a missing case: DESIGN.md §6 itself only asks for a best-effort
/// redistribution step before falling back to overlap/float, and a fully general N-donor
/// simultaneous-split solver would be exactly the kind of aggregate, cross-subtree computation
/// <see cref="SplitTreeLayout"/>'s own design rejected.
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

        List<WindowId> redistributedIds = [];
        List<WindowId> overlappedIds = [];
        ImmutableArray<AutoFloatDecision>.Builder autoFloats = ImmutableArray.CreateBuilder<AutoFloatDecision>();

        foreach (WindowPlacement placement in placements)
        {
            ProcessWindow(working, placement.WindowId, defaultMinimum, effectiveMinSizes, workArea, resolvedOptions, redistributedIds, overlappedIds, autoFloats);
        }

        // Bounds for Redistributions/Overlaps are resolved from `working` only now, after every
        // window has been processed -- never captured inline at each window's own processing time
        // (Codex review finding on this PR): a window recorded here as redistributed/overlapped can
        // still be mutated afterward by a LATER window's own successful step-1 redistribution
        // (using it as a donor neighbor), which would otherwise leave these outcomes reporting
        // stale bounds that disagree with what Placements ultimately records for the same window.
        return new MinSizeConflictResult
        {
            Placements = BuildFinalPlacements(placements, working),
            Redistributions = BuildOutcomes(redistributedIds, working, static (id, bounds) => new RedistributedPlacement(id, bounds)),
            Overlaps = BuildOutcomes(overlappedIds, working, static (id, bounds) => new BoundedOverlapPlacement(id, bounds)),
            AutoFloats = autoFloats.ToImmutable(),
        };
    }

    private static ImmutableArray<T> BuildOutcomes<T>(List<WindowId> ids, Dictionary<WindowId, Rect> working, Func<WindowId, Rect, T> factory)
    {
        ImmutableArray<T>.Builder builder = ImmutableArray.CreateBuilder<T>(ids.Count);
        foreach (WindowId id in ids)
        {
            builder.Add(factory(id, working[id]));
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Runs the whole ladder for one window: skip entirely if its current tile already satisfies
    /// its requirement (regardless of how large that requirement is relative to
    /// <paramref name="workArea"/> -- Codex review finding on this PR: a lone/monocle tile that
    /// already meets an oversized cache-learned minimum has no conflict to resolve and must not be
    /// floated); otherwise auto-float (step 3) if the requirement is unreasonable relative to
    /// <paramref name="workArea"/>, else attempt step 1 on each deficient axis and fall back to
    /// step 2 (bounded overlap) for whatever step 1 could not resolve. Mutates
    /// <paramref name="working"/> and records <paramref name="id"/>'s outcome in
    /// <paramref name="redistributedIds"/>/<paramref name="overlappedIds"/>/
    /// <paramref name="autoFloats"/> as needed (bounds for the first two are resolved by the caller
    /// after every window has been processed, not here -- see <see cref="Resolve"/>'s remarks).
    /// </summary>
    private static void ProcessWindow(
        Dictionary<WindowId, Rect> working,
        WindowId id,
        LayoutConstraints defaultMinimum,
        IReadOnlyDictionary<WindowId, LayoutConstraints> effectiveMinSizes,
        Rect workArea,
        MinSizeConflictLadderOptions options,
        List<WindowId> redistributedIds,
        List<WindowId> overlappedIds,
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
            working[id] = InflateToMinimum(current, required, workArea);
            overlappedIds.Add(id);
        }
        else if (widthRedistributed || heightRedistributed)
        {
            redistributedIds.Add(id);
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
    /// Attempts step 1 for one axis of <paramref name="id"/>'s deficit: find the closest eligible
    /// donor (<see cref="TryFindDonor"/>, which already verified capacity) and move exactly
    /// <paramref name="deficit"/> from it to <paramref name="id"/>. Mutates
    /// <paramref name="working"/> in place and returns <see langword="true"/> on success; leaves it
    /// untouched and returns <see langword="false"/> if no eligible donor exists at all (falling
    /// through to steps 2/3 is the caller's job).
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
        if (!TryFindDonor(working, id, current, axis, deficit, defaultMinimum, effectiveMinSizes, out WindowId donorId, out bool donorIsAfter))
        {
            return false;
        }

        Rect donorRect = working[donorId];
        if (donorIsAfter)
        {
            working[id] = AdjustEdge(current, axis, isLeadingEdge: false, delta: deficit);
            working[donorId] = AdjustEdge(donorRect, axis, isLeadingEdge: true, delta: deficit);
        }
        else
        {
            working[id] = AdjustEdge(current, axis, isLeadingEdge: true, delta: -deficit);
            working[donorId] = AdjustEdge(donorRect, axis, isLeadingEdge: false, delta: -deficit);
        }

        return true;
    }

    /// <summary>
    /// Searches <paramref name="working"/> for every other placement that is full-span adjacent to
    /// <paramref name="current"/> along <paramref name="axis"/> (either side) with nothing else
    /// occupying the space between them, and returns the closest one that also has enough capacity
    /// to give up <paramref name="deficit"/> without falling below its own required minimum.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A full-span match alone is not sufficient (Codex review finding on this PR): in, say, a
    /// horizontal layout whose middle subtree is itself split vertically, the two full-height
    /// tiles flanking that middle subtree both satisfy <see cref="IsFullSpanNeighbor"/> with each
    /// other even though the (non-full-span) middle tiles sit physically between them. Accepting
    /// either flanking tile as "adjacent" on the strength of a merely-nonnegative gap distance
    /// would grow one of them straight through the middle tiles, corrupting them, while this
    /// method's caller believes it just performed a clean, non-overlapping redistribution. Every
    /// candidate is therefore additionally checked via <see cref="IsAnyOtherPlacementBetween"/>.
    /// </para>
    /// <para>
    /// Capacity alone does not stop the search at the first geometrically-eligible candidate either
    /// (a second Codex review finding on this PR): every eligible candidate is collected and ordered
    /// closest-first, then each is tried in turn until one has enough room to give up the deficit --
    /// a nearer donor already at its own required minimum must not block a farther donor with ample
    /// space to spare.
    /// </para>
    /// </remarks>
    private static bool TryFindDonor(
        Dictionary<WindowId, Rect> working,
        WindowId id,
        Rect current,
        SplitOrientation axis,
        double deficit,
        LayoutConstraints defaultMinimum,
        IReadOnlyDictionary<WindowId, LayoutConstraints> effectiveMinSizes,
        out WindowId donorId,
        out bool donorIsAfter)
    {
        List<(WindowId Id, bool IsAfter, double Distance)> candidates = FindEligibleCandidates(working, id, current, axis);
        candidates.Sort(static (x, y) => x.Distance.CompareTo(y.Distance));

        foreach ((WindowId candidateId, bool isAfter, _) in candidates)
        {
            LayoutConstraints candidateRequired = Required(candidateId, defaultMinimum, effectiveMinSizes);
            if (HasCapacity(working[candidateId], axis, deficit, candidateRequired))
            {
                donorId = candidateId;
                donorIsAfter = isAfter;
                return true;
            }
        }

        donorId = default;
        donorIsAfter = false;
        return false;
    }

    /// <summary>Every other placement in <paramref name="working"/> that is geometrically eligible (full-span, unobstructed) to donate along <paramref name="axis"/>, with its signed distance and side.</summary>
    private static List<(WindowId Id, bool IsAfter, double Distance)> FindEligibleCandidates(
        Dictionary<WindowId, Rect> working, WindowId id, Rect current, SplitOrientation axis)
    {
        List<(WindowId Id, bool IsAfter, double Distance)> candidates = [];
        foreach (KeyValuePair<WindowId, Rect> candidate in working)
        {
            if (candidate.Key == id || !IsFullSpanNeighbor(current, candidate.Value, axis))
            {
                continue;
            }

            double gapAfter = axis == SplitOrientation.Horizontal ? candidate.Value.Left - current.Right : candidate.Value.Top - current.Bottom;
            double gapBefore = axis == SplitOrientation.Horizontal ? current.Left - candidate.Value.Right : current.Top - candidate.Value.Bottom;

            if (gapAfter >= -Epsilon && !IsAnyOtherPlacementBetween(working, id, candidate.Key, current, candidate.Value, axis, isAfter: true))
            {
                candidates.Add((candidate.Key, true, gapAfter));
            }

            if (gapBefore >= -Epsilon && !IsAnyOtherPlacementBetween(working, id, candidate.Key, current, candidate.Value, axis, isAfter: false))
            {
                candidates.Add((candidate.Key, false, gapBefore));
            }
        }

        return candidates;
    }

    /// <summary>Whether <paramref name="candidateRect"/> can give up <paramref name="deficit"/> along <paramref name="axis"/> without falling below <paramref name="candidateRequired"/> (or below zero).</summary>
    private static bool HasCapacity(Rect candidateRect, SplitOrientation axis, double deficit, LayoutConstraints candidateRequired)
    {
        double requiredSize = axis == SplitOrientation.Horizontal ? candidateRequired.MinWidth : candidateRequired.MinHeight;
        double currentSize = axis == SplitOrientation.Horizontal ? candidateRect.Width : candidateRect.Height;
        double sizeAfterShrink = currentSize - deficit;
        return sizeAfterShrink > 0 && sizeAfterShrink >= requiredSize - Epsilon;
    }

    /// <summary>Whether <paramref name="b"/> shares <paramref name="a"/>'s full extent on the axis perpendicular to <paramref name="axis"/> -- the geometric signature of two tree siblings from a single split.</summary>
    private static bool IsFullSpanNeighbor(Rect a, Rect b, SplitOrientation axis) =>
        axis == SplitOrientation.Horizontal
            ? AlmostEqual(a.Top, b.Top) && AlmostEqual(a.Bottom, b.Bottom)
            : AlmostEqual(a.Left, b.Left) && AlmostEqual(a.Right, b.Right);

    /// <summary>
    /// Whether some placement other than <paramref name="id"/>/<paramref name="candidateId"/>
    /// occupies the strip of space between <paramref name="current"/> and
    /// <paramref name="candidateRect"/> along <paramref name="axis"/> -- if so, they are not truly
    /// adjacent regardless of how their raw gap distance compares to other candidates (see
    /// <see cref="TryFindDonor"/>'s own remarks for the counterexample this guards against). Uses
    /// <see cref="Rect.IntersectsWith"/>'s existing strict-inequality semantics, so a third
    /// placement that merely touches the bridge's boundary (the ordinary case for a
    /// genuinely-adjacent gap) is correctly not treated as an obstruction.
    /// </summary>
    private static bool IsAnyOtherPlacementBetween(
        Dictionary<WindowId, Rect> working,
        WindowId id,
        WindowId candidateId,
        Rect current,
        Rect candidateRect,
        SplitOrientation axis,
        bool isAfter)
    {
        Rect bridge = BuildBridge(current, candidateRect, axis, isAfter);
        foreach (KeyValuePair<WindowId, Rect> other in working)
        {
            if (other.Key != id && other.Key != candidateId && bridge.IntersectsWith(other.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The rectangle spanning the space between <paramref name="current"/> and <paramref name="candidateRect"/> along <paramref name="axis"/>, at their shared (full-span) perpendicular extent.</summary>
    private static Rect BuildBridge(Rect current, Rect candidateRect, SplitOrientation axis, bool isAfter) =>
        (axis, isAfter) switch
        {
            (SplitOrientation.Horizontal, true) => new Rect(current.Right, current.Top, candidateRect.Left, current.Bottom),
            (SplitOrientation.Horizontal, false) => new Rect(candidateRect.Right, current.Top, current.Left, current.Bottom),
            (SplitOrientation.Vertical, true) => new Rect(current.Left, current.Bottom, current.Right, candidateRect.Top),
            (SplitOrientation.Vertical, false) => new Rect(current.Left, candidateRect.Bottom, current.Right, current.Top),
            _ => throw new UnreachableException(),
        };

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
