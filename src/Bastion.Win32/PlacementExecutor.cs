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
/// <c>await reconciler.ConvergeOnceAsync(ct)</c>'s result directly into <see cref="ApplyAsync"/>.
/// </para>
/// <para>
/// <b>Correction: <see cref="ApplyAsync"/> is genuinely asynchronous, not the synchronous method an
/// earlier revision of this type used.</b> Both <c>WPF_ASYNCWINDOWPLACEMENT</c> (state
/// normalization) and <c>SWP_ASYNCWINDOWPOS</c> (the per-window fallback) are documented to *post*
/// the request to the target window's thread rather than wait for it to be processed, whenever that
/// thread is on a different input queue than the caller — true for essentially every foreign window
/// bastiond will ever place. A successful return from either call therefore only means "posted," not
/// "applied": reading geometry back immediately can still observe the pre-move bounds and misreport
/// a clamp (Codex review finding on this PR). <see cref="ApplyAsync"/> defers verification for every
/// window placed through one of those two async-flagged paths and, once per <em>pass</em> (never per
/// window — the cost must not scale with batch size), awaits one bounded
/// <see cref="PlacementExecutorOptions.AsyncVerifyDelay"/> via <c>Task.Delay(TimeSpan, TimeProvider)</c>
/// before reading any of them back. The synchronous <c>BeginDeferWindowPos</c>/<c>DeferWindowPos</c>/
/// <c>EndDeferWindowPos</c> batch path needs no such wait — <c>EndDeferWindowPos</c> without
/// <c>SWP_ASYNCWINDOWPOS</c> already blocks until every window's <c>WM_WINDOWPOSCHANGED</c> is
/// processed (DESIGN.md §3.6d: "EndDeferWindowPos sends synchronously" — the very reason hung
/// windows must be excluded from the batch rather than relying on this flag inside it) — it verifies
/// immediately, with no delay. A pass whose every instruction resolves via <c>Untile</c>, a vanished
/// <c>WindowId</c>, quarantine, or the synchronous batch pays no delay at all.
/// </para>
/// <para>
/// <b><see cref="ApplyAsync"/> itself is not thread-safe; expects sequential invocation.</b> Matches
/// the single-threaded-actor architecture the whole pipeline assumes upstream (DESIGN.md §3, §3.4's
/// <see cref="Reconciler"/> remarks) — exactly one plan is being applied at a time in production
/// (<c>Bastion.Win32.PlacementExecutionPump</c>'s own single sequential drain loop). <see cref="_quarantine"/>'s
/// <em>dictionary</em> is a narrower exception, guarded by <see cref="_quarantineLock"/> (GitHub
/// issue #10, Codex review finding on this PR): <see cref="Purge"/> is a genuine second concurrent
/// entry point — <c>Bastion.Win32.ReconcilerIntentPump</c>'s own independent drain loop calls it on
/// <c>WindowVanished</c>, which can run concurrently with an in-flight <see cref="ApplyAsync"/> pass
/// on a different thread. The lock covers only the dictionary's own structural mutations
/// (<see cref="GetOrCreateQuarantine"/>'s lookup-or-insert, <see cref="Purge"/>'s remove) — never a
/// <see cref="QuarantineState"/> instance's own field mutations
/// (<see cref="QuarantineState.RecordHang"/>/<see cref="QuarantineState.RecordResponsive"/>), which
/// stay reachable only from <see cref="ApplyAsync"/>'s own still-single-caller-at-a-time path.
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
/// outcome in a pass is clamped, <see cref="ApplyAsync"/> calls
/// <see cref="IReconcileNowSignal.RequestReconcileNow"/> exactly once — never once per clamped
/// window. The "budgeted" part of "one budgeted re-layout" is not re-implemented here at all: it is
/// already the Reconciler's own reassert-budget mechanism (GitHub issue #4,
/// <c>ReconcilerOptions.ReassertBudgetPerWindow</c>), which this signal's target convergence pass
/// runs through regardless of who woke it — a chronically-clamped window is untiled by that
/// existing mechanism, not by anything new here.
/// </para>
/// <para>
/// <b>Outcome order matches plan order.</b> Every instruction's outcome is written into its own
/// reserved slot (keyed by its original index in <paramref name="plan"/>, threaded through
/// <see cref="BatchCandidate"/>/<see cref="PendingVerification"/>) rather than appended in whatever
/// order each execution phase (synchronous rejections, the Defer batch, deferred async
/// verifications) happens to finish in — a plan mixing <c>Untile</c> and <c>Move</c> instructions
/// would otherwise come back reordered (Codex review finding on this PR).
/// </para>
/// <para>
/// <b>Options are validated at construction, matching <c>Reconciler</c>'s own pattern.</b> A
/// non-positive <see cref="PlacementExecutorOptions.HangProbeTimeout"/> would otherwise flow into
/// <c>SendMessageTimeout</c> as <c>(uint)timeout.TotalMilliseconds</c>, wrapping a negative value
/// into a huge one and stalling every call this executor makes (Copilot review finding on this PR);
/// every other timing/threshold value is checked for the same reason.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and intended to be " +
        "registered once Bastion.Daemon's composition root is wired (GitHub issue #10) — not yet " +
        "wired as of this change. Same documented CA1812 false-positive shape as " +
        "Coalescer/WindowSystemAdapter/WinEventPumpService/ReconcilerIntentPump/BastiondService.")]
