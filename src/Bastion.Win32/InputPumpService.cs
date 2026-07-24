using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Bastion.Win32;

/// <summary>
/// The Tier-1 input service (DESIGN.md §7): a single, named OS thread that registers
/// <see cref="DefaultHotkeyBindings.All"/> via <c>RegisterHotKey</c>, runs its own
/// <c>GetMessage</c>/<c>DispatchMessage</c> loop, and dispatches every fired <c>WM_HOTKEY</c> to an
/// injected <see cref="IHotkeyDispatchTarget"/>.
/// </summary>
/// <remarks>
/// <para>
/// A raw <see cref="IHostedService"/>, never
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> — the identical rule
/// <see cref="WinEventPumpService"/> follows, for the identical reason
/// (docs/engineering/daemon-architecture.md §2): since .NET 10, <c>BackgroundService.ExecuteAsync</c>
/// runs its entire body, including synchronous code before the first <see langword="await"/>, on a
/// thread-pool thread — the wrong lifetime/identity model for a Win32 message pump that must own one
/// stable, dedicated foreground OS thread for the daemon's entire life. Every structural piece of
/// this class — the <c>_threadReady</c>/<see cref="ManualResetEventSlim"/> handshake, forcing message
/// queue creation before signaling ready, the cancellation-during-<see cref="StartAsync"/> handling,
/// the 3-way <c>GetMessage</c> return check, the bounded <c>Thread.Join</c> in
/// <see cref="StopAsync"/> — is deliberately identical to <see cref="WinEventPumpService"/>'s own
/// pump-thread skeleton (GitHub issue #1), reused rather than re-derived: that issue already found
/// and fixed the message-queue race and the cancellation-leaves-orphaned-thread bug this skeleton
/// avoids. The only genuinely new pieces here are the hotkey registration/conflict-probing logic
/// (<see cref="HotkeyRegistrar"/>) and the <c>WM_HOTKEY</c> dispatch (<see cref="HotkeyDispatch"/>).
/// </para>
/// <para>
/// <b>No <c>[UnmanagedCallersOnly]</c> callback here, unlike <see cref="WinEventPumpService"/>.</b>
/// <c>RegisterHotKey</c> posts <c>WM_HOTKEY</c> to the registering thread's ordinary message queue
/// (per its own documented contract: "If this parameter is NULL, WM_HOTKEY messages are posted to
/// the message queue of the calling thread and must be processed in the message loop") — there is no
/// separate native callback to register, only a message value to recognize inside the same
/// <c>GetMessage</c>/<c>DispatchMessage</c> loop every other thread message already flows through.
/// </para>
/// <para>
/// <b><c>DispatchMessage</c> cannot deliver <c>WM_HOTKEY</c> — it must be handled directly.</b>
/// Verified against https://learn.microsoft.com/windows/win32/winmsg/about-messages-and-message-queues:
/// "If the window handle is NULL, DispatchMessage does nothing with the message." Because this pump
/// registers hotkeys with <c>hWnd = NULL</c> (associating them with the thread, not a window), every
/// <c>WM_HOTKEY</c> arrives with <c>MSG.hwnd == HWND.Null</c> — so <see cref="RunMessageLoop"/> checks
/// for it explicitly, the same way it already special-cases a <c>0</c> <c>GetMessage</c> return for
/// <c>WM_QUIT</c>, rather than relying on <c>DispatchMessage</c>/<c>TranslateMessage</c> to do
/// anything with it.
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
        "registered with AddSingleton<IHostedService, InputPumpService>() once Bastion.Daemon's " +
        "composition root is wired (GitHub issue #10) — not yet wired as of this change. Same " +
        "documented CA1812 false-positive shape as WinEventPumpService/Coalescer.")]
internal sealed class InputPumpService : IHostedService, IDisposable
{
    private readonly IHotkeyRegistrationSystem _registrationSystem;
    private readonly IHotkeyDispatchTarget _dispatchTarget;
    private readonly ILogger<InputPumpService> _logger;
    private readonly ManualResetEventSlim _threadReady = new(initialState: false);
    private Thread? _pumpThread;
    private volatile bool _stopRequested;
    private uint _pumpThreadId;

