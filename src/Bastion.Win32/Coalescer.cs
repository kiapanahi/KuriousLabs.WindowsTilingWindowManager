using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Windows.Win32;

namespace Bastion.Win32;

/// <summary>
/// The Coalescer (DESIGN.md §3.2): drains the WinEvent ingest channel on a single dedicated
/// reader and turns storms of raw <see cref="WinEvent"/>s into a typed, debounced stream of
/// <see cref="CoalescedIntent"/>s.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hosting shape.</b> A <see cref="BackgroundService"/>, not a raw <see cref="IHostedService"/>
/// + dedicated <see cref="Thread"/> — that pattern (docs/engineering/daemon-architecture.md §2) is
/// reserved for message-pump/hook-owning threads (the WinEvent pump, GitHub issue #1), which this
/// is not: <see cref="ExecuteAsync"/> is an ordinary <see langword="await foreach"/> channel-drain
/// loop with no <c>GetMessage</c> loop or hook registration of its own. It also makes one plain
/// synchronous <c>DwmGetWindowAttribute</c> call per CLOAKED/UNCLOAKED event (via
/// <see cref="ICloakStateReader"/>) — a documented, ordinary Win32 call needing no STA/message-pump
/// semantics, so it does not push this into dedicated-thread territory either
/// (docs/engineering/concurrency-performance.md §2: <c>TaskCreationOptions.LongRunning</c>/a plain
/// async drain loop is acceptable specifically because this loop "performs no Win32 calls that
/// require message-pump semantics" and "no COM calls requiring apartment affinity" — true here).
/// </para>
/// <para>
/// <b>Per-HWND, per-intent-kind coalescing.</b> Two independent clocks are deliberately in play,
/// per docs/engineering/concurrency-performance.md §4: whether a new raw event <em>merges</em> into
/// an already-pending batch is decided from <see cref="WinEvent.DwmsEventTimeMs"/> — the OS's own
/// event timestamp — via <see cref="IsWithinCoalesceWindow"/>, so that two OS-distant events which
/// merely happen to be drained back-to-back (e.g. this loop catching up on a backlog) are never
/// wrongly folded into one intent. Separately, <em>when</em> a pending batch actually flushes is
/// governed by a <see cref="TimeProvider"/>-backed one-shot <see cref="ITimer"/>
/// (<see cref="TimeProvider.CreateTimer"/>) per (Hwnd, <see cref="IntentKind"/>) pair, reset on
/// every merge — a live storm keeps pushing the flush out until it actually goes quiet, bounded to
/// <see cref="_coalesceWindow"/> of quiet time. <see cref="WindowVanished"/> (from
/// <c>EVENT_OBJECT_DESTROY</c>) and the <c>MOVESIZEEND</c>-driven <see cref="DragEnded"/>/
/// <see cref="GeometryDrift"/> pair are the only three intents emitted immediately, never
/// debounced — see their own remarks and <see cref="HandleDragEnd"/> for why.
/// </para>
/// <para>
/// <b>Thread safety.</b> Flush timers fire on thread-pool threads (documented:
/// <see cref="TimeProvider.CreateTimer"/>'s callback "may be invoked simultaneously on two threads
/// if the timer fires again before or while a previous callback is still being handled"), while
/// <see cref="OnEvent"/> runs on this service's own drain-loop thread — both touch
/// <see cref="_hwndStates"/>, so every access is guarded by <see cref="_gate"/>
/// (<see cref="System.Threading.Lock"/>, never held across an <see langword="await"/>).
/// <see cref="FlushLocked"/> additionally guards against a documented race: a timer's callback can
/// still fire after <see cref="ITimer.Dispose"/> has already been called on it (queued on a
/// thread-pool thread before the dispose), so it re-checks the batch is still the <em>current</em>
/// entry for its (Hwnd, Kind) before emitting, rather than trusting that disposing a timer means
/// its callback can never run again.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and intended to be " +
        "registered with AddHostedService<Coalescer>() once Bastion.Daemon's composition root is " +
        "wired (GitHub issue #10) — not yet wired as of this change. Same documented CA1812 " +
        "false-positive shape as WinEventPumpService/BastiondService.")]
internal sealed class Coalescer : BackgroundService
{
    /// <summary>
    /// DESIGN.md §3.2's "~75 ms" default coalescing window — "engineering practice, not a
    /// documented constant" (§3.2's own words), config-tunable via the constructor's
    /// <c>coalesceWindow</c> parameter. This is only the shipped default.
    /// </summary>
    internal static readonly TimeSpan DefaultCoalesceWindow = TimeSpan.FromMilliseconds(75);