internal sealed class PlacementExecutor
{
    private readonly IPlacementSystem _system;
    private readonly IReconcileNowSignal _reconcileNowSignal;
    private readonly TimeProvider _timeProvider;
    private readonly PlacementExecutorOptions _options;
    private readonly Dictionary<WindowId, QuarantineState> _quarantine = [];

    // Guards only _quarantine's own structural mutations (lookup-or-insert, remove) -- see this
    // type's own remarks for why Purge becoming a genuine second concurrent caller (GitHub issue
    // #10) needs this despite ApplyAsync's own still-single-caller-at-a-time contract. Never held
    // across an await -- every access below is a synchronous dictionary operation.
    private readonly Lock _quarantineLock = new();

    public PlacementExecutor(
        IPlacementSystem system,
        IReconcileNowSignal reconcileNowSignal,
        TimeProvider timeProvider,
        PlacementExecutorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(reconcileNowSignal);
        ArgumentNullException.ThrowIfNull(timeProvider);

        PlacementExecutorOptions resolvedOptions = options ?? PlacementExecutorOptions.Default;

        // Manual checks, not the ArgumentOutOfRangeException.ThrowIfX(value, ...) helpers: those
        // derive paramName from the value expression via CallerArgumentExpression, and every value
        // here is a member access on the local `resolvedOptions` rather than a parameter of this
        // constructor -- the same MA0015 mismatch Reconciler's own constructor already documents.
        // nameof(options) is the actual source of every value below, even after defaulting.
        if (resolvedOptions.HangProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.HangProbeTimeout, "PlacementExecutorOptions.HangProbeTimeout must be positive.");
        }