    // Written once on the pump thread, before _threadReady.Set() (see PumpLoop) — never mutated
    // again afterward. Safe to read from any thread with no further synchronization because
    // _threadReady's Set()/Wait() pair is the same full-fence happens-before edge
    // WinEventPumpService's own _pumpThreadId field relies on: StartAsync's Wait() cannot return
    // until PumpLoop's Set() call, and that call happens strictly after this field's assignment.
    private ImmutableArray<HotkeyRegistrationResult> _registrationResults = ImmutableArray<HotkeyRegistrationResult>.Empty;

    public InputPumpService(IHotkeyRegistrationSystem registrationSystem, IHotkeyDispatchTarget dispatchTarget, ILogger<InputPumpService> logger)
    {
        _registrationSystem = registrationSystem;
        _dispatchTarget = dispatchTarget;
        _logger = logger;
    }

    /// <summary>
    /// Every default binding's registration outcome (DESIGN.md §7: "every registration is probed at
    /// startup ... conflicts surfaced"). Structured — not just logged — so a future
    /// <c>bastion doctor</c> (GitHub issue #24) can report exactly which chord conflicted with what;
    /// see <see cref="HotkeyRegistrationResult"/>. Empty until <see cref="StartAsync"/> completes.
    /// </summary>
    public ImmutableArray<HotkeyRegistrationResult> RegistrationResults => _registrationResults;

    /// <summary>
    /// Test-observable: whether the pump's dedicated foreground OS thread is currently running.
    /// Exposed only so tests can assert that a canceled <see cref="StartAsync"/> leaves no orphaned
    /// pump thread behind, without reaching into private state via reflection.
    /// </summary>
    internal bool IsPumpThreadAlive => _pumpThread is { IsAlive: true };

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _pumpThread = new Thread(PumpLoop)
        {
            Name = "Bastion.InputPump",
            IsBackground = false, // outlives GC-driven finalization concerns; we own shutdown.
        };
        _pumpThread.Start();

        // Wait for the pump to register every default binding and force its own message queue into
        // existence (PumpLoop's ForceMessageQueueCreation, run just before _threadReady.Set()) before
        // returning, so a subsequent StopAsync can never race PostThreadMessage against a
        // not-yet-existent message queue — identical reasoning to WinEventPumpService's own handshake.
        try
        {
            _threadReady.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A hosted service whose StartAsync fails is not guaranteed to receive StopAsync, and
            // Dispose doesn't stop the pump either — so a canceled wait must itself stop the
            // already-started pump thread before propagating, or the foreground thread can survive
            // this call blocked in GetMessage. Reuse StopAsync's own stop-and-join logic rather than
            // duplicating it — identical to WinEventPumpService's own StartAsync catch block, for the
            // identical reason.
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
            // documented way to end a message pump this class doesn't otherwise own control flow of.
            bool posted = PInvoke.PostThreadMessage(_pumpThreadId, PInvoke.WM_QUIT, wParam: default, lParam: default);
            if (!posted)
            {
                // Not immediately fatal — the bounded Join below still turns a pump that misses
                // WM_QUIT into an observable TimeoutException rather than a silent hang — but a
                // genuine occurrence of this documented failure should be visible, not discarded.
                _logger.LogPostQuitMessageFailed(_pumpThreadId, "Input pump");
            }
        }

        return _pumpThread is { } thread && !thread.Join(TimeSpan.FromSeconds(2))
            ? Task.FromException(new TimeoutException("Input pump thread did not exit within the shutdown timeout."))
            : Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _threadReady.Dispose();