    // Bounded, matching the ingest channel's own established pattern
    // (docs/engineering/concurrency-performance.md §1) rather than an unbounded channel: capacity
    // is an order of magnitude below the 4096 ingest capacity, reflecting that this stream is
    // already coalesced/deduplicated and so runs at a much lower rate. DropWrite for the same
    // reason the ingest channel picked it — the incoming (newest) item is sacrificed, so a shed
    // intent never disturbs the shape of what's already queued. No production consumer exists yet
    // (the Reconciler is GitHub issue #4), so this capacity is a forward-looking placeholder, not
    // a tuned value.
    internal const int IntentChannelCapacity = 1024;

    private static readonly BoundedChannelOptions s_intentChannelOptions = new(capacity: IntentChannelCapacity)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleWriter = false, // the drain loop AND per-(Hwnd,Kind) flush-timer callbacks both write
        SingleReader = true,  // one consumer: the future Reconciler (issue #4), or a test
        AllowSynchronousContinuations = false,
    };

    private readonly ChannelReader<WinEvent> _ingestReader;
    private readonly ICloakStateReader _cloakStateReader;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _coalesceWindow;
    private readonly Channel<CoalescedIntent> _intentChannel;
    private readonly ChannelWriter<CoalescedIntent> _intentWriter;
    private readonly TimerCallback _onFlushTimerFired;
    private readonly Lock _gate = new();
    private readonly Dictionary<nint, HwndState> _hwndStates = [];
    private long _droppedIntentCount;

    public Coalescer(
        ChannelReader<WinEvent> ingestReader,
        ICloakStateReader cloakStateReader,
        TimeProvider timeProvider,
        TimeSpan coalesceWindow)
    {
        ArgumentNullException.ThrowIfNull(ingestReader);
        ArgumentNullException.ThrowIfNull(cloakStateReader);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(coalesceWindow, TimeSpan.Zero);

        _ingestReader = ingestReader;
        _cloakStateReader = cloakStateReader;
        _timeProvider = timeProvider;
        _coalesceWindow = coalesceWindow;
        _onFlushTimerFired = OnFlushTimerFired;
        _intentChannel = Channel.CreateBounded<CoalescedIntent>(
            s_intentChannelOptions,
            itemDropped: _ => Interlocked.Increment(ref _droppedIntentCount));
        _intentWriter = _intentChannel.Writer;
    }

    /// <summary>
    /// The consuming side of the coalesced-intent channel. The future Reconciler (GitHub issue #4)
    /// drains this; this issue stops at emitting the stream. Same "expose a ChannelReader" pattern
    /// <see cref="WinEventPumpService.IngestReader"/> uses.
    /// </summary>
    public ChannelReader<CoalescedIntent> IntentReader => _intentChannel.Reader;

    /// <summary>
    /// Test-observable count of intents shed because <see cref="IntentReader"/>'s consumer fell
    /// behind — mirrors the ingest channel's <c>itemDropped</c> observability
    /// (docs/engineering/concurrency-performance.md §1), scaled down to a counter here since no
    /// <see cref="IReconcileNowSignal"/>-shaped production signal exists yet for this channel.
    /// </summary>
    internal long DroppedIntentCount => Interlocked.Read(ref _droppedIntentCount);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (WinEvent evt in _ingestReader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                OnEvent(evt);
            }
        }
        finally
        {
            _intentWriter.TryComplete();
        }
    }

    /// <summary>
    /// Processes one raw <see cref="WinEvent"/>. Called from <see cref="ExecuteAsync"/>'s drain
    /// loop in production; directly callable by tests (matching docs/engineering/testing.md §4's
    /// <c>FakeTimeProvider</c> illustration) without starting the host or a real channel drain.
    /// </summary>
    internal void OnEvent(WinEvent evt)
    {
        switch (evt.EventId)
        {
            case PInvoke.EVENT_OBJECT_DESTROY:
                HandleDestroy(evt.Hwnd);
                break;

            case PInvoke.EVENT_OBJECT_SHOW:
                ScheduleOrMerge(evt.Hwnd, IntentKind.WindowAppeared, evt.DwmsEventTimeMs);
                break;

            case PInvoke.EVENT_OBJECT_NAMECHANGE:
                // Open design question, resolved here: DESIGN.md §3.2 says NAMECHANGE
                // re-evaluation is "rate-limited" but does not name which of the six intents it
                // maps to. §3.3 ("late titles drive rules... re-evaluated on NAMECHANGE") and §5
                // ("A late EVENT_OBJECT_NAMECHANGE re-runs rules and may re-home the window") both
                // frame a NAMECHANGE exactly like a fresh admission — the same rules pass a
                // SHOW/UNCLOAKED triggers. WindowAppeared is therefore the natural mapping: a
                // rate-limited "re-evaluate this window" signal, semantically the same intent as
                // first appearance, not a 7th type contradicting the acceptance criteria's "six
                // intent types." Routing it through the same per-(Hwnd, WindowAppeared) debounce
                // as SHOW/UNCLOAKED gives it exactly the "rate-limited" behavior §3.2 asks for.
                ScheduleOrMerge(evt.Hwnd, IntentKind.WindowAppeared, evt.DwmsEventTimeMs);
                break;

            case PInvoke.EVENT_SYSTEM_FOREGROUND:
                ScheduleOrMerge(evt.Hwnd, IntentKind.ForegroundChanged, evt.DwmsEventTimeMs);
                break;

            case PInvoke.EVENT_SYSTEM_MOVESIZESTART:
                HandleDragStart(evt.Hwnd);
                break;

            case PInvoke.EVENT_SYSTEM_MOVESIZEEND:
                HandleDragEnd(evt.Hwnd, evt.DwmsEventTimeMs);
                break;

            case PInvoke.EVENT_OBJECT_LOCATIONCHANGE:
                HandleLocationChange(evt.Hwnd, evt.DwmsEventTimeMs);
                break;

            case PInvoke.EVENT_OBJECT_CLOAKED:
            case PInvoke.EVENT_OBJECT_UNCLOAKED:
                HandleCloakEvent(evt);
                break;

            default:
                // EVENT_OBJECT_CREATE ("ignored for admission" per DESIGN.md §5) and
                // EVENT_OBJECT_HIDE (keeps the registry slot per DESIGN.md §5, but maps to none of
                // the six typed intents this issue defines) are intentionally no-ops here, along
                // with EVENT_SYSTEM_MINIMIZESTART/END (registered by the pump per DESIGN.md §3.1,
                // but likewise has no typed-intent mapping in this issue's scope).
                break;
        }
    }

    private void HandleDestroy(nint hwnd)
    {
        lock (_gate)
        {
            if (_hwndStates.Remove(hwnd, out HwndState? state) && state.PendingBatches is { } batches)
            {
                foreach (PendingBatch batch in batches.Values)
                {
                    batch.Timer?.Dispose();
                }
            }
        }

        // Never debounced — see WindowVanished's own remarks for why.
        _ = _intentWriter.TryWrite(new WindowVanished(hwnd));
    }

    private void HandleDragStart(nint hwnd)
    {
        lock (_gate)
        {
            HwndState state = GetOrCreateStateLocked(hwnd);
            state.IsDragging = true;

            // A GeometryDrift batch pending from before the drag began would otherwise still fire
            // mid-drag or just after — redundant with the authoritative recompute MOVESIZEEND is
            // about to perform (HandleDragEnd). Drop it rather than let it emit a stale, confusing
            // signal.
            if (state.PendingBatches is { } batches && batches.Remove(IntentKind.GeometryDrift, out PendingBatch? stale))
            {
                stale.Timer?.Dispose();
            }
        }
    }

    private void HandleDragEnd(nint hwnd, uint dwmsEventTimeMs)
    {
        lock (_gate)
        {
            HwndState state = GetOrCreateStateLocked(hwnd);
            state.IsDragging = false;
            if (state.PendingBatches is not { Count: > 0 })
            {
                _hwndStates.Remove(hwnd);
            }
        }

        // DESIGN.md §3.2 ("recompute once on END") + §7 ("re-tile happens once on MOVESIZEEND"):
        // MOVESIZEEND both ends the drag interaction (DragEnded, for drag-to-swap/re-tile
        // consumers) and supplies the one authoritative geometry recompute the bracket promised —
        // both emitted immediately, never debounced, since this is a discrete state transition
        // rather than a storm to collapse.
        _ = _intentWriter.TryWrite(new DragEnded(hwnd));
        _ = _intentWriter.TryWrite(new GeometryDrift(hwnd, dwmsEventTimeMs));
    }

    private void HandleLocationChange(nint hwnd, uint dwmsEventTimeMs)
    {
        lock (_gate)
        {
            HwndState state = GetOrCreateStateLocked(hwnd);
            if (state.IsDragging)
            {
                // DESIGN.md §3.2: LOCATIONCHANGE is suppressed between MOVESIZESTART and
                // MOVESIZEEND; HandleDragEnd recomputes geometry exactly once, on END.
                return;
            }

            MergeOrCreateLocked(state, hwnd, IntentKind.GeometryDrift, dwmsEventTimeMs);
        }
    }

    private void HandleCloakEvent(WinEvent evt)
    {
        if (_cloakStateReader.IsCloaked(evt.Hwnd))
        {
            // DESIGN.md §3.2/§4's observed-behavior heuristic — see DesktopSwitchSuspected's own
            // remarks for the full classification and citation; an already-accepted design
            // decision, not re-derived here.
            ScheduleOrMerge(evt.Hwnd, IntentKind.DesktopSwitchSuspected, evt.DwmsEventTimeMs);
            return;
        }

        if (evt.EventId == PInvoke.EVENT_OBJECT_UNCLOAKED)
        {
            // DESIGN.md §3.3/§5: windows are admitted on SHOW/UNCLOAKED. A currently-not-cloaked
            // UNCLOAKED event is exactly that admission trigger — the same WindowAppeared signal
            // SHOW produces (see WindowAppeared's own remarks).
            ScheduleOrMerge(evt.Hwnd, IntentKind.WindowAppeared, evt.DwmsEventTimeMs);
            return;
        }

        // EVENT_OBJECT_CLOAKED whose current DWMWA_CLOAKED read is already back to zero: a narrow
        // race (the window cloaked-then-uncloaked between the event firing and this read) that
        // maps to none of the six typed intents. WinEvents are hints, not truth (DESIGN.md §1) —
        // dropping this one is safe because the 5 s reconciliation heartbeat (§3.4) backstops any
        // state this narrow race could leave stale.
    }

    private void ScheduleOrMerge(nint hwnd, IntentKind kind, uint dwmsEventTimeMs)
    {
        lock (_gate)
        {
            HwndState state = GetOrCreateStateLocked(hwnd);
            MergeOrCreateLocked(state, hwnd, kind, dwmsEventTimeMs);
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private HwndState GetOrCreateStateLocked(nint hwnd)
    {
        if (!_hwndStates.TryGetValue(hwnd, out HwndState? state))
        {
            state = new HwndState();
            _hwndStates[hwnd] = state;
        }

        return state;
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private void MergeOrCreateLocked(HwndState state, nint hwnd, IntentKind kind, uint dwmsEventTimeMs)
    {
        state.PendingBatches ??= [];

        if (state.PendingBatches.TryGetValue(kind, out PendingBatch? existing))
        {
            if (IsWithinCoalesceWindow(dwmsEventTimeMs, existing.LastDwmsEventTimeMs))
            {
                // Same burst: fold in and push the flush deadline out again from now — a live
                // storm keeps extending the debounce until it actually goes quiet.
                existing.LastDwmsEventTimeMs = dwmsEventTimeMs;
                existing.Timer?.Change(_coalesceWindow, Timeout.InfiniteTimeSpan);
                return;
            }

            // Too far apart in OS event-time (dwmsEventTime) to be the same burst — e.g. this
            // drain loop fell behind and is now catching up on a backlog spanning real minutes.
            // Flush the stale batch now rather than silently folding two unrelated occurrences
            // into one intent, then start a fresh one below.
            FlushLocked(existing);

            // FlushLocked may have just removed `state` from _hwndStates entirely (if this was
            // its only pending batch and it wasn't dragging) — re-register it before adding the
            // fresh batch below, or that batch would be created on an HwndState no longer
            // reachable from _hwndStates.
            _hwndStates[hwnd] = state;
        }

        // The timer's `state` argument must be `batch` itself (so OnFlushTimerFired can recover
        // it), which means the timer can only be created once `batch` already exists — construct
        // first, then arm the timer as a separate step, rather than via an object initializer.
        var batch = new PendingBatch(hwnd, kind, dwmsEventTimeMs);
        batch.Timer = _timeProvider.CreateTimer(_onFlushTimerFired, batch, _coalesceWindow, Timeout.InfiniteTimeSpan);
        state.PendingBatches[kind] = batch;
    }

    private bool IsWithinCoalesceWindow(uint currentDwmsEventTimeMs, uint lastDwmsEventTimeMs)
    {
        // dwmsEventTime is a GetTickCount()-style millisecond counter that wraps roughly every
        // 49.7 days; compare via unchecked 32-bit subtraction (the standard tick-count-delta
        // idiom) so a wraparound between the two readings can't produce a spurious huge gap. Safe
        // as long as the true elapsed time between the two events is under ~24.8 days, which any
        // realistic coalescing window here is, by many orders of magnitude.
        int deltaMs = unchecked((int)(currentDwmsEventTimeMs - lastDwmsEventTimeMs));
        return Math.Abs(deltaMs) <= _coalesceWindow.TotalMilliseconds;
    }

    private void OnFlushTimerFired(object? state)
    {
        var batch = (PendingBatch)state!;
        lock (_gate)
        {
            FlushLocked(batch);
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private void FlushLocked(PendingBatch batch)
    {
        if (!_hwndStates.TryGetValue(batch.Hwnd, out HwndState? state)
            || state.PendingBatches is not { } batches
            || !batches.TryGetValue(batch.Kind, out PendingBatch? current)
            || !ReferenceEquals(current, batch))
        {
            // Superseded by a newer batch for the same (Hwnd, Kind) — e.g. a flush timer that
            // raced a manual flush-and-restart in MergeOrCreateLocked (System.Threading.Timer's
            // own docs: a callback can still fire after Dispose(), since it may already be queued
            // on a thread-pool thread). Dispose this stale timer and return without emitting a
            // second, duplicate intent for the same burst.
            batch.Timer?.Dispose();
            return;
        }

        batches.Remove(batch.Kind);
        if (batches.Count == 0 && !state.IsDragging)
        {
            _hwndStates.Remove(batch.Hwnd);
        }

        batch.Timer?.Dispose();
        _ = _intentWriter.TryWrite(CreateIntent(batch));
    }

    private static CoalescedIntent CreateIntent(PendingBatch batch) => batch.Kind switch
    {
        IntentKind.WindowAppeared => new WindowAppeared(batch.Hwnd),
        IntentKind.ForegroundChanged => new ForegroundChanged(batch.Hwnd),
        IntentKind.DesktopSwitchSuspected => new DesktopSwitchSuspected(batch.Hwnd),
        IntentKind.GeometryDrift => new GeometryDrift(batch.Hwnd, batch.LastDwmsEventTimeMs),
        _ => throw new UnreachableException($"Unhandled {nameof(IntentKind)}: {batch.Kind}"),
    };

    /// <inheritdoc/>
    public override void Dispose()
    {
        lock (_gate)
        {
            foreach (HwndState state in _hwndStates.Values)
            {
                if (state.PendingBatches is { } batches)
                {
                    foreach (PendingBatch batch in batches.Values)
                    {
                        batch.Timer?.Dispose();
                    }
                }
            }

            _hwndStates.Clear();
        }

        _intentWriter.TryComplete();
        base.Dispose();
    }

    /// <summary>
    /// The four intent kinds this class debounces per (Hwnd, Kind) via <see cref="PendingBatch"/>.
    /// <see cref="WindowVanished"/> and <see cref="DragEnded"/> have no member here — both are
    /// always emitted immediately, never debounced (see <see cref="HandleDestroy"/>/
    /// <see cref="HandleDragEnd"/>), so neither needs a coalescing bucket at all.
    /// </summary>
    private enum IntentKind : byte
    {
        WindowAppeared,
        ForegroundChanged,
        DesktopSwitchSuspected,
        GeometryDrift,
    }

    /// <summary>One in-flight, not-yet-flushed coalescing episode for a single (Hwnd, Kind) pair.</summary>
    private sealed class PendingBatch(nint hwnd, IntentKind kind, uint dwmsEventTimeMs)
    {
        public nint Hwnd { get; } = hwnd;

        public IntentKind Kind { get; } = kind;

        public uint LastDwmsEventTimeMs { get; set; } = dwmsEventTimeMs;

        /// <summary>
        /// Set once, immediately after construction (never via an object initializer — the timer's
        /// <c>state</c> argument must be this very instance, which doesn't exist yet inside one).
        /// Nullable only to make that two-step construction representable; every code path that
        /// reads it treats a live <see cref="PendingBatch"/> as always having one.
        /// </summary>
        public ITimer? Timer { get; set; }
    }

    /// <summary>Per-HWND Coalescer state: drag-bracket status plus any pending batches.</summary>
    private sealed class HwndState
    {
        public bool IsDragging { get; set; }

        public Dictionary<IntentKind, PendingBatch>? PendingBatches { get; set; }
    }
}