        if (resolvedOptions.InitialQuarantineBackoff <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.InitialQuarantineBackoff, "PlacementExecutorOptions.InitialQuarantineBackoff must be positive.");
        }

        if (resolvedOptions.MaxQuarantineBackoff < resolvedOptions.InitialQuarantineBackoff)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.MaxQuarantineBackoff, "PlacementExecutorOptions.MaxQuarantineBackoff must be at least InitialQuarantineBackoff.");
        }

        if (resolvedOptions.QuarantineBackoffMultiplier < 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.QuarantineBackoffMultiplier, "PlacementExecutorOptions.QuarantineBackoffMultiplier must be at least 1.0.");
        }

        if (resolvedOptions.SizeToleranceDevicePixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.SizeToleranceDevicePixels, "PlacementExecutorOptions.SizeToleranceDevicePixels must be non-negative.");
        }

        if (resolvedOptions.AsyncVerifyDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolvedOptions.AsyncVerifyDelay, "PlacementExecutorOptions.AsyncVerifyDelay must be non-negative.");
        }

        _system = system;
        _reconcileNowSignal = reconcileNowSignal;
        _timeProvider = timeProvider;
        _options = resolvedOptions;
    }

    /// <summary>
    /// Applies every <see cref="PlacementInstruction"/> in <paramref name="plan"/>, one outcome per
    /// instruction, in the same order as <paramref name="plan"/>. See this type's remarks for why
    /// this awaits a bounded, pass-wide settle delay for async-flagged placements.
    /// </summary>
    public async Task<ImmutableArray<PlacementOutcome>> ApplyAsync(ImmutableArray<PlacementInstruction> plan, CancellationToken cancellationToken = default)
    {
        if (plan.IsEmpty)
        {
            return ImmutableArray<PlacementOutcome>.Empty;
        }

        var pass = new ApplyPass(_system.ReadPrimaryWorkArea(), plan.Length);

        for (int index = 0; index < plan.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessInstruction(plan[index], index, pass);
        }

        if (pass.Batchable.Count > 0 && !TryApplyBatch(pass.Batchable, pass))
        {
            // DESIGN.md §3.6d: any DeferWindowPos failure abandons the whole HDWP -- every window
            // that was going to be in this batch needs the per-window fallback, not just whichever
            // one failed.
            pass.PerWindowFallback.AddRange(pass.Batchable);
        }

        IssuePerWindowFallbacks(pass);

        if (pass.PendingVerifications.Count > 0)
        {
            // See this type's remarks: one bounded, pass-wide wait for every window placed through
            // an async-flagged path this pass, never a per-window wait.
            await Task.Delay(_options.AsyncVerifyDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        FinalizeVerifications(pass);

        if (pass.AnyClamped)
        {
            _reconcileNowSignal.RequestReconcileNow();
        }

        return ImmutableArray.Create(pass.Outcomes);
    }

    /// <summary>
    /// Runs the hang-probe/border-correction/state-normalization-or-batch-queue decision for one
    /// instruction, writing a completed outcome directly into <c>pass.Outcomes[index]</c> or queuing
    /// a <see cref="BatchCandidate"/> onto <paramref name="pass"/> for later batch/fallback
    /// processing.
    /// </summary>
    private void ProcessInstruction(PlacementInstruction instruction, int index, ApplyPass pass)
    {
        if (instruction.Action == PlacementAction.Untile)
        {
            pass.Outcomes[index] = PlacementOutcome.Untiled(instruction.WindowId);
            return;
        }

        if (!_system.TryResolveHwnd(instruction.WindowId, out HWND hwnd))
        {
            // Vanished between the Reconciler's plan and this pass -- a routine race, not
            // exceptional; the next convergence pass naturally forgets it once EnumWindows stops
            // reporting it (matching WindowSystemAdapter's own established posture).
            pass.Outcomes[index] = PlacementOutcome.Failed(instruction.WindowId, errorCode: null);
            return;
        }

        QuarantineState quarantine = GetOrCreateQuarantine(instruction.WindowId);
        if (TryQuarantine(instruction.WindowId, hwnd, quarantine, index, pass))
        {
            return;
        }

        // TargetBounds is only ever null for Untile (handled above) -- PlacementInstruction's own
        // Move factory requires a non-null Rect (Bastion.Core.PlacementInstruction's own remarks),
        // so every instruction reaching here carries a real target.
        Rect requestedTarget = instruction.TargetBounds!.Value;
        Rect correctedTarget = requestedTarget;
        if (_system.TryReadGeometry(hwnd, out Rect windowRect, out Rect frameBounds))
        {
            correctedTarget = PlacementCoordinateConverter.ApplyBorderCorrection(requestedTarget, windowRect, frameBounds);
        }

        WindowPlacementState state = _system.ReadPlacementState(hwnd);

        if (state.NeedsStateNormalization)
        {
            ApplyStateNormalization(index, instruction.WindowId, hwnd, requestedTarget, correctedTarget, state, pass);
            return;
        }

        var candidate = new BatchCandidate(index, instruction.WindowId, hwnd, requestedTarget, correctedTarget);
        if (quarantine.HasEverBeenHung)
        {
            pass.PerWindowFallback.Add(candidate);
        }
        else
        {
            pass.Batchable.Add(candidate);
        }
    }

    /// <summary>
    /// Runs the hang-probe/backoff decision for one window (DESIGN.md §3.6a, §9). Returns
    /// <see langword="true"/> (and writes a <see cref="PlacementOutcomeKind.QuarantinedHung"/>
    /// outcome into <c>pass.Outcomes[index]</c>) if the window is currently within its transient
    /// backoff or just failed a fresh probe; <see langword="false"/> if it is clear to proceed.
    /// </summary>
    private bool TryQuarantine(WindowId windowId, HWND hwnd, QuarantineState quarantine, int index, ApplyPass pass)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (quarantine.IsBackedOff(now))
        {
            pass.Outcomes[index] = PlacementOutcome.QuarantinedHung(windowId);
            return true;
        }

        if (_system.ProbeIsHung(hwnd, _options.HangProbeTimeout))
        {
            quarantine.RecordHang(now, _options);
            pass.Outcomes[index] = PlacementOutcome.QuarantinedHung(windowId);
            return true;
        }

        quarantine.RecordResponsive();
        return false;
    }

    /// <summary>DESIGN.md §3.6b: restore directly into the tile, never restore-then-move.</summary>
    private void ApplyStateNormalization(
        int index, WindowId windowId, HWND hwnd, Rect requestedTarget, Rect correctedTarget, WindowPlacementState state, ApplyPass pass)
    {
        Rect placementTarget = state.IsToolWindow
            ? correctedTarget
            : PlacementCoordinateConverter.ToWorkspaceCoordinates(correctedTarget, pass.PrimaryWorkArea);

        PlacementCallResult result = _system.ApplyWindowPlacement(hwnd, placementTarget);
        if (!result.Success)
        {
            pass.Outcomes[index] = PlacementOutcome.Failed(windowId, result.ErrorCode);
            return;
        }

        // Succeeded, but this call always sets WPF_ASYNCWINDOWPLACEMENT -- defer verification until
        // after ApplyAsync's pass-wide settle wait (see this type's remarks).
        pass.PendingVerifications.Add(new PendingVerification(index, windowId, hwnd, requestedTarget));
    }

    private void IssuePerWindowFallbacks(ApplyPass pass)
    {
        foreach (BatchCandidate candidate in pass.PerWindowFallback)
        {
            PlacementCallResult result = _system.ApplyWindowPosFallback(candidate.Hwnd, candidate.CorrectedTarget);
            if (!result.Success)
            {
                pass.Outcomes[candidate.Index] = PlacementOutcome.Failed(candidate.WindowId, result.ErrorCode);
                continue;
            }

            // Succeeded, but this call always sets SWP_ASYNCWINDOWPOS -- defer verification (see
            // this type's remarks), exactly like the state-normalization path.
            pass.PendingVerifications.Add(new PendingVerification(candidate.Index, candidate.WindowId, candidate.Hwnd, candidate.OriginalTarget));
        }
    }

    /// <summary>
    /// Drops <paramref name="windowId"/>'s quarantine bookkeeping (both the transient backoff and
    /// the sticky ever-hung flag). Wired by GitHub issue #10's <see cref="ReconcilerIntentPump"/> to
    /// the same <c>WindowVanished</c> signal that already purges <c>WindowRegistry</c>, so a
    /// long-lived daemon's per-window dictionary here does not grow unbounded across the churn of
    /// windows opening and closing (mirroring <c>Reconciler.PruneReassertBudgets</c>'s own
    /// rationale). Safe to call concurrently with an in-flight <see cref="ApplyAsync"/> pass on a
    /// different thread — see <see cref="_quarantineLock"/>'s own remarks.
    /// </summary>
    public void Purge(WindowId windowId)
    {
        lock (_quarantineLock)
        {
            _quarantine.Remove(windowId);
        }
    }

    private QuarantineState GetOrCreateQuarantine(WindowId windowId)
    {
        lock (_quarantineLock)
        {
            if (!_quarantine.TryGetValue(windowId, out QuarantineState? state))
            {
                state = new QuarantineState();
                _quarantine[windowId] = state;
            }

            return state;
        }
    }

    /// <summary>
    /// Attempts the whole <paramref name="candidates"/> batch. On success, writes one verified
    /// <see cref="PlacementOutcome"/> per candidate into its own <c>pass.Outcomes</c> slot and
    /// returns <see langword="true"/>. On failure, writes nothing — the caller redoes every
    /// candidate via the per-window fallback instead.
    /// </summary>
    private bool TryApplyBatch(List<BatchCandidate> candidates, ApplyPass pass)
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
            // EndDeferWindowPos here carries no SWP_ASYNCWINDOWPOS and sends synchronously
            // (DESIGN.md §3.6d) -- unlike the async-flagged paths, no settle wait is needed before
            // verifying.
            PlacementOutcome outcome = FinalizeVerifiedMove(candidate.WindowId, candidate.Hwnd, candidate.OriginalTarget);
            pass.AnyClamped |= outcome.ClampedTo is not null;
            pass.Outcomes[candidate.Index] = outcome;
        }

        return true;
    }

    private bool TryDeferAndEndBatch(List<BatchCandidate> candidates)
    {
        HDWP batch = _system.BeginDefer(candidates.Count);
        if (batch.IsNull)
        {
            return false;
        }

        foreach (BatchCandidate candidate in candidates)
        {
            if (_system.TryDefer(batch, candidate.Hwnd, candidate.CorrectedTarget) is not { } next)
            {
                // DeferWindowPos's own documented contract: abandon and never call
                // EndDeferWindowPos on this HDWP.
                return false;
            }

            batch = next;
        }

        return _system.EndDefer(batch);
    }

    /// <summary>Verify-after-move (DESIGN.md §3.6e) for every window deferred behind the pass-wide async settle wait.</summary>
    private void FinalizeVerifications(ApplyPass pass)
    {
        foreach (PendingVerification pending in pass.PendingVerifications)
        {
            PlacementOutcome outcome = FinalizeVerifiedMove(pending.WindowId, pending.Hwnd, pending.RequestedTarget);
            pass.AnyClamped |= outcome.ClampedTo is not null;
            pass.Outcomes[pending.Index] = outcome;
        }
    }

    /// <summary>
    /// Reads back and clamp-checks one window whose apply call is already known to have succeeded.
    /// </summary>
    private PlacementOutcome FinalizeVerifiedMove(WindowId windowId, HWND hwnd, Rect requestedTarget)
    {
        if (!_system.TryReadGeometry(hwnd, out _, out Rect verifiedBounds))
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

    /// <summary>One window queued for either the Defer batch or the per-window fallback, keyed by its original plan index.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct BatchCandidate(int Index, WindowId WindowId, HWND Hwnd, Rect OriginalTarget, Rect CorrectedTarget);

    /// <summary>One window whose async-flagged apply call succeeded and is awaiting the pass-wide settle wait before verification, keyed by its original plan index.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PendingVerification(int Index, WindowId WindowId, HWND Hwnd, Rect RequestedTarget);

    /// <summary>
    /// Mutable, per-<see cref="ApplyAsync"/>-call working state: the primary work area (queried
    /// once), each instruction's reserved outcome slot (preserving plan order regardless of which
    /// phase resolves it), the batch/fallback/pending-verification queues
    /// <see cref="ProcessInstruction"/> fills in, and the clamp-detected-anywhere-this-pass flag.
    /// </summary>
    private sealed class ApplyPass(Rect primaryWorkArea, int instructionCount)
    {
        public Rect PrimaryWorkArea { get; } = primaryWorkArea;

        public PlacementOutcome[] Outcomes { get; } = new PlacementOutcome[instructionCount];

        public List<BatchCandidate> Batchable { get; } = [];

        public List<BatchCandidate> PerWindowFallback { get; } = [];

        public List<PendingVerification> PendingVerifications { get; } = [];

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
