using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Bastion.Win32;

/// <summary>
/// The dedicated WinEvent ingest pump (DESIGN.md §3.1): a single, named OS thread that registers
/// six narrow, out-of-context <c>SetWinEventHook</c> ranges, runs its own
/// <c>GetMessage</c>/<c>DispatchMessage</c> loop, and enqueues filtered, normalized events into a
/// bounded channel that a future Coalescer (GitHub issue #2) will drain via
/// <see cref="IngestReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// A raw <see cref="IHostedService"/>, never
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>: since .NET 10,
/// <c>BackgroundService.ExecuteAsync</c> runs its entire body — including synchronous code before
/// the first <see langword="await"/> — on a thread-pool thread, which is the wrong lifetime/identity
/// model for a Win32 message pump that must own one stable, dedicated foreground OS thread for the
/// daemon's entire life (docs/engineering/daemon-architecture.md §2).
/// </para>
/// <para>
/// DESIGN.md §3.1 registers <em>six</em> separate, narrow hook ranges — not one call spanning the
/// full <c>EVENT_SYSTEM_FOREGROUND</c>..<c>EVENT_OBJECT_UNCLOAKED</c> spread, which would also
/// enqueue every event ID in between that Bastion has no use for:
/// <c>EVENT_SYSTEM_FOREGROUND</c>; <c>MOVESIZESTART</c>-<c>MOVESIZEEND</c>;
/// <c>MINIMIZESTART</c>-<c>MINIMIZEEND</c>; <c>OBJECT_CREATE</c>-<c>OBJECT_HIDE</c>;
/// <c>OBJECT_LOCATIONCHANGE</c>-<c>OBJECT_NAMECHANGE</c>; <c>OBJECT_CLOAKED</c>-<c>OBJECT_UNCLOAKED</c>.
/// </para>
/// <para>
/// Not yet wired into the composition root (<c>Bastion.Daemon</c>) — that is GitHub issue #10;
/// this type is constructed directly by tests today via <c>InternalsVisibleTo</c>.
/// </para>
/// <para>
/// <b>Message-queue creation is forced explicitly, not assumed.</b> Verified against
/// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwineventhook and
/// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-postthreadmessagew: nothing in
/// <c>SetWinEventHook</c>'s documented contract creates this thread's message queue as a side
/// effect — its Remarks only require the calling thread to pump messages ("must have a message
/// loop in order to receive events"), which is a distinct, weaker claim than "registration forces
/// the queue to exist." <c>PostThreadMessage</c>'s own Remarks document the queue-creation problem
/// directly and give the fix verbatim: call <c>PeekMessage</c> once with <c>PM_NOREMOVE</c> to
/// force the queue into existence. <see cref="PumpLoop"/> does exactly that
/// (<see cref="ForceMessageQueueCreation"/>) before signaling <see cref="_threadReady"/>, so
/// <see cref="StopAsync"/>'s <c>PostThreadMessage</c> can never race a not-yet-existent queue.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and intended to be " +
        "registered with AddSingleton<IHostedService, WinEventPumpService>() once Bastion.Daemon's " +
        "composition root is wired (GitHub issue #10) — not yet wired as of this change. Same " +
        "documented CA1812 false-positive shape as BastiondService.")]
internal sealed class WinEventPumpService : IHostedService, IDisposable
{
    // Six separate, narrow SetWinEventHook ranges — DESIGN.md §3.1 deliberately, see the class
    // remarks above.
    private static readonly (uint Min, uint Max)[] s_eventRanges =
    [
        (PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND),
        (PInvoke.EVENT_SYSTEM_MOVESIZESTART, PInvoke.EVENT_SYSTEM_MOVESIZEEND),
        (PInvoke.EVENT_SYSTEM_MINIMIZESTART, PInvoke.EVENT_SYSTEM_MINIMIZEEND),
        (PInvoke.EVENT_OBJECT_CREATE, PInvoke.EVENT_OBJECT_HIDE),
        (PInvoke.EVENT_OBJECT_LOCATIONCHANGE, PInvoke.EVENT_OBJECT_NAMECHANGE),
        (PInvoke.EVENT_OBJECT_CLOAKED, PInvoke.EVENT_OBJECT_UNCLOAKED),
    ];

    // GCHandle context registry keyed by hook handle (interop.md §3.2) — a static, process-wide
    // dictionary because neither SetWinEventHook nor the WinEventProc callback it invokes has a
    // user-data slot to stash a per-instance reference in. ConcurrentDictionary (not a plain
    // Dictionary + lock) so this correctly generalizes if a second pump instance is ever
    // constructed concurrently — e.g. in a future test — even though exactly one instance exists
    // in production today.
    private static readonly ConcurrentDictionary<HWINEVENTHOOK, GCHandle> s_hookContexts = new();

    private readonly Channel<WinEvent> _ingestChannel;
    private readonly List<HWINEVENTHOOK> _registeredHooks = new(capacity: s_eventRanges.Length);
    private readonly ManualResetEventSlim _threadReady = new(initialState: false);
    private Thread? _pumpThread;
    private volatile bool _stopRequested;
    private uint _pumpThreadId;

