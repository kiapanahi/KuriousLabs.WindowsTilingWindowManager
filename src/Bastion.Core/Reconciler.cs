using System.Collections.Immutable;
using System.Threading.Channels;

namespace Bastion.Core;

/// <summary>
/// The Reconciler (DESIGN.md §3.4): the single-threaded actor that owns <see cref="DesiredState"/>
/// and <see cref="ObservedState"/> and converges them on three triggers — a coalesced intent, the
/// 5 s heartbeat, or a distrust escalation (verify-after-move mismatch, channel overflow,
/// <c>WM_DISPLAYCHANGE</c>, Explorer restart). All three funnel through the same mechanism:
/// <see cref="RequestReconcileNow"/> wakes <see cref="RunAsync"/>'s loop sooner than its next
/// heartbeat tick, and every wake — whatever caused it — runs the identical
/// <see cref="ConvergeOnceAsync"/> pass. This is a direct consequence of DESIGN.md §1's "reads are
/// truth; events are scheduling hints": no trigger's own payload is ever trusted, so none of them
/// need bespoke handling here beyond timing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-threaded-actor invariant, enforced, not assumed.</b> <see cref="ConvergeOnceAsync"/>
/// serializes on a <see cref="SemaphoreSlim"/> (mutual exclusion across an
/// <see langword="await"/> boundary, never a <see cref="System.Threading.Lock"/>, which cannot be
/// held across one) so a caller invoking it directly (e.g. a test, or a future distrust-escalation
/// path) while <see cref="RunAsync"/> is also running can never interleave two convergence passes'
/// state mutations. DESIGN.md §3 states this invariant architecturally ("All state mutation flows
/// through the single-threaded Reconciler actor"); this type makes it structurally true rather
/// than trusting every caller to serialize their own calls.
/// </para>
/// <para>
/// <b>Purity boundary.</b> This type has zero Win32/COM surface (pure-core skill): it depends only
/// on <see cref="IWindowSystem"/> (an interface implemented in <c>Bastion.Win32</c>),
/// <see cref="ILayoutEngine"/> (pure — implementations live in <c>Bastion.Layout</c>, e.g.
/// <c>DwindleLayoutEngine</c>), and an injected <see cref="TimeProvider"/> — never
/// <c>DateTime.Now</c>, <c>Task.Delay</c>, or a bare <see cref="PeriodicTimer"/> constructed
/// without one. It builds and tests on Linux CI (docs/engineering/testing.md §1) exactly like
/// <c>Bastion.Layout</c> already does.
/// </para>
/// <para>
/// <b>Diffing is flat, not tree-recursive, by construction.</b> <see cref="DesiredWorkspace.Windows"/>
/// is a flat, ordered list, and every loop in <see cref="ConvergeOnceAsync"/> is flat iteration —
/// there is no new recursive traversal in this type for docs/engineering/daemon-architecture.md
/// §6's depth-cap rule to apply to directly. The one place actual tree recursion occurs in the
/// pipeline this type invokes is inside <see cref="ILayoutEngine.Solve"/> (for
/// <c>DwindleLayoutEngine</c>, walking a <c>SplitTree</c> via <c>SplitTreeLayout.Solve</c>) —
/// already bounded by <c>SplitTree.MaxDepth</c>, a hard, config-independent, throwing cap from
/// GitHub issue #37, which <c>DwindleLayoutEngine.Solve</c> additionally pre-empts by degrading to
/// a flat stacked layout once the window count alone would exceed it. See
/// <see cref="DesiredWorkspace"/>'s remarks for why this type reuses <see cref="ILayoutEngine"/>
/// rather than holding/walking a persisted tree itself.
/// </para>
/// </remarks>
public sealed class Reconciler : IDisposable
{
    private static readonly BoundedChannelOptions s_wakeChannelOptions = new(capacity: 1)
    {
        // A pending wake is enough regardless of how many RequestReconcileNow() calls coalesce
        // into it before the loop consumes one -- DropWrite mirrors the ingest/intent channels'
        // own established rationale (docs/engineering/concurrency-performance.md §1): the
        // incoming (redundant) signal is sacrificed, never the one already queued.
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false, // arbitrary pump/coalescer threads all call RequestReconcileNow()
        AllowSynchronousContinuations = false,
    };

