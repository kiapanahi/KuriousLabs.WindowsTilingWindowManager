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

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pumpThread = new Thread(PumpLoop)
        {
            Name = "Bastion.WinEventPump",
            IsBackground = false, // outlives GC-driven finalization concerns; we own shutdown.
        };
        _pumpThread.Start();

        // Wait for the pump to install its hook(s) and record its thread id before returning, so
        // a subsequent StopAsync can never race PostThreadMessage against a not-yet-existent
        // message queue (docs/engineering/daemon-architecture.md §2).
        _threadReady.Wait(cancellationToken);
        return Task.CompletedTask;
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
            _ = PInvoke.PostThreadMessage(_pumpThreadId, PInvoke.WM_QUIT, wParam: default, lParam: default);
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
            UnregisterHooks();
            contextHandle.Free();

            // Safety net: if RegisterHooks/something before the Set() call above ever threw
            // unexpectedly, this keeps StartAsync's Wait from hanging forever. A no-op on the
            // ordinary shutdown path, since _threadReady is already set by then.
            _threadReady.Set();
        }
    }

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

    private void UnregisterHooks()
    {
        foreach (HWINEVENTHOOK hook in _registeredHooks)
        {
            _ = PInvoke.UnhookWinEvent(hook);
            s_hookContexts.TryRemove(hook, out _);
        }

        _registeredHooks.Clear();
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
            HWND root = WindowProbe.GetRootAncestor(hwnd);
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
