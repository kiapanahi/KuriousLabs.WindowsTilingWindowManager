using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime;
using System.Runtime.InteropServices;
using Bastion.Core;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Bastion.Win32;

/// <summary>
/// The Placement Executor (DESIGN.md §3.6): turns the Reconciler's <see cref="PlacementInstruction"/>
/// plan into actual Win32 calls and verifies the results. Per window: (a) hang probe, (b) state
/// normalization, (c) invisible-border correction, (d) batch apply with per-window fallback, (e)
/// verify-after-move.
/// </summary>
/// <remarks>
/// <para>
/// <b>Input/output contract.</b> <see cref="Reconciler"/> already exposes its plan two ways —
/// <see cref="Reconciler.ConvergeOnceAsync"/>'s own return value and the
/// <see cref="Reconciler.LastPlacementPlan"/> property — so no change to its public surface was
/// needed to make this executor a real consumer; a future composition root (GitHub issue #10) wires
/// <c>await reconciler.ConvergeOnceAsync(ct)</c>'s result directly into <see cref="Apply"/>.
/// </para>
/// <para>
/// <b><see cref="Apply"/> is deliberately synchronous, not <c>Task</c>-returning.</b> Every
/// operation it performs — the hang probe, every geometry read, <c>SetWindowPlacement</c>, the
/// Defer-batch triad, the per-window fallback — is a plain, blocking Win32 syscall; none of them
/// are COM calls needing the dedicated STA thread (interop.md §5 scopes that requirement to shell
/// COM specifically) and none genuinely await anything. Wrapping a method with no real asynchronous
/// work in <c>async</c>/<c>Task.FromResult</c> would misrepresent it as cheap/non-blocking when a
/// worst case (every window in a batch newly hung) blocks the calling thread for up to
/// <c>HangProbeTimeout × batch size</c>. Whether a future caller running this from an async context
/// wants to shield itself from that (e.g. <c>Task.Run</c>) is a threading-model decision for that
/// caller (GitHub issue #10) to make with full context, not one this issue should guess at.
/// </para>
/// <para>
/// <b>Not thread-safe; expects sequential invocation.</b> Matches the single-threaded-actor
/// architecture the whole pipeline assumes upstream (DESIGN.md §3, §3.4's <see cref="Reconciler"/>
/// remarks) — exactly one plan is being applied at a time in production. <see cref="_quarantine"/>'s
/// dictionary is not guarded by a lock; a future caller that ever needs concurrent
/// <see cref="Apply"/> calls must serialize them itself.
/// </para>
/// <para>
/// <b>Hang quarantine has two independent parts.</b> A <em>transient</em> backoff
/// (<c>QuarantineState.IsBackedOff</c>) that grows on each consecutive observed hang and resets the
/// moment the window responds again — this is what "quarantined with backoff instead of stalling
/// the batch" (the acceptance criteria's words) means: a backed-off window is skipped entirely,
/// without even a fresh probe, until its backoff elapses. Separately, a <em>sticky</em>
/// <c>QuarantineState.HasEverBeenHung</c> flag that, once set, is never cleared: DESIGN.md §3.6d's
/// "the standing mode for any window ever seen hung" — such a window never re-enters a Defer batch
/// again for the lifetime of this executor, even after it recovers, always taking the per-window
/// <c>SetWindowPos(SWP_ASYNCWINDOWPOS)</c> path instead. <see cref="Purge"/> drops both, for a
/// future caller (GitHub issue #10) to wire to the same <c>WindowVanished</c>/registry-purge signal
/// <see cref="ReconcilerIntentPump"/> already reacts to — not wired here, since this executor has no
/// visibility into vanish events on its own.
/// </para>
/// <para>
/// <b>Clamp detection and "one budgeted re-layout" (DESIGN.md §3.6e).</b> Every successful move is
/// verified via a fresh post-move frame-bounds read; if the verified width/height differs from what
/// was requested by more than <see cref="PlacementExecutorOptions.SizeToleranceDevicePixels"/>, the
/// outcome's <see cref="PlacementOutcome.ClampedTo"/> is populated — the data GitHub issue #6's
/// learned effective-min-size cache will eventually subscribe to (not built here). If <em>any</em>
/// outcome in a pass is clamped, <see cref="Apply"/> calls
/// <see cref="IReconcileNowSignal.RequestReconcileNow"/> exactly once — never once per clamped
/// window. The "budgeted" part of "one budgeted re-layout" is not re-implemented here at all: it is
/// already the Reconciler's own reassert-budget mechanism (GitHub issue #4,
/// <c>ReconcilerOptions.ReassertBudgetPerWindow</c>), which this signal's target convergence pass
/// runs through regardless of who woke it — a chronically-clamped window is untiled by that
/// existing mechanism, not by anything new here.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and intended to be " +
        "registered once Bastion.Daemon's composition root is wired (GitHub issue #10) — not yet " +
        "wired as of this change. Same documented CA1812 false-positive shape as " +
        "Coalescer/WindowSystemAdapter/WinEventPumpService/ReconcilerIntentPump/BastiondService.")]