    private readonly IWindowSystem _windowSystem;
    private readonly ILayoutEngine _layoutEngine;
    private readonly TimeProvider _timeProvider;
    private readonly ReconcilerOptions _options;
    private readonly Channel<byte> _wakeChannel = Channel.CreateBounded<byte>(s_wakeChannelOptions);
    private readonly SemaphoreSlim _convergenceGate = new(1, 1);
    private readonly Dictionary<WindowId, ReassertBudgetState> _reassertBudgets = [];
    private bool _disposed;

    public Reconciler(IWindowSystem windowSystem, ILayoutEngine layoutEngine, TimeProvider timeProvider, ReconcilerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(windowSystem);
        ArgumentNullException.ThrowIfNull(layoutEngine);
        ArgumentNullException.ThrowIfNull(timeProvider);

        ReconcilerOptions resolvedOptions = options ?? ReconcilerOptions.Default;

        // Manual checks, not the ArgumentOutOfRangeException.ThrowIfX(value, ...) helpers: those
        // rely on CallerArgumentExpression to derive paramName from the value expression itself,
        // and every value here is a member access on the local `resolvedOptions` rather than a
        // parameter of this constructor -- Meziantou.Analyzer's MA0015 correctly flags that
        // mismatch (the derived "paramName" would read "resolvedOptions.HeartbeatInterval" rather
        // than a real parameter). nameof(options) is the actual source of every value below, even
        // after defaulting.
        if (resolvedOptions.HeartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.HeartbeatInterval, "ReconcilerOptions.HeartbeatInterval must be positive.");
        }