    public WinEventPumpService(IReconcileNowSignal reconcileNowSignal)
    {
        _ingestChannel = WinEventChannelFactory.CreateIngestChannel(reconcileNowSignal);
    }

    /// <summary>
    /// The consuming side of the ingest channel. The future Coalescer (GitHub issue #2) drains
    /// this; this issue hands events off and stops here.
    /// </summary>
    public ChannelReader<WinEvent> IngestReader => _ingestChannel.Reader;

    /// <summary>
    /// Test-observable: whether the pump's dedicated foreground OS thread is currently running.
    /// Exposed only so tests can assert that a canceled <see cref="StartAsync"/> leaves no
    /// orphaned pump thread behind, without reaching into private state via reflection.
    /// </summary>
    internal bool IsPumpThreadAlive => _pumpThread is { IsAlive: true };

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _pumpThread = new Thread(PumpLoop)
        {
            Name = "Bastion.WinEventPump",
            IsBackground = false, // outlives GC-driven finalization concerns; we own shutdown.
        };
        _pumpThread.Start();

        // Wait for the pump to install its hook(s) and force its own message queue into
        // existence (PumpLoop's ForceMessageQueueCreation, run just before _threadReady.Set())
        // before returning, so a subsequent StopAsync can never race PostThreadMessage against a
        // not-yet-existent message queue (see the class remarks above).
        try
        {
            _threadReady.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A hosted service whose StartAsync fails is not guaranteed to receive StopAsync, and
            // Dispose doesn't stop the pump either — so a canceled wait must itself stop the
            // already-started pump thread before propagating, or the foreground thread can
            // survive this call blocked in GetMessage. Reuse StopAsync's own stop-and-join logic
            // rather than duplicating it. StopAsync's own cancellationToken parameter goes unused
            // today (see its body); passing CancellationToken.None rather than the token that
            // just canceled makes that explicit rather than accidental (CA2016). If the join
            // itself times out, StopAsync's own TimeoutException surfaces here instead of the
            // OperationCanceledException below — a stuck pump thread is the more urgent failure
            // to report of the two.
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopRequested = true;

        if (_pumpThreadId != 0)
        {
            // Unblocks PumpLoop's GetMessage loop from this (StopAsync's caller's) thread — the
            // documented way to end a message pump this class doesn't otherwise own control flow
            // of. PostThreadMessage posts without waiting; GetMessage retrieves it on the pump
            // thread's next loop iteration.
            bool posted = PInvoke.PostThreadMessage(_pumpThreadId, PInvoke.WM_QUIT, wParam: default, lParam: default);
            if (!posted)
            {
                // Not immediately fatal — the bounded Join below still turns a pump that misses
                // WM_QUIT into an observable TimeoutException rather than a silent hang — but a
                // genuine occurrence of this documented failure should be visible, not discarded.
                HookDiagnostics.LogPostQuitMessageFailed(_pumpThreadId);
            }
        }

        return _pumpThread is { } thread && !thread.Join(TimeSpan.FromSeconds(2))
            ? Task.FromException(new TimeoutException("WinEvent pump thread did not exit within the shutdown timeout."))
            : Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _threadReady.Dispose();

    private void PumpLoop()
    {
        _pumpThreadId = PInvoke.GetCurrentThreadId();

        var contextHandle = GCHandle.Alloc(_ingestChannel.Writer, GCHandleType.Normal);
        try
        {
            RegisterHooks(contextHandle);
            ForceMessageQueueCreation();

            // StartAsync's _threadReady.Wait(...) unblocks here — every hook that could register
            // has (DESIGN.md's WinEvents-are-hints posture means a partial registration is a
            // degraded-but-running pump, not a startup failure).
            _threadReady.Set();

            RunMessageLoop();
        }
        finally
        {
            // UnhookWinEvent must run on the same thread that called SetWinEventHook — a verified
            // fact (docs/engineering/interop.md) — which is why this happens here, inside
            // PumpLoop, rather than from StopAsync's caller thread.
            if (UnregisterHooks())
            {
                contextHandle.Free();
            }
            else
            {
                // interop.md §3.2: free the shared GCHandle only after every hook's
                // UnhookWinEvent succeeded. At least one hook here may still be registered and
                // could still invoke OnWinEvent with this same contextHandle — freeing it anyway
                // risks that callback resolving a freed GCHandle. An intentional, logged leak is
                // far safer than that use-after-free class of bug.
                HookDiagnostics.LogHookContextLeakedAfterFailedUnhook();
            }

            // Safety net: if RegisterHooks/something before the Set() call above ever threw
            // unexpectedly, this keeps StartAsync's Wait from hanging forever. A no-op on the
            // ordinary shutdown path, since _threadReady is already set by then.
            _threadReady.Set();
        }
    }

    // DOCUMENTED CONTRACT — quoted verbatim from PostThreadMessageW's own Remarks
    // (https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-postthreadmessagew#remarks):
    // "The thread to which the message is posted must have created a message queue, or else the
    // call to PostThreadMessage fails. ... call PeekMessage as shown here to force the system to
    // create the message queue. PeekMessage(&msg, NULL, WM_USER, WM_USER, PM_NOREMOVE)". See the
    // class remarks above for why this pump cannot rely on SetWinEventHook itself to have created
    // the queue as a side effect. The WM_USER..WM_USER filter range is part of the documented
    // idiom, not an arbitrary choice — it ensures nothing already queued is ever actually consumed.
    private static void ForceMessageQueueCreation() =>
        _ = PInvoke.PeekMessage(
            out _,
            HWND.Null,
            wMsgFilterMin: PInvoke.WM_USER,
            wMsgFilterMax: PInvoke.WM_USER,
            PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);

    private void RegisterHooks(GCHandle contextHandle)
    {
        foreach ((uint min, uint max) in s_eventRanges)
        {
            HWINEVENTHOOK hook = RegisterOneHook(min, max);
            if (hook.IsNull)
            {
                // Best-effort per DESIGN.md §1/§3.1: WinEvents are hints, never the sole source of
                // truth, so a failed registration for one range skips that range rather than
                // crashing daemon startup — the Reconciler's heartbeat re-sync still catches
                // whatever this range would have reported.
                HookDiagnostics.LogHookRegistrationFailed(min, max);
                continue;
            }

            s_hookContexts[hook] = contextHandle;
            _registeredHooks.Add(hook);
        }
    }

    private static unsafe HWINEVENTHOOK RegisterOneHook(uint eventMin, uint eventMax) =>
        PInvoke.SetWinEventHook(
            eventMin: eventMin,
            eventMax: eventMax,
            hmodWinEventProc: HMODULE.Null,
            pfnWinEventProc: &OnWinEvent,
            idProcess: 0,
            idThread: 0,
            dwFlags: PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);

    /// <returns>
    /// <see langword="true"/> if every registered hook's <c>UnhookWinEvent</c> call succeeded;
    /// <see langword="false"/> if at least one failed, so <see cref="PumpLoop"/> knows the shared
    /// <see cref="GCHandle"/> context is not yet safe to free (interop.md §3.2).
    /// </returns>
    private bool UnregisterHooks()
    {
        bool allUnregistered = true;
        foreach (HWINEVENTHOOK hook in _registeredHooks)
        {
            bool unhookSucceeded = PInvoke.UnhookWinEvent(hook);
            if (!HookUnregistration.ApplyResult(hook, unhookSucceeded, s_hookContexts))
            {
                HookDiagnostics.LogUnhookWinEventFailed(hook);
                allUnregistered = false;
            }
        }

        _registeredHooks.Clear();
        return allUnregistered;
    }

    private void RunMessageLoop()
    {
        while (!_stopRequested)
        {
            // GetMessage's return is a 3-way signal, not the 0/nonzero shorthand its own docs warn
            // against relying on: -1 means an error occurred, 0 means WM_QUIT was retrieved, and
            // anything else is a real message to translate/dispatch.
            int result = PInvoke.GetMessage(out MSG message, HWND.Null, wMsgFilterMin: 0, wMsgFilterMax: 0);
            switch (result)
            {
                case 0:
                    return; // WM_QUIT — StopAsync's PostThreadMessage, or shutdown already in flight.
                case -1:
                    // Unexpected: this pump always passes a null hWnd filter, so the documented
                    // invalid-window-handle trigger for -1 should not occur in practice — exit
                    // rather than spin forever on a persistent error, per GetMessage's own docs.
                    HookDiagnostics.LogMessageLoopFault();
                    return;
                default:
                    _ = PInvoke.TranslateMessage(message);
                    _ = PInvoke.DispatchMessage(message);
                    break;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Mandatory catch-all: an exception must never escape an " +
            "[UnmanagedCallersOnly] callback across the native boundary. See " +
            "docs/engineering/interop.md §3.3.")]
    private static void OnWinEvent(
        HWINEVENTHOOK hWinEventHook,
        uint eventId,
        HWND hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        try
        {
            if (!WinEventFilter.IsRelevantWindowEvent(hwnd, idObject, idChild))
            {
                return;
            }

            if (!s_hookContexts.TryGetValue(hWinEventHook, out GCHandle contextHandle))
            {
                // Unknown hook — e.g. a stray callback delivered after this hook's registration
                // was removed from the registry during shutdown. Defensive; should not happen
                // given UnhookWinEvent/registry-removal run on this same thread.
                return;
            }

            var writer = (ChannelWriter<WinEvent>)contextHandle.Target!;
            HWND root = WinEventRootNormalizer.NormalizeRoot(hwnd, WindowProbe.GetRootAncestor(hwnd));
            _ = writer.TryWrite(new WinEvent(root, eventId, dwmsEventTime));
        }
        catch (Exception ex)
        {
            // Mandatory catch-all — an exception must never escape an [UnmanagedCallersOnly]
            // method across the native boundary. See docs/engineering/interop.md §3.3.
            HookDiagnostics.LogCallbackFault(ex);
        }
    }
}
