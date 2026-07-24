namespace Bastion.Core;

/// <summary>
/// The learned, per-<see cref="RuleKey"/> effective-minimum-size cache (DESIGN.md §6: "Constraints
/// are learned, not queried"). Seeded from a Win32-side <c>GetSystemMetricsForDpi(SM_CXMINTRACK/
/// SM_CYMINTRACK)</c> floor (<c>Bastion.Win32.SystemMinTrackSizeReader</c>, GitHub issue #6), grown
/// by <see cref="RecordClamp"/> whenever the Placement Executor's verify-after-move readback
/// observes a clamp (<c>PlacementOutcome.ClampedTo</c>), and shrunk back over time by decay (see
/// <see cref="EffectiveMinSizeCacheOptions"/>'s remarks for the exact schedule) so a stale,
/// no-longer-accurate learned minimum does not survive an app update forever.
/// </summary>
/// <remarks>
/// <para>
/// <b>Purity boundary.</b> Zero Win32 surface (pure-core skill): depends only on an injected
/// <see cref="TimeProvider"/>, never <see cref="DateTime.Now"/> or a bare
/// <see cref="Task.Delay(TimeSpan)"/>. Builds and tests on Linux CI exactly like
/// <see cref="Reconciler"/> already does. The <c>GetSystemMetricsForDpi</c> seeding read is a
/// <c>Bastion.Win32</c>-side adapter (<c>SystemMinTrackSizeReader</c>) that runs once and hands its
/// result to this type's constructor -- this type never calls into Win32 itself.
/// </para>
/// <para>
/// <b>Scope call (GitHub issue #6): a standalone component, not wired to any call site.</b> This
/// type defines only the cache's own seed/update/decay/read API. It has no knowledge of
/// <c>PlacementOutcome</c>, the Reconciler, or the Placement Executor -- a future issue (explicitly
/// out of this issue's scope) is responsible for (a) resolving a live window's <see cref="RuleKey"/>
/// (<c>Bastion.Win32.RuleKeyResolver</c>, itself unwired today) and (b) calling
/// <see cref="RecordClamp"/> from the Executor's verify-after-move step and
/// <see cref="GetEffectiveMinSize"/> from wherever per-window minimums feed into layout solving
/// (e.g. <c>Bastion.Layout.MinSizeConflictLadder</c>'s <c>effectiveMinSizes</c> lookup, itself also
/// standalone -- see that type's remarks).
/// </para>
/// <para>
/// <b><see cref="RecordClamp"/> takes independently-nullable per-axis values, not one
/// <see cref="LayoutConstraints"/> blob (Codex review finding on this PR).</b> A verify-after-move
/// readback's clamp detection (<c>PlacementOutcome.ClampedTo</c>) can trip on only one axis --
/// e.g. a 400x800 request clamped to 500x800 means the app refused to shrink below 500 px of width,
/// but says nothing about its minimum height, since 800 was simply what was requested and happened
/// to be honored. If this method accepted a single whole-rect observation, a caller naively
/// forwarding the entire clamped rect would misrecord the <em>unclamped</em> axis too -- teaching a
/// permanent 800 px minimum height the app never actually demanded, which would later force
/// unnecessary overlap or auto-float for a window that would gladly have accepted a shorter tile.
/// Passing <see langword="null"/> for an axis that was not itself clamped is how a caller states "no
/// new evidence for this axis" -- that axis's own learned value and decay clock are left completely
/// untouched by the call (still free to decay normally against whatever it last learned), rather
/// than being reset or grown from a value that was never a real constraint.
/// </para>
/// <para>
/// <b>High-water-mark with decay, not a raw overwrite.</b> For whichever axis <em>is</em> supplied,
/// <see cref="RecordClamp"/> never simply replaces the learned minimum with the freshly observed
/// value -- it takes the larger of (that axis's existing learned value, decayed for whatever time
/// has elapsed since it was last updated) and (the fresh observation). A single anomalously-small
/// clamp reading can therefore never instantly undo previously-learned, larger evidence -- only the
/// passage of real time (decay, uncorroborated by any new clamp on that axis) can shrink it back
/// down. This directly implements DESIGN.md §6's "persisted per rule-key with decay so app updates
/// can shrink it": an app update is a calendar-time event, not a single-observation event.
/// </para>
/// <para>
/// <b><see cref="SystemFloor"/> is an absolute floor on every returned value, not merely a
/// before-first-observation default.</b> Both <see cref="GetEffectiveMinSize"/> and
/// <see cref="RecordClamp"/>'s own bookkeeping never report (or store) a value below
/// <see cref="SystemFloor"/> for either axis, even when a fresh clamp observation is smaller than
/// the floor (plausible in principle: <c>SM_CXMINTRACK</c>/<c>SM_CYMINTRACK</c> govern
/// <em>interactive</em> resize-drag limits, not programmatic <c>SetWindowPos</c> sizing, so a window
/// could in principle be programmatically sized below them). There is no product benefit to the
/// layout engine ever aiming for a tile smaller than what Windows itself treats as a sane general
/// minimum for a user-resizable window, so this cache treats the seed as a hard floor rather than a
/// mere default.
/// </para>
/// <para>
/// <b>Declined finding: durable, cross-daemon-restart persistence (Codex review finding on this
/// PR).</b> <see cref="_learned"/> is an ordinary in-memory <see cref="Dictionary{TKey, TValue}"/>
/// with no disk-backed hydration/save hooks, so restarting <c>bastiond</c> resets every rule key
/// back to <see cref="SystemFloor"/>. This is a deliberate scope boundary, not an oversight, for two
/// reasons. First, this issue's own acceptance criteria asks for a cache keyed "per-rule-key (not
/// per-HWND — must survive window recycling)" — read literally, that phrase is about surviving
/// <em>HWND recycling within one running daemon session</em> (the reason for keying by
/// <see cref="RuleKey"/> instead of <c>WindowId</c>/HWND at all — DESIGN.md §9's own "HWND recycling"
/// row is exactly this concern), not about surviving a full process restart; nothing in the
/// acceptance criteria names a file path, a serialization format, or restart-survival. Second, this
/// type deliberately mirrors <see cref="Reconciler"/>'s own established per-key +
/// <see cref="TimeProvider"/> bookkeeping pattern (its reassert-budget dictionary, GitHub issue #4,
/// already merged and reviewed) — an in-memory-only dictionary with the identical "no durable
/// persistence" shape — and DESIGN.md's "persisted per rule-key with decay" reads, consistent with
/// that precedent, as "retained across observations within the process's lifetime" (as opposed to a
/// single momentary reading), not "survives a process restart." Durable persistence, if ever wanted,
/// needs both a serialization format (naturally GitHub issue #9's JSONC/config surface) and a
/// load-on-startup hook (issue #10's composition root) — both explicitly out of this issue's scope;
/// building an ad hoc persistence mechanism now would likely need replacing once issue #9's real
/// config system lands anyway.
/// </para>
/// </remarks>
public sealed class EffectiveMinSizeCache
{
    private readonly TimeProvider _timeProvider;
    private readonly EffectiveMinSizeCacheOptions _options;
    private readonly Dictionary<RuleKey, LearnedState> _learned = [];