    private void PumpLoop()
    {
        _pumpThreadId = PInvoke.GetCurrentThreadId();

        try
        {
            // DESIGN.md §7: register the default table and probe every registration before this
            // thread is considered ready — a failed registration is surfaced (HotkeyRegistrar logs
            // it) but never stops the remaining bindings from being attempted.
            _registrationResults = HotkeyRegistrar.RegisterAll(_logger, _registrationSystem, DefaultHotkeyBindings.All);
            ForceMessageQueueCreation();

            // StartAsync's _threadReady.Wait(...) unblocks here — registration has been probed and
            // this thread's message queue is guaranteed to exist.
            _threadReady.Set();

            RunMessageLoop();
        }
        finally
        {
            // UnregisterHotKey must run on the same thread that called RegisterHotKey — a documented
            // fact ("Frees a hot key previously registered by the calling thread") — which is why
            // this happens here, inside PumpLoop, rather than from StopAsync's caller thread. Matches
            // WinEventPumpService's identical same-thread requirement for UnhookWinEvent.
            HotkeyRegistrar.UnregisterAll(_logger, _registrationSystem, _registrationResults);

            // Safety net: if RegisterAll/something before the Set() call above ever threw
            // unexpectedly, this keeps StartAsync's Wait from hanging forever. A no-op on the
            // ordinary shutdown path, since _threadReady is already set by then.
            _threadReady.Set();
        }
    }

    // Identical idiom to WinEventPumpService.ForceMessageQueueCreation, and for the identical
    // reason: RegisterHotKey's own documented contract states only that WM_HOTKEY messages are
    // posted to the calling thread's queue, which is a distinct, weaker claim than "registration
    // forces the queue to exist." PostThreadMessageW's own Remarks
    // (https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-postthreadmessagew#remarks)
    // document the queue-creation problem directly and give the fix verbatim: call PeekMessage once
    // with PM_NOREMOVE to force the queue into existence, before StopAsync's PostThreadMessage(WM_QUIT)
    // can ever race a not-yet-existent queue.
    private static void ForceMessageQueueCreation() =>
        _ = PInvoke.PeekMessage(
            out _,
            HWND.Null,
            wMsgFilterMin: PInvoke.WM_USER,
            wMsgFilterMax: PInvoke.WM_USER,
            PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);

    private void RunMessageLoop()
    {
        while (!_stopRequested)
        {
            // GetMessage's return is a 3-way signal, not the 0/nonzero shorthand its own docs warn
            // against relying on: -1 means an error occurred, 0 means WM_QUIT was retrieved, and
            // anything else is a real message to translate/dispatch. Identical to
            // WinEventPumpService's own message loop.
            int result = PInvoke.GetMessage(out MSG message, HWND.Null, wMsgFilterMin: 0, wMsgFilterMax: 0);
            switch (result)
            {
                case 0:
                    return; // WM_QUIT — StopAsync's PostThreadMessage, or shutdown already in flight.
                case -1:
                    // Unexpected: this pump always passes a null hWnd filter, so the documented
                    // invalid-window-handle trigger for -1 should not occur in practice — exit rather
                    // than spin forever on a persistent error, per GetMessage's own docs.
                    _logger.LogMessageLoopFault("Input pump");
                    return;
                default:
                    if (message.message == PInvoke.WM_HOTKEY)
                    {
                        // DispatchMessage cannot deliver this — see the class remarks above — so it
                        // is handled directly rather than translated/dispatched.
                        DispatchHotkey(message);
                    }
                    else
                    {
                        _ = PInvoke.TranslateMessage(message);
                        _ = PInvoke.DispatchMessage(message);
                    }

                    break;
            }
        }
    }

    private void DispatchHotkey(MSG message)
    {
        // WM_HOTKEY's wParam carries the RegisterHotKey id that fired
        // (https://learn.microsoft.com/windows/win32/inputdev/wm-hotkey). WPARAM's implicit nuint
        // conversion, then a narrowing cast to int, is safe because RegisterHotKey's own documented
        // contract limits every application id to the range 0x0000-0xBFFF -- comfortably inside
        // int's range -- regardless of whether DefaultHotkeyBindings.All's own ids stay sequential
        // in the future.
        int id = (int)(nuint)message.wParam;
        if (HotkeyDispatch.TryResolveCommand(_registrationResults, id, out HotkeyCommand command))
        {
            // Never call _dispatchTarget.OnHotkeyInvoked directly here — HotkeyDispatch.InvokeSafely
            // is the mandatory crash-containment boundary; see its own remarks for why an escaping
            // exception on this raw dedicated thread would otherwise kill the whole daemon process.
            HotkeyDispatch.InvokeSafely(_logger, _dispatchTarget, command);
        }
    }
}
