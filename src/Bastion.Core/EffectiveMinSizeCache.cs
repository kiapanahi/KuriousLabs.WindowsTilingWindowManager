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
/// <b>High-water-mark with decay, not a raw overwrite.</b> <see cref="RecordClamp"/> never simply
/// replaces the learned minimum with the freshly observed value -- it takes the larger of (the
/// existing learned value, decayed for whatever time has elapsed since it was last updated) and
/// (the fresh observation). A single anomalously-small clamp reading (plausible: a verify-after-move
/// readback captures one specific requested target, not an exhaustive probe of the window's true
/// minimum) can therefore never instantly undo previously-learned, larger evidence -- only the
/// passage of real time (decay, uncorroborated by any new clamp) can shrink it back down. This
/// directly implements DESIGN.md §6's "persisted per rule-key with decay so app updates can shrink
/// it": an app update is a calendar-time event, not a single-observation event.
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
    /// if no clamp has ever been recorded for it, otherwise its learned value decayed for whatever
    /// time has elapsed since the most recent <see cref="RecordClamp"/> call -- never below
    /// <see cref="SystemFloor"/>. A pure read: never mutates any stored state, so repeated calls
    /// with no intervening <see cref="RecordClamp"/> return a smoothly-shrinking sequence of values
    /// as <paramref name="ruleKey"/>'s decay clock (driven by the constructor-injected
    /// <see cref="TimeProvider"/>) advances.
    /// </summary>
    public LayoutConstraints GetEffectiveMinSize(RuleKey ruleKey)
    {
        if (!_learned.TryGetValue(ruleKey, out LearnedState? state))
        {
            return SystemFloor;
        }

        return Decay(state, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Records an observed clamp -- the Executor's verify-after-move readback, e.g.
    /// <c>PlacementOutcome.ClampedTo</c> converted to a <see cref="LayoutConstraints"/>; call only
    /// when a clamp genuinely occurred, not on every solved/requested size -- for
    /// <paramref name="ruleKey"/>, growing its learned minimum to the larger of its already-decayed
    /// current value and <paramref name="observedMinimum"/>, and resetting its decay clock to now.
    /// Returns the resulting effective minimum (equivalent to an immediately subsequent
    /// <see cref="GetEffectiveMinSize"/> call).
    /// </summary>
    public LayoutConstraints RecordClamp(RuleKey ruleKey, LayoutConstraints observedMinimum)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (!_learned.TryGetValue(ruleKey, out LearnedState? state))
        {
            state = new LearnedState
            {
                CurrentMinWidth = SystemFloor.MinWidth,
                CurrentMinHeight = SystemFloor.MinHeight,
                LastUpdatedAt = now,
            };
            _learned[ruleKey] = state;
        }

        LayoutConstraints decayed = Decay(state, now);
        double newWidth = Math.Max(decayed.MinWidth, Math.Max(SystemFloor.MinWidth, observedMinimum.MinWidth));
        double newHeight = Math.Max(decayed.MinHeight, Math.Max(SystemFloor.MinHeight, observedMinimum.MinHeight));

        state.CurrentMinWidth = newWidth;
        state.CurrentMinHeight = newHeight;
        state.LastUpdatedAt = now;

        return new LayoutConstraints(newWidth, newHeight);
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

    private LayoutConstraints Decay(LearnedState state, DateTimeOffset now)
    {
        TimeSpan elapsed = now - state.LastUpdatedAt;

        // A non-positive elapsed span (clock non-monotonicity, or reading immediately after a
        // RecordClamp at the same instant) must never flow into Math.Pow as a negative exponent --
        // that would GROW the excess instead of decaying it. Treat it as "no decay has occurred yet".
        double multiplier = elapsed <= TimeSpan.Zero ? 1.0 : Math.Pow(_options.DecayFactor, elapsed / _options.DecayInterval);

        double width = SystemFloor.MinWidth + ((state.CurrentMinWidth - SystemFloor.MinWidth) * multiplier);
        double height = SystemFloor.MinHeight + ((state.CurrentMinHeight - SystemFloor.MinHeight) * multiplier);

        // Defensive floor against floating-point overshoot -- state.CurrentMinWidth/Height >=
        // SystemFloor is an invariant RecordClamp maintains, and multiplier is always in (0, 1], so
        // this should never actually engage; belt-and-braces only (matches SplitTreeLayout's own
        // ClampRatio -> Math.Clamp precedent).
        return new LayoutConstraints(Math.Max(SystemFloor.MinWidth, width), Math.Max(SystemFloor.MinHeight, height));
    }

    /// <summary>One rule key's learned bookkeeping: the last-recorded (pre-decay) minimum and when it was recorded.</summary>
    private sealed class LearnedState
    {
        public double CurrentMinWidth;
        public double CurrentMinHeight;
        public DateTimeOffset LastUpdatedAt;
    }
}