    public EffectiveMinSizeCache(LayoutConstraints systemFloor, TimeProvider timeProvider, EffectiveMinSizeCacheOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        EffectiveMinSizeCacheOptions resolvedOptions = options ?? EffectiveMinSizeCacheOptions.Default;

        // Manual checks, not the ArgumentOutOfRangeException.ThrowIfX(value, ...) helpers: matches
        // Reconciler/PlacementExecutor's own established rationale (both cite MA0015) -- every
        // value checked below is a member access on a local, not a genuine parameter of this
        // constructor, so CallerArgumentExpression could not derive a meaningful paramName anyway.
        if (!double.IsFinite(systemFloor.MinWidth) || systemFloor.MinWidth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(systemFloor), systemFloor.MinWidth, "LayoutConstraints.MinWidth must be finite and non-negative.");
        }

        if (!double.IsFinite(systemFloor.MinHeight) || systemFloor.MinHeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(systemFloor), systemFloor.MinHeight, "LayoutConstraints.MinHeight must be finite and non-negative.");
        }

        if (resolvedOptions.DecayInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.DecayInterval, "EffectiveMinSizeCacheOptions.DecayInterval must be positive.");
        }

        if (!double.IsFinite(resolvedOptions.DecayFactor) || resolvedOptions.DecayFactor <= 0 || resolvedOptions.DecayFactor > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.DecayFactor, "EffectiveMinSizeCacheOptions.DecayFactor must be in (0, 1].");
        }

        SystemFloor = systemFloor;
        _timeProvider = timeProvider;
        _options = resolvedOptions;
    }

    /// <summary>
    /// The <c>GetSystemMetricsForDpi(SM_CXMINTRACK/SM_CYMINTRACK)</c> seed every rule key starts
    /// from and every value this cache returns is floored at -- see this type's remarks.
    /// </summary>
    public LayoutConstraints SystemFloor { get; }

    /// <summary>
    /// The current effective minimum size for <paramref name="ruleKey"/>: <see cref="SystemFloor"/>
    /// on either axis that has never had a clamp recorded for it, otherwise that axis's learned
    /// value decayed for whatever time has elapsed since its own most recent
    /// <see cref="RecordClamp"/> call -- never below <see cref="SystemFloor"/>. A pure read: never
    /// mutates any stored state, so repeated calls with no intervening <see cref="RecordClamp"/> for
    /// an axis return a smoothly-shrinking sequence of values for it as <paramref name="ruleKey"/>'s
    /// decay clock (driven by the constructor-injected <see cref="TimeProvider"/>) advances.
    /// </summary>
    public LayoutConstraints GetEffectiveMinSize(RuleKey ruleKey)
    {
        if (!_learned.TryGetValue(ruleKey, out LearnedState? state))
        {
            return SystemFloor;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        double width = DecayAxis(state.CurrentMinWidth, SystemFloor.MinWidth, state.LastWidthUpdatedAt, now);
        double height = DecayAxis(state.CurrentMinHeight, SystemFloor.MinHeight, state.LastHeightUpdatedAt, now);
        return new LayoutConstraints(width, height);
    }

    /// <summary>
    /// Records an observed clamp -- e.g. from the Executor's verify-after-move readback
    /// (<c>PlacementOutcome.ClampedTo</c>) -- for <paramref name="ruleKey"/>. Pass
    /// <see langword="null"/> for whichever of <paramref name="clampedWidth"/>/
    /// <paramref name="clampedHeight"/> was <em>not</em> itself clamped this observation (see this
    /// type's remarks for why passing the whole verified rect regardless of which axis actually
    /// clamped is a correctness bug, not a convenience) -- a <see langword="null"/> axis is left
    /// completely untouched by this call. Passing <see langword="null"/> for both is a harmless
    /// no-op (equivalent to not calling this method at all); callers should not do so deliberately,
    /// since this method should only ever be invoked when at least one axis genuinely clamped.
    /// Returns the resulting effective minimum (equivalent to an immediately subsequent
    /// <see cref="GetEffectiveMinSize"/> call).
    /// </summary>
    public LayoutConstraints RecordClamp(RuleKey ruleKey, double? clampedWidth, double? clampedHeight)
    {
        if (clampedWidth is { } w && (!double.IsFinite(w) || w < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(clampedWidth), w, "clampedWidth must be finite and non-negative when supplied.");
        }

        if (clampedHeight is { } h && (!double.IsFinite(h) || h < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(clampedHeight), h, "clampedHeight must be finite and non-negative when supplied.");
        }

        if (clampedWidth is null && clampedHeight is null)
        {
            return GetEffectiveMinSize(ruleKey);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (!_learned.TryGetValue(ruleKey, out LearnedState? state))
        {
            state = new LearnedState
            {
                CurrentMinWidth = SystemFloor.MinWidth,
                CurrentMinHeight = SystemFloor.MinHeight,
                LastWidthUpdatedAt = now,
                LastHeightUpdatedAt = now,
            };
            _learned[ruleKey] = state;
        }

        if (clampedWidth is { } observedWidth)
        {
            double decayed = DecayAxis(state.CurrentMinWidth, SystemFloor.MinWidth, state.LastWidthUpdatedAt, now);
            state.CurrentMinWidth = Math.Max(decayed, Math.Max(SystemFloor.MinWidth, observedWidth));
            state.LastWidthUpdatedAt = now;
        }

        if (clampedHeight is { } observedHeight)
        {
            double decayed = DecayAxis(state.CurrentMinHeight, SystemFloor.MinHeight, state.LastHeightUpdatedAt, now);
            state.CurrentMinHeight = Math.Max(decayed, Math.Max(SystemFloor.MinHeight, observedHeight));
            state.LastHeightUpdatedAt = now;
        }

        return GetEffectiveMinSize(ruleKey);
    }

    /// <summary>
    /// Drops <paramref name="ruleKey"/>'s learned bookkeeping entirely, reverting it to
    /// <see cref="SystemFloor"/> on its next <see cref="GetEffectiveMinSize"/> call. Not yet wired to
    /// anything -- mirrors <c>PlacementExecutor.Purge</c>/<see cref="Reconciler"/>'s own
    /// <c>PruneReassertBudgets</c>'s "hygiene hook for a future caller" shape (e.g. a rules-file
    /// reload or an explicit cache-reset command), rather than solving an unbounded-growth problem
    /// that is not a practical concern here: this dictionary is keyed per distinct app identity, not
    /// per window/HWND, so it is already bounded by the number of distinct applications a user runs,
    /// not by window churn.
    /// </summary>
    public void Purge(RuleKey ruleKey) => _learned.Remove(ruleKey);

    /// <summary>Decays a single axis's learned value toward <paramref name="floor"/> given how long it has been since <paramref name="lastUpdatedAt"/>.</summary>
    private double DecayAxis(double current, double floor, DateTimeOffset lastUpdatedAt, DateTimeOffset now)
    {
        TimeSpan elapsed = now - lastUpdatedAt;

        // A non-positive elapsed span (clock non-monotonicity, or reading immediately after a
        // RecordClamp at the same instant) must never flow into Math.Pow as a negative exponent --
        // that would GROW the excess instead of decaying it. Treat it as "no decay has occurred yet".
        double multiplier = elapsed <= TimeSpan.Zero ? 1.0 : Math.Pow(_options.DecayFactor, elapsed / _options.DecayInterval);
        double decayed = floor + ((current - floor) * multiplier);

        // Defensive floor against floating-point overshoot -- current >= floor is an invariant
        // RecordClamp maintains, and multiplier is always in (0, 1], so this should never actually
        // engage; belt-and-braces only (matches SplitTreeLayout's own ClampRatio -> Math.Clamp
        // precedent).
        return Math.Max(floor, decayed);
    }

    /// <summary>
    /// One rule key's learned bookkeeping, tracked per axis so an observation on one axis never
    /// disturbs the other's learned value or decay clock (see this type's remarks).
    /// </summary>
    private sealed class LearnedState
    {
        public double CurrentMinWidth;
        public double CurrentMinHeight;
        public DateTimeOffset LastWidthUpdatedAt;
        public DateTimeOffset LastHeightUpdatedAt;
    }
}