        if (resolvedOptions.ReassertBudgetPerWindow < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.ReassertBudgetPerWindow, "ReconcilerOptions.ReassertBudgetPerWindow must be at least 1.");
        }

        if (resolvedOptions.ReassertBudgetWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.ReassertBudgetWindow, "ReconcilerOptions.ReassertBudgetWindow must be positive.");
        }

        if (resolvedOptions.PositionToleranceDevicePixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.PositionToleranceDevicePixels, "ReconcilerOptions.PositionToleranceDevicePixels must be non-negative.");
        }

        _windowSystem = windowSystem;
        _layoutEngine = layoutEngine;
        _timeProvider = timeProvider;
        _options = resolvedOptions;

        DesiredState = DesiredState.Empty;
        ObservedState = ImmutableArray<ObservedWindow>.Empty;
        LastPlacementPlan = ImmutableArray<PlacementInstruction>.Empty;
    }

    /// <summary>The current desired arrangement. Never mutated in place — every convergence pass replaces it wholesale.</summary>
    public DesiredState DesiredState { get; private set; }

    /// <summary>
    /// The last authoritative read, one entry per currently tracked window (DESIGN.md §3.4).
    /// Rebuilt wholesale every convergence pass via <see cref="IWindowSystem.ReadAllAsync"/> — never
    /// <see langword="default"/> (docs/engineering/daemon-architecture.md §5).
    /// </summary>
    public ImmutableArray<ObservedWindow> ObservedState { get; private set; }

    /// <summary>The placement plan the most recent convergence pass produced (GitHub issue #4's deliverable; issue #5 executes it).</summary>
    public ImmutableArray<PlacementInstruction> LastPlacementPlan { get; private set; }

    /// <summary>
    /// Registers or replaces the workspace at <paramref name="key"/>, seeding its work
    /// area/constraints/gaps. v0.1 has no monitor topology service (GitHub issue #16) to discover
    /// this automatically, so callers (the eventual composition root, GitHub issue #10; tests here)
    /// supply it directly. An existing workspace's window list is preserved across calls that only
    /// change geometry.
    /// </summary>
    public void SetWorkspace(WorkspaceKey key, Rect workArea, LayoutConstraints constraints = default, LayoutGaps gaps = default)
    {
        ImmutableArray<WindowId> existingWindows = DesiredState.Workspaces.TryGetValue(key, out DesiredWorkspace? existing)
            ? existing.Windows
            : ImmutableArray<WindowId>.Empty;

        DesiredState = DesiredState.WithWorkspace(
            key,
            new DesiredWorkspace { Windows = existingWindows, WorkArea = workArea, Constraints = constraints, Gaps = gaps });
    }

    /// <summary>
    /// Requests an out-of-band convergence pass at the next opportunity — the coalesced-intent and
    /// distrust-escalation convergence triggers both fold into this one call (DESIGN.md §3.4):
    /// "reads are truth, events are hints" means the trigger's own reason never needs to reach
    /// this far, only its timing does. Cheap, synchronous, and safe to call from any thread — the
    /// adapter ring's real <c>IReconcileNowSignal</c> implementation (this issue resolves the TODO
    /// both the WinEvent ingest pump's and the Coalescer's own doc comments left) is a thin
    /// same-assembly forward to this method.
    /// </summary>
    public void RequestReconcileNow() => _wakeChannel.Writer.TryWrite(0);

    /// <summary>
    /// Runs the convergence loop until <paramref name="cancellationToken"/> is canceled: waits for
    /// either the next heartbeat tick or a pending <see cref="RequestReconcileNow"/> signal,
    /// whichever comes first, then runs exactly one <see cref="ConvergeOnceAsync"/> pass before
    /// looping. Exactly one <see cref="PeriodicTimer.WaitForNextTickAsync(CancellationToken)"/>
    /// call is ever in flight (docs/engineering/concurrency-performance.md §4: the timer "may only
    /// be used by one consumer at a time") — the loop only arms a fresh tick-wait once the
    /// previous one has actually completed, never speculatively alongside a wake-signal race.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var heartbeat = new PeriodicTimer(_options.HeartbeatInterval, _timeProvider);
        Task<bool> heartbeatTick = heartbeat.WaitForNextTickAsync(cancellationToken).AsTask();
        Task<bool> wakeSignal = _wakeChannel.Reader.WaitToReadAsync(cancellationToken).AsTask();

        try
        {
            while (true)
            {
                Task<bool> completed = await Task.WhenAny(heartbeatTick, wakeSignal).ConfigureAwait(false);
                bool stillRunning;

                if (completed == heartbeatTick)
                {
                    stillRunning = await heartbeatTick.ConfigureAwait(false);
                    heartbeatTick = heartbeat.WaitForNextTickAsync(cancellationToken).AsTask();
                }
                else
                {
                    stillRunning = await wakeSignal.ConfigureAwait(false);
                    if (stillRunning)
                    {
                        _ = _wakeChannel.Reader.TryRead(out _);
                    }

                    wakeSignal = _wakeChannel.Reader.WaitToReadAsync(cancellationToken).AsTask();
                }

                if (!stillRunning)
                {
                    break;
                }

                await ConvergeOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown (DESIGN.md §3.10) -- observe whichever of the two race tasks
            // did not win, so its own (also-expected) cancellation never becomes an unobserved
            // task exception (docs/engineering/daemon-architecture.md §6: UnobservedTaskException
            // is diagnostics-only, not a safety net; every task gets an explicit observer).
            await ObserveIgnoringCancellationAsync(heartbeatTick).ConfigureAwait(false);
            await ObserveIgnoringCancellationAsync(wakeSignal).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one convergence pass: a fresh <see cref="IWindowSystem.ReadAllAsync"/> read, a desired-
    /// window-set sync against it, a solve-and-diff per workspace, and reassert-budget-gated
    /// placement decisions. Safe to call directly (e.g. from a test, or a future distrust-escalation
    /// caller) even while <see cref="RunAsync"/>'s own loop is running concurrently — every call
    /// serializes through the same gate, so the single-threaded-actor invariant (DESIGN.md §3)
    /// holds structurally rather than by caller discipline.
    /// </summary>
    public async Task<ImmutableArray<PlacementInstruction>> ConvergeOnceAsync(CancellationToken cancellationToken = default)
    {
        await _convergenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ImmutableArray<ObservedWindow> observed = await _windowSystem.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            ObservedState = observed;

            DesiredState = SyncDesiredWindowSet(DesiredState, observed);
            PruneReassertBudgets(observed);

            Dictionary<WindowId, ObservedWindow> observedById = new(observed.Length);
            foreach (ObservedWindow window in observed)
            {
                observedById[window.WindowId] = window;
            }

            ImmutableArray<PlacementInstruction>.Builder plan = ImmutableArray.CreateBuilder<PlacementInstruction>();

            foreach (DesiredWorkspace workspace in DesiredState.Workspaces.Values)
            {
                if (workspace.Windows.IsEmpty)
                {
                    continue;
                }

                IReadOnlyList<WindowPlacement> solved =
                    _layoutEngine.Solve(workspace.Windows, workspace.WorkArea, workspace.Constraints, workspace.Gaps);

                foreach (WindowPlacement placement in solved)
                {
                    if (!observedById.TryGetValue(placement.WindowId, out ObservedWindow observedWindow)
                        || RectsMatch(placement.Bounds, observedWindow.FrameBounds))
                    {
                        continue;
                    }

                    if (TryConsumeReassertBudget(placement.WindowId))
                    {
                        plan.Add(PlacementInstruction.Move(placement.WindowId, placement.Bounds));
                    }
                    else
                    {
                        plan.Add(PlacementInstruction.Untile(placement.WindowId));
                        DesiredState = DesiredState.WithWindowUntiled(placement.WindowId);
                    }
                }
            }

            LastPlacementPlan = plan.ToImmutable();
            return LastPlacementPlan;
        }
        finally
        {
            _convergenceGate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wakeChannel.Writer.TryComplete();
        _convergenceGate.Dispose();
    }

    private static async Task ObserveIgnoringCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: this task shares RunAsync's own cancellation token.
        }
    }

    /// <summary>
    /// Reconciles <paramref name="state"/>'s workspace membership against a fresh
    /// <paramref name="observed"/> snapshot: drops anything no longer observed at all or that has
    /// become cloaked (DESIGN.md §3.3/§4 — cloaked windows stay in <see cref="ObservedState"/>,
    /// never in <see cref="DesiredState"/>), drops untiled-window bookkeeping for anything that has
    /// vanished, and auto-admits any newly-eligible window into <see cref="WorkspaceKey.Default"/>
    /// if that workspace exists. v0.1 has no monitor/workspace assignment policy (GitHub issue #16)
    /// beyond this single default destination.
    /// </summary>
    private static DesiredState SyncDesiredWindowSet(DesiredState state, ImmutableArray<ObservedWindow> observed)
    {
        var stillObserved = new HashSet<WindowId>(observed.Length);
        var eligibleForTiling = new HashSet<WindowId>(observed.Length);
        foreach (ObservedWindow window in observed)
        {
            stillObserved.Add(window.WindowId);
            if (!window.IsCloaked)
            {
                eligibleForTiling.Add(window.WindowId);
            }
        }

        DesiredState result = state;

        foreach (KeyValuePair<WorkspaceKey, DesiredWorkspace> entry in state.Workspaces)
        {
            ImmutableArray<WindowId> surviving = entry.Value.Windows.RemoveAll(id => !eligibleForTiling.Contains(id));
            if (surviving.Length != entry.Value.Windows.Length)
            {
                result = result.WithWorkspace(entry.Key, entry.Value with { Windows = surviving });
            }
        }

        if (result.UntiledWindows.Any(id => !stillObserved.Contains(id)))
        {
            result = result with { UntiledWindows = result.UntiledWindows.Where(stillObserved.Contains).ToImmutableHashSet() };
        }

        if (result.Workspaces.TryGetValue(WorkspaceKey.Default, out DesiredWorkspace? defaultWorkspace))
        {
            var alreadyDesired = new HashSet<WindowId>();
            foreach (DesiredWorkspace workspace in result.Workspaces.Values)
            {
                alreadyDesired.UnionWith(workspace.Windows);
            }

            List<WindowId>? newlyEligible = null;
            foreach (WindowId id in eligibleForTiling)
            {
                if (alreadyDesired.Contains(id) || result.UntiledWindows.Contains(id))
                {
                    continue;
                }

                (newlyEligible ??= []).Add(id);
            }

            if (newlyEligible is { Count: > 0 })
            {
                result = result.WithWorkspace(
                    WorkspaceKey.Default,
                    defaultWorkspace with { Windows = defaultWorkspace.Windows.AddRange(newlyEligible) });
            }
        }

        return result;
    }

    /// <summary>
    /// Removes reassert-budget bookkeeping for any window no longer present in
    /// <paramref name="observed"/> at all, so a long-lived daemon's per-window dictionary does not
    /// grow unbounded across the churn of windows opening and closing over its lifetime.
    /// </summary>
    private void PruneReassertBudgets(ImmutableArray<ObservedWindow> observed)
    {
        if (_reassertBudgets.Count == 0)
        {
            return;
        }

        var stillObserved = new HashSet<WindowId>(observed.Length);
        foreach (ObservedWindow window in observed)
        {
            stillObserved.Add(window.WindowId);
        }

        List<WindowId>? toRemove = null;
        foreach (WindowId trackedId in _reassertBudgets.Keys)
        {
            if (!stillObserved.Contains(trackedId))
            {
                (toRemove ??= []).Add(trackedId);
            }
        }

        if (toRemove is null)
        {
            return;
        }

        foreach (WindowId id in toRemove)
        {
            _reassertBudgets.Remove(id);
        }
    }

    /// <summary>
    /// Attempts to consume one unit of <paramref name="windowId"/>'s reassert budget (DESIGN.md
    /// §3.4: "default 2 per 2-second window"), resetting the rolling window first if it has
    /// elapsed since <see cref="_timeProvider"/> last saw it start. Returns <see langword="false"/>
    /// once the budget is exhausted for the current window, signaling the caller to float the
    /// window instead of planning another move.
    /// </summary>
    private bool TryConsumeReassertBudget(WindowId windowId)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (!_reassertBudgets.TryGetValue(windowId, out ReassertBudgetState? state))
        {
            state = new ReassertBudgetState(now);
            _reassertBudgets[windowId] = state;
        }
        else if (now - state.WindowStartUtc >= _options.ReassertBudgetWindow)
        {
            state.Reset(now);
        }

        if (state.UsedInWindow >= _options.ReassertBudgetPerWindow)
        {
            return false;
        }

        state.UsedInWindow++;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="desired"/> and <paramref name="observed"/> are close enough (per
    /// <see cref="ReconcilerOptions.PositionToleranceDevicePixels"/>) to treat as already
    /// converged. Needed because <see cref="ILayoutEngine.Solve"/> solves in exact, fractional
    /// coordinates while observed frame bounds are always whole device pixels — see
    /// <see cref="ReconcilerOptions.PositionToleranceDevicePixels"/>'s own remarks.
    /// </summary>
    private bool RectsMatch(Rect desired, Rect observed)
    {
        double tolerance = _options.PositionToleranceDevicePixels;
        return Math.Abs(desired.Left - observed.Left) <= tolerance
            && Math.Abs(desired.Top - observed.Top) <= tolerance
            && Math.Abs(desired.Right - observed.Right) <= tolerance
            && Math.Abs(desired.Bottom - observed.Bottom) <= tolerance;
    }

    /// <summary>One managed window's rolling reassert-budget bookkeeping.</summary>
    private sealed class ReassertBudgetState(DateTimeOffset windowStartUtc)
    {
        public int UsedInWindow { get; set; }

        public DateTimeOffset WindowStartUtc { get; private set; } = windowStartUtc;

        public void Reset(DateTimeOffset now)
        {
            UsedInWindow = 0;
            WindowStartUtc = now;
        }
    }
}