internal sealed class PlacementExecutor(
    IPlacementSystem system,
    IReconcileNowSignal reconcileNowSignal,
    TimeProvider timeProvider,
    PlacementExecutorOptions? options = null)
{
    private readonly PlacementExecutorOptions _options = options ?? PlacementExecutorOptions.Default;
    private readonly Dictionary<WindowId, QuarantineState> _quarantine = [];

    /// <summary>
    /// Applies every <see cref="PlacementInstruction"/> in <paramref name="plan"/>, one outcome per
    /// instruction, in order. See this type's remarks for why this is synchronous.
    /// </summary>
    public ImmutableArray<PlacementOutcome> Apply(ImmutableArray<PlacementInstruction> plan, CancellationToken cancellationToken = default)
    {
        if (plan.IsEmpty)
        {
            return ImmutableArray<PlacementOutcome>.Empty;
        }

        var pass = new ApplyPass(system.ReadPrimaryWorkArea());
        ImmutableArray<PlacementOutcome>.Builder outcomes = ImmutableArray.CreateBuilder<PlacementOutcome>(plan.Length);

        foreach (PlacementInstruction instruction in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessInstruction(instruction, pass, outcomes);
        }

        if (pass.Batchable.Count > 0 && !TryApplyBatch(pass.Batchable, outcomes, pass))
        {
            // DESIGN.md §3.6d: any DeferWindowPos failure abandons the whole HDWP -- every window
            // that was going to be in this batch needs the per-window fallback, not just whichever
            // one failed.
            pass.PerWindowFallback.AddRange(pass.Batchable);
        }

        ApplyPerWindowFallbacks(pass, outcomes);

        if (pass.AnyClamped)
        {
            reconcileNowSignal.RequestReconcileNow();
        }

        return outcomes.ToImmutable();
    }

    /// <summary>
    /// Runs the hang-probe/border-correction/state-normalization-or-batch-queue decision for one
    /// instruction, appending a completed outcome directly or queuing a <see cref="BatchCandidate"/>
    /// onto <paramref name="pass"/> for later batch/fallback processing.
    /// </summary>
    private void ProcessInstruction(PlacementInstruction instruction, ApplyPass pass, ImmutableArray<PlacementOutcome>.Builder outcomes)
    {
        if (instruction.Action == PlacementAction.Untile)
        {
            outcomes.Add(PlacementOutcome.Untiled(instruction.WindowId));
            return;
        }

        if (!system.TryResolveHwnd(instruction.WindowId, out HWND hwnd))
        {
            // Vanished between the Reconciler's plan and this pass -- a routine race, not
            // exceptional; the next convergence pass naturally forgets it once EnumWindows stops
            // reporting it (matching WindowSystemAdapter's own established posture).
            outcomes.Add(PlacementOutcome.Failed(instruction.WindowId, errorCode: null));
            return;
        }

        QuarantineState quarantine = GetOrCreateQuarantine(instruction.WindowId);
        DateTimeOffset now = timeProvider.GetUtcNow();

        if (quarantine.IsBackedOff(now))
        {
            outcomes.Add(PlacementOutcome.QuarantinedHung(instruction.WindowId));
            return;
        }

        if (system.ProbeIsHung(hwnd, _options.HangProbeTimeout))
        {
            quarantine.RecordHang(now, _options);
            outcomes.Add(PlacementOutcome.QuarantinedHung(instruction.WindowId));
            return;
        }

        quarantine.RecordResponsive();

        // TargetBounds is only ever null for Untile (handled above) -- PlacementInstruction's own
        // Move factory requires a non-null Rect (Bastion.Core.PlacementInstruction's own remarks),
        // so every instruction reaching here carries a real target.
        Rect requestedTarget = instruction.TargetBounds!.Value;
        Rect correctedTarget = requestedTarget;
        if (system.TryReadGeometry(hwnd, out Rect windowRect, out Rect frameBounds))
        {
            correctedTarget = PlacementCoordinateConverter.ApplyBorderCorrection(requestedTarget, windowRect, frameBounds);
        }

        WindowPlacementState state = system.ReadPlacementState(hwnd);

        if (state.NeedsStateNormalization)
        {
            ApplyStateNormalization(instruction.WindowId, hwnd, requestedTarget, correctedTarget, state, pass, outcomes);
            return;
        }

        var candidate = new BatchCandidate(instruction.WindowId, hwnd, requestedTarget, correctedTarget);
        (quarantine.HasEverBeenHung ? pass.PerWindowFallback : pass.Batchable).Add(candidate);
    }

    /// <summary>DESIGN.md §3.6b: restore directly into the tile, never restore-then-move.</summary>
    private void ApplyStateNormalization(
        WindowId windowId,
        HWND hwnd,
        Rect requestedTarget,
        Rect correctedTarget,
        WindowPlacementState state,
        ApplyPass pass,
        ImmutableArray<PlacementOutcome>.Builder outcomes)
    {
        Rect placementTarget = state.IsToolWindow
            ? correctedTarget
            : PlacementCoordinateConverter.ToWorkspaceCoordinates(correctedTarget, pass.PrimaryWorkArea);

        PlacementCallResult result = system.ApplyWindowPlacement(hwnd, placementTarget);
        PlacementOutcome outcome = FinalizeMoveOutcome(windowId, hwnd, requestedTarget, result);
        pass.AnyClamped |= outcome.ClampedTo is not null;
        outcomes.Add(outcome);
    }

    private void ApplyPerWindowFallbacks(ApplyPass pass, ImmutableArray<PlacementOutcome>.Builder outcomes)
    {
        foreach (BatchCandidate candidate in pass.PerWindowFallback)
        {
            PlacementCallResult result = system.ApplyWindowPosFallback(candidate.Hwnd, candidate.CorrectedTarget);
            PlacementOutcome outcome = FinalizeMoveOutcome(candidate.WindowId, candidate.Hwnd, candidate.OriginalTarget, result);
            pass.AnyClamped |= outcome.ClampedTo is not null;
            outcomes.Add(outcome);
        }
    }

    /// <summary>
    /// Drops <paramref name="windowId"/>'s quarantine bookkeeping (both the transient backoff and
    /// the sticky ever-hung flag). Not yet wired to anything — a future caller (GitHub issue #10)
    /// pairs this with the same <c>WindowVanished</c> signal <see cref="ReconcilerIntentPump"/>
    /// already reacts to, so a long-lived daemon's per-window dictionary here does not grow
    /// unbounded across the churn of windows opening and closing (mirroring
    /// <c>Reconciler.PruneReassertBudgets</c>'s own rationale).
    /// </summary>
    public void Purge(WindowId windowId) => _quarantine.Remove(windowId);

    private QuarantineState GetOrCreateQuarantine(WindowId windowId)
    {
        if (!_quarantine.TryGetValue(windowId, out QuarantineState? state))
        {
            state = new QuarantineState();
            _quarantine[windowId] = state;
        }

        return state;
    }

    /// <summary>
    /// Attempts the whole <paramref name="candidates"/> batch. On success, appends one
    /// <see cref="PlacementOutcome"/> per candidate (verified) to <paramref name="outcomes"/> and
    /// returns <see langword="true"/>. On failure, appends nothing — the caller redoes every
    /// candidate via the per-window fallback instead.
    /// </summary>
    private bool TryApplyBatch(List<BatchCandidate> candidates, ImmutableArray<PlacementOutcome>.Builder outcomes, ApplyPass pass)
    {
        // DESIGN.md §3.6d / docs/engineering/concurrency-performance.md §3: scoped tightly around
        // the Defer batch only, restored immediately after in a finally -- never left set, never
        // applied process-wide.
        GCLatencyMode previous = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        bool succeeded;
        try
        {
            succeeded = TryDeferAndEndBatch(candidates);
        }
        finally
        {
            GCSettings.LatencyMode = previous;
        }

        if (!succeeded)
        {
            return false;
        }

        foreach (BatchCandidate candidate in candidates)
        {
            PlacementOutcome outcome = FinalizeMoveOutcome(candidate.WindowId, candidate.Hwnd, candidate.OriginalTarget, PlacementCallResult.Ok);
            pass.AnyClamped |= outcome.ClampedTo is not null;
            outcomes.Add(outcome);
        }

        return true;
    }

    private bool TryDeferAndEndBatch(List<BatchCandidate> candidates)
    {
        HDWP batch = system.BeginDefer(candidates.Count);
        if (batch.IsNull)
        {
            return false;
        }

        foreach (BatchCandidate candidate in candidates)
        {
            if (system.TryDefer(batch, candidate.Hwnd, candidate.CorrectedTarget) is not { } next)
            {
                // DeferWindowPos's own documented contract: abandon and never call
                // EndDeferWindowPos on this HDWP.
                return false;
            }

            batch = next;
        }

        return system.EndDefer(batch);
    }

    /// <summary>Verify-after-move (DESIGN.md §3.6e) for one window whose apply call already ran.</summary>
    private PlacementOutcome FinalizeMoveOutcome(WindowId windowId, HWND hwnd, Rect requestedTarget, PlacementCallResult applyResult)
    {
        if (!applyResult.Success)
        {
            return PlacementOutcome.Failed(windowId, applyResult.ErrorCode);
        }

        if (!system.TryReadGeometry(hwnd, out _, out Rect verifiedBounds))
        {
            // Vanished between the move and this verify read -- a routine race, not exceptional
            // (matching WindowSystemAdapter's own established handling of the identical race). The
            // move itself still succeeded; there is simply nothing further to verify.
            return PlacementOutcome.Moved(windowId, verifiedBounds: null, clampedTo: null);
        }

        bool widthClamped = Math.Abs(verifiedBounds.Width - requestedTarget.Width) > _options.SizeToleranceDevicePixels;
        bool heightClamped = Math.Abs(verifiedBounds.Height - requestedTarget.Height) > _options.SizeToleranceDevicePixels;
        Rect? clampedTo = widthClamped || heightClamped ? verifiedBounds : null;

        return PlacementOutcome.Moved(windowId, verifiedBounds, clampedTo);
    }

    /// <summary>One window queued for either the Defer batch or the per-window fallback.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct BatchCandidate(WindowId WindowId, HWND Hwnd, Rect OriginalTarget, Rect CorrectedTarget);

    /// <summary>
    /// Mutable, per-<see cref="Apply"/>-call working state: the primary work area (queried once),
    /// the batch/fallback queues <see cref="ProcessInstruction"/> fills in, and the
    /// clamp-detected-anywhere-this-pass flag.
    /// </summary>
    private sealed class ApplyPass(Rect primaryWorkArea)
    {
        public Rect PrimaryWorkArea { get; } = primaryWorkArea;

        public List<BatchCandidate> Batchable { get; } = [];

        public List<BatchCandidate> PerWindowFallback { get; } = [];

        public bool AnyClamped { get; set; }
    }

    /// <summary>
    /// One managed window's hang-quarantine bookkeeping — see this type's own remarks for the
    /// transient-backoff-vs-sticky-ever-hung distinction.
    /// </summary>
    private sealed class QuarantineState
    {
        private TimeSpan _currentBackoff;
        private DateTimeOffset? _backedOffUntil;

        /// <summary>DESIGN.md §3.6d's "any window ever seen hung" — sticky, never reset by <see cref="RecordResponsive"/>.</summary>
        public bool HasEverBeenHung { get; private set; }

        /// <summary>Whether this window is still within its transient backoff window and should be skipped without a fresh probe.</summary>
        public bool IsBackedOff(DateTimeOffset now) => _backedOffUntil is { } until && now < until;

        /// <summary>Records a hang: sets the sticky flag and grows (or starts) the transient backoff, capped at <see cref="PlacementExecutorOptions.MaxQuarantineBackoff"/>.</summary>
        public void RecordHang(DateTimeOffset now, PlacementExecutorOptions options)
        {
            HasEverBeenHung = true;
            TimeSpan next = _currentBackoff <= TimeSpan.Zero
                ? options.InitialQuarantineBackoff
                : TimeSpan.FromTicks((long)(_currentBackoff.Ticks * options.QuarantineBackoffMultiplier));
            _currentBackoff = next > options.MaxQuarantineBackoff ? options.MaxQuarantineBackoff : next;
            _backedOffUntil = now + _currentBackoff;
        }

        /// <summary>Clears the transient backoff (a later hang starts fresh at <see cref="PlacementExecutorOptions.InitialQuarantineBackoff"/>) — never clears <see cref="HasEverBeenHung"/>.</summary>
        public void RecordResponsive()
        {
            _backedOffUntil = null;
            _currentBackoff = TimeSpan.Zero;
        }
    }
}
