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
/// <b>Single-threaded-actor invariant, enforced, not assumed, via two complementary locks.</b>
/// <see cref="ConvergeOnceAsync"/> serializes whole passes on a <see cref="SemaphoreSlim"/> (mutual
/// exclusion across an <see langword="await"/> boundary, never a <see cref="System.Threading.Lock"/>,
/// which cannot be held across one) so a caller invoking it directly (e.g. a test, or a future
/// distrust-escalation path) while <see cref="RunAsync"/> is also running can never let two passes'
/// <see cref="IWindowSystem.ReadAllAsync"/> calls or state mutations interleave. Separately, every
/// actual read-modify-write of <see cref="DesiredState"/> — <see cref="ConvergeOnceAsync"/>'s own
/// post-await synchronous body <em>and</em> <see cref="SetWorkspace"/>, which never awaits at all
/// and so is never covered by the semaphore — additionally serializes on a
/// <see cref="System.Threading.Lock"/>, closing a race a Codex review finding on this PR identified
/// between a concurrent <see cref="SetWorkspace"/> call (e.g. a future monitor-topology-service
/// reacting to a display change, GitHub issue #16) and an in-flight convergence pass. DESIGN.md §3
/// states the overall invariant architecturally ("All state mutation flows through the
/// single-threaded Reconciler actor"); these two locks together make it structurally true for
/// every mutating entry point, not just <see cref="ConvergeOnceAsync"/> in isolation.
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
/// <para>
/// <b>Placement-plan hand-off (GitHub issue #10's composition-root wiring).</b>
/// <see cref="PlacementPlanReader"/> publishes every non-empty plan <see cref="ConvergeOnceAsync"/>
/// produces — whichever of the three convergence triggers caused it — so a Win32-side consumer
/// (<c>Bastion.Win32</c>'s placement-execution pump) can drain it and hand each plan to the
/// Placement Executor, exactly the same "expose a <see cref="ChannelReader{T}"/>, let an adapter-ring
/// pump drain it" shape <c>WinEventPumpService.IngestReader</c>/<c>Coalescer.IntentReader</c> already
/// establish for the two upstream hops of this same pipeline. This keeps <see cref="RunAsync"/> the
/// single production entry point for convergence timing (heartbeat + wake, already covered by this
/// type's own tests) while still letting <see cref="ConvergeOnceAsync"/>'s result reach the executor
/// without inventing a second, duplicate heartbeat/wake loop outside this class. Only <em>non-empty</em>
/// plans are published — an empty plan means nothing to execute, matching how the upstream channels
/// only ever carry meaningful payloads. A plan not yet drained when a fresher one arrives is
/// superseded, never queued behind it (<see cref="BoundedChannelFullMode.DropOldest"/>): the newest
/// convergence result always supersedes an older, not-yet-applied one, the same "reads are truth"
/// reasoning DESIGN.md §1 already applies everywhere else in this pipeline.
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

    private static readonly BoundedChannelOptions s_placementPlanChannelOptions = new(capacity: 1)
    {
        // The inverse choice from s_wakeChannelOptions, deliberately: here the *newest* plan must
        // win over a stale one still sitting unread, not the other way around -- see this type's
        // own remarks ("Placement-plan hand-off") for why.
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true, // only ConvergeOnceAsync ever writes, serialized by _convergenceGate
        AllowSynchronousContinuations = false,
    };

    private readonly IWindowSystem _windowSystem;
    private readonly ILayoutEngine _layoutEngine;
    private readonly TimeProvider _timeProvider;
    private readonly ReconcilerOptions _options;
    private readonly Channel<byte> _wakeChannel = Channel.CreateBounded<byte>(s_wakeChannelOptions);
    private readonly Channel<ImmutableArray<PlacementInstruction>> _placementPlanChannel =
        Channel.CreateBounded<ImmutableArray<PlacementInstruction>>(s_placementPlanChannelOptions);
    private readonly SemaphoreSlim _convergenceGate = new(1, 1);

    // Guards every read-modify-write of DesiredState (and the reassert-budget dictionary, which is
    // only ever touched from the same critical sections) -- SetWorkspace's own mutation and
    // ConvergeOnceAsync's post-await synchronous body both take this lock, closing the race a
    // Codex review finding on this PR identified: without it, a caller invoking SetWorkspace (e.g.
    // a future monitor-topology-service reacting to a display change, GitHub issue #16) from a
    // different thread while a convergence pass is concurrently past its own await could lose
    // either side's update. Never held across an await (System.Threading.Lock cannot be) --
    // _convergenceGate alone still serializes whole passes, including the awaited
    // IWindowSystem.ReadAllAsync call this lock never wraps.
    private readonly Lock _stateLock = new();

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
    /// The consuming side of the placement-plan hand-off channel (GitHub issue #10). Every
    /// <em>non-empty</em> plan <see cref="ConvergeOnceAsync"/> produces — regardless of which of the
    /// three convergence triggers caused it — is published here for a Win32-side placement-execution
    /// pump to drain and apply. Same "expose a <see cref="ChannelReader{T}"/>" shape as
    /// <c>WinEventPumpService.IngestReader</c>/<c>Coalescer.IntentReader</c>; see this type's own
    /// remarks ("Placement-plan hand-off") for why this is a channel rather than a change to
    /// <see cref="RunAsync"/>'s own signature.
    /// </summary>
    public ChannelReader<ImmutableArray<PlacementInstruction>> PlacementPlanReader => _placementPlanChannel.Reader;

    /// <summary>
    /// Registers or replaces the workspace at <paramref name="key"/>, seeding its work
    /// area/constraints/gaps. v0.1 has no monitor topology service (GitHub issue #16) to discover
    /// this automatically, so callers (the eventual composition root, GitHub issue #10; tests here)
    /// supply it directly. An existing workspace's window list is preserved across calls that only
    /// change geometry. Safe to call from any thread, including concurrently with a running
    /// <see cref="ConvergeOnceAsync"/>/<see cref="RunAsync"/> pass — see <see cref="_stateLock"/>'s
    /// own remarks.
    /// </summary>
    public void SetWorkspace(WorkspaceKey key, Rect workArea, LayoutConstraints constraints = default, LayoutGaps gaps = default)
    {
        lock (_stateLock)
        {
            ImmutableArray<WindowId> existingWindows = DesiredState.Workspaces.TryGetValue(key, out DesiredWorkspace? existing)
                ? existing.Windows
                : ImmutableArray<WindowId>.Empty;

            DesiredState = DesiredState.WithWorkspace(
                key,
                new DesiredWorkspace { Windows = existingWindows, WorkArea = workArea, Constraints = constraints, Gaps = gaps });
        }
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

            // Everything from here on is synchronous (ILayoutEngine.Solve is a pure, non-awaiting
            // call per its own contract) -- held entirely under _stateLock, never across an await,
            // so a concurrent SetWorkspace call can never interleave with this pass's own
            // read-modify-write of DesiredState (see _stateLock's own remarks).
            ImmutableArray<PlacementInstruction> plan;
            lock (_stateLock)
            {
                ObservedState = observed;
                DesiredState = SyncDesiredWindowSet(DesiredState, observed);
                PruneReassertBudgets(observed);

                Dictionary<WindowId, ObservedWindow> observedById = new(observed.Length);
                foreach (ObservedWindow window in observed)
                {
                    observedById[window.WindowId] = window;
                }

                ImmutableArray<PlacementInstruction>.Builder builder = ImmutableArray.CreateBuilder<PlacementInstruction>();
                foreach (DesiredWorkspace workspace in DesiredState.Workspaces.Values)
                {
                    SolveAndDiffWorkspaceLocked(workspace, observedById, builder);
                }

                plan = builder.ToImmutable();
                LastPlacementPlan = plan;
            }

            if (!plan.IsEmpty)
            {
                // See this type's remarks ("Placement-plan hand-off"): only non-empty plans are
                // published -- an empty plan means nothing for the executor to do. TryWrite never
                // blocks/waits (s_placementPlanChannelOptions.FullMode is DropOldest, not Wait), so
                // this can never stall a convergence pass even if nothing has drained the previous
                // plan yet.
                _ = _placementPlanChannel.Writer.TryWrite(plan);
            }

            return plan;
        }
        finally
        {
            _convergenceGate.Release();
        }
    }

    /// <summary>
    /// Solves <paramref name="workspace"/> and appends a <see cref="PlacementInstruction"/> for
    /// every placement that doesn't already match its observed rect (per <see cref="RectsMatch"/>),
    /// gated by the reassert budget. Caller must hold <see cref="_stateLock"/> — this both reads
    /// and writes <see cref="DesiredState"/> (via <see cref="DesiredState.WithWindowUntiled"/>).
    /// </summary>
    private void SolveAndDiffWorkspaceLocked(
        DesiredWorkspace workspace,
        Dictionary<WindowId, ObservedWindow> observedById,
        ImmutableArray<PlacementInstruction>.Builder plan)
    {
        if (workspace.Windows.IsEmpty)
        {
            return;
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

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wakeChannel.Writer.TryComplete();
        _placementPlanChannel.Writer.TryComplete();
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
    /// §3.4: "default 2 per 2-second window") against a true trailing window — never more than
    /// <see cref="ReconcilerOptions.ReassertBudgetPerWindow"/> attempts are ever considered "recent"
    /// within any <see cref="ReconcilerOptions.ReassertBudgetWindow"/>-wide span ending "now,"
    /// regardless of when the oldest currently-tracked attempt happened to start. A fixed window
    /// anchored at the first attempt (reset entirely once stale, rather than aging out one entry at
    /// a time) can let roughly double the configured budget through right at its own reset boundary
    /// (Codex review finding on this PR) — this trailing-window form cannot, by construction.
    /// Returns <see langword="false"/> once the budget is exhausted, signaling the caller to untile
    /// the window instead of planning another move.
    /// </summary>
    private bool TryConsumeReassertBudget(WindowId windowId)
    {
        if (!_reassertBudgets.TryGetValue(windowId, out ReassertBudgetState? state))
        {
            state = new ReassertBudgetState();
            _reassertBudgets[windowId] = state;
        }

        return state.TryConsume(_timeProvider.GetUtcNow(), _options.ReassertBudgetPerWindow, _options.ReassertBudgetWindow);
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

    /// <summary>
    /// One managed window's reassert-budget bookkeeping: a true trailing window over recent
    /// attempt timestamps, bounded to at most <see cref="ReconcilerOptions.ReassertBudgetPerWindow"/>
    /// entries at any moment (a tiny, typically-2-element queue — no unbounded growth risk).
    /// </summary>
    private sealed class ReassertBudgetState
    {
        private readonly Queue<DateTimeOffset> _recentAttempts = new();

        /// <summary>
        /// Prunes attempts older than <paramref name="window"/> relative to <paramref name="now"/>,
        /// then attempts to record one more. Returns <see langword="false"/> without recording one
        /// if <paramref name="maxAttempts"/> are already within the trailing window.
        /// </summary>
        public bool TryConsume(DateTimeOffset now, int maxAttempts, TimeSpan window)
        {
            while (_recentAttempts.Count > 0 && now - _recentAttempts.Peek() >= window)
            {
                _recentAttempts.Dequeue();
            }

            if (_recentAttempts.Count >= maxAttempts)
            {
                return false;
            }

            _recentAttempts.Enqueue(now);
            return true;
        }
    }
}
