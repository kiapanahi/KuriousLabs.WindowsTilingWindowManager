using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Bastion.Win32;

/// <summary>
/// Structured logging for hook/hotkey pump diagnostics (DESIGN.md §3.9,
/// docs/engineering/daemon-architecture.md §3). Two distinct mechanisms live in this one class,
/// because exactly one of its methods is genuinely constrained by where it is called from:
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="LogCallbackFault(Exception)"/></b> is called from inside an
/// <c>[UnmanagedCallersOnly]</c> callback's mandatory catch-all
/// (<see cref="WinEventPumpService"/>'s <c>OnWinEvent</c>, <c>WindowProbe</c>'s <c>OnEnumWindow</c>,
/// <c>ApplicationFrameUwpAttributionProvider</c>'s <c>OnEnumChildWindow</c>) — resolving anything
/// from a DI container there is unsafe/unavailable (docs/engineering/interop.md §3.2/§3.3). It reads
/// a static, once-set <see cref="ILogger"/> reference instead (<see cref="Initialize(ILogger)"/>),
/// and is itself a hardened, never-throw boundary — see its own remarks.
/// </para>
/// <para>
/// <b>Every other method here</b> runs from ordinary managed pump-thread code — never a native
/// callback — and is a plain <c>this ILogger logger</c> extension method, the same shape
/// <c>StartupLog</c> establishes for a static utility class with no DI-instance home of its own. Each
/// caller threads through whatever <see cref="ILogger{TCategoryName}"/> its own constructor already
/// has injected; a normal DI-registered logger is entirely safe to use for these.
/// </para>
/// </remarks>
internal static partial class HookDiagnostics
{
    // Written exactly once in production, by Bastion.Daemon's composition root (Program.cs), after
    // the host is built and before host.RunAsync() starts any hosted service that could fire a hook
    // callback -- see Initialize's own remarks. Volatile.Write/Read (rather than a plain field
    // read/write) make the intended cross-thread happens-before relationship explicit: the pump
    // thread that later reads this field is never the thread that set it.
    private static ILogger? s_logger;

    /// <summary>
    /// One-time handoff of a real <see cref="ILogger"/> for <see cref="LogCallbackFault(Exception)"/>
    /// to log through. Called once from the composition root after the host is built and before
    /// <c>host.RunAsync()</c> starts any hosted service — ordinary managed startup code, never called
    /// from inside a hook callback itself, so resolving <paramref name="logger"/> from DI beforehand
    /// is fine.
    /// </summary>
    public static void Initialize(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Volatile.Write(ref s_logger, logger);
    }

    /// <summary>
    /// Test-only: restores the pre-<see cref="Initialize(ILogger)"/> state so a test exercising the
    /// one-time handoff never leaks a fake logger into whichever test happens to run next in the same
    /// process. Internal rather than <see langword="private"/> only so <c>Bastion.Win32.Tests</c>
    /// (already granted access via this assembly's <c>InternalsVisibleTo</c>) can call it from a
    /// <see langword="finally"/> block.
    /// </summary>
    internal static void ResetForTesting() => Volatile.Write(ref s_logger, null);

    /// <summary>
    /// Last-resort logging for exceptions caught inside an <c>[UnmanagedCallersOnly]</c> callback
    /// body. Reads whatever <see cref="Initialize(ILogger)"/> most recently set — never resolves
    /// anything from a DI container — and falls back to the original minimal <c>Console.Error</c>
    /// sink if nothing has been initialized yet (e.g. a test constructing <c>WinEventPumpService</c>/
    /// <c>WindowProbe</c> directly via <c>InternalsVisibleTo</c>, with no composition root ever having
    /// run). See docs/engineering/interop.md §3.3.
    /// </summary>
    /// <remarks>
    /// This method — and everything it calls — must never itself throw: it is invoked from inside the
    /// exact mandatory catch-all that exists to guarantee no exception ever crosses the
    /// <c>[UnmanagedCallersOnly]</c> native boundary undefined-behavior-style (interop.md §3.3). The
    /// inner try/catch below is deliberate defense in depth against a hypothetical faulting
    /// <see cref="ILogger"/> provider, not decoration — a provider throwing here would otherwise
    /// become exactly the kind of escaping exception this whole mechanism exists to prevent.
    /// </remarks>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This method is called from inside an [UnmanagedCallersOnly] callback's own " +
            "mandatory catch-all, so it inherits the identical must-never-throw obligation " +
            "transitively -- a faulting ILogger provider must not itself become the exception that " +
            "escapes the native boundary. See this method's own remarks and " +
            "docs/engineering/interop.md §3.3.")]
    public static void LogCallbackFault(Exception exception)
    {
        ILogger? logger = Volatile.Read(ref s_logger);
        if (logger is null)
        {
            Console.Error.WriteLine($"[Bastion.Win32] hook callback fault: {exception}");
            return;
        }

        try
        {
            LogCallbackFaultCore(logger, exception);
        }
        catch
        {
            Console.Error.WriteLine($"[Bastion.Win32] hook callback fault: {exception}");
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Hook callback fault.")]
    private static partial void LogCallbackFaultCore(ILogger logger, Exception exception);

    /// <summary>
    /// Logs a failed <c>SetWinEventHook</c> registration for one narrow event range. Per
    /// DESIGN.md §1/§3.1, WinEvents are hints, never the sole source of truth — a failed
    /// registration skips that range rather than crashing daemon startup. Called from
    /// <c>WinEventPumpService.RegisterHooks</c> — ordinary managed pump-thread code, not the
    /// <c>[UnmanagedCallersOnly]</c> callback itself.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "SetWinEventHook failed for event range 0x{EventMin:X4}-0x{EventMax:X4}; that range will not be monitored.")]
    public static partial void LogHookRegistrationFailed(this ILogger logger, uint eventMin, uint eventMax);

    /// <summary>
    /// Logs an unexpected <c>GetMessage</c> failure (a -1 return) on <paramref name="pumpName"/>'s
    /// message loop — shared by every <c>GetMessage</c>/<c>DispatchMessage</c>-loop pump in this
    /// assembly (<see cref="WinEventPumpService"/>, <see cref="InputPumpService"/>), never
    /// hardcoded to one. <c>GetMessage</c>'s own docs warn against the naive 0/nonzero check for
    /// exactly this reason; every one of these pumps implements the correct 3-way check but always
    /// passes a null <c>hWnd</c> filter, so the documented invalid-window-handle trigger for -1
    /// should not occur in practice — this sink exists so a genuine occurrence is observable rather
    /// than silent. Called from each pump's own message-loop body — ordinary managed code, not a
    /// native callback.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "{PumpName}'s GetMessage loop returned -1 (error); exiting the pump loop.")]
    public static partial void LogMessageLoopFault(this ILogger logger, string pumpName);

    /// <summary>
    /// Logs a failed <c>PostThreadMessage(WM_QUIT)</c> call from <paramref name="pumpName"/>'s
    /// <c>StopAsync</c> — shared by every pump-thread <c>IHostedService</c> in this assembly, never
    /// hardcoded to one. Per <c>PostThreadMessage</c>'s own documented failure modes, the pump
    /// thread's message queue may not exist yet, the thread id may already be stale, a UIPI
    /// integrity-level check may have blocked it, or the per-queue message quota may have been hit.
    /// Not immediately fatal on its own: <c>StopAsync</c>'s bounded <c>Thread.Join</c> still turns a
    /// pump that never receives <c>WM_QUIT</c> into an observable <see cref="TimeoutException"/>
    /// rather than a silent hang, but a genuine occurrence of this specific failure should be
    /// visible rather than discarded. Called from <c>StopAsync</c> itself — ordinary managed code,
    /// not a native callback.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "PostThreadMessage(WM_QUIT) to thread {ThreadId} failed; the {PumpName} may not exit until StopAsync's join timeout.")]
    public static partial void LogPostQuitMessageFailed(this ILogger logger, uint threadId, string pumpName);

    /// <summary>
    /// Logs a hook that failed to unregister via <c>UnhookWinEvent</c>. Per
    /// docs/engineering/interop.md §3.2, a failed unhook means the hook may still be registered,
    /// so its shared callback context is deliberately retained rather than freed — see
    /// <see cref="LogHookContextLeakedAfterFailedUnhook"/>. Called from
    /// <c>WinEventPumpService.UnregisterHooks</c> — ordinary managed pump-thread code.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "UnhookWinEvent failed for hook 0x{HookHandle:X}; it may still be registered, so its callback context will not be freed.")]
    public static partial void LogUnhookWinEventFailed(this ILogger logger, nint hookHandle);

    /// <summary>
    /// Logs that the WinEvent pump's shared <c>GCHandle</c> callback context is being
    /// deliberately leaked because at least one hook failed to unregister (see
    /// <see cref="LogUnhookWinEventFailed"/>). Freeing it anyway risks a still-live native
    /// registration invoking <see cref="WinEventPumpService"/>'s callback with a
    /// <c>GCHandle</c> that no longer resolves to a valid target — an intentional, logged leak on
    /// this abnormal shutdown path is far safer than that use-after-free class of bug, matching
    /// this codebase's general fail-soft/degrade-rather-than-crash posture. Called from
    /// <c>WinEventPumpService.PumpLoop</c>'s <see langword="finally"/> block — ordinary managed code.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "At least one WinEvent hook failed to unregister; intentionally leaking its shared callback context rather than risking a use-after-free.")]
    public static partial void LogHookContextLeakedAfterFailedUnhook(this ILogger logger);

    /// <summary>
    /// Logs a hotkey registration that failed (a bare zero <c>BOOL</c> return from
    /// <c>RegisterHotKey</c>). Per DESIGN.md §7's honesty note, this is treated as a conflict
    /// unconditionally — never gated on <c>GetLastError</c> returning specifically
    /// <c>ERROR_HOTKEY_ALREADY_REGISTERED</c>, since that code is observed behavior for this API,
    /// not a contractual guarantee. <paramref name="errorCode"/> is logged for diagnostics only,
    /// never branched on to decide whether the failure "counts" as a conflict. Called from
    /// <c>HotkeyRegistrar.RegisterAll</c> — ordinary managed pump-thread code.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "RegisterHotKey failed for id {Id} (modifiers=0x{Modifiers:X}, vk=0x{VirtualKeyCode:X}); treating as a conflict regardless of the specific error (GetLastError observed: {ErrorCode}).")]
    public static partial void LogHotkeyRegistrationConflict(this ILogger logger, int id, HOT_KEY_MODIFIERS modifiers, uint virtualKeyCode, WIN32_ERROR? errorCode);

    /// <summary>
    /// Logs a hotkey that failed to unregister via <c>UnregisterHotKey</c> during shutdown. Not
    /// immediately fatal on its own — the pump thread is exiting regardless — but a genuine
    /// occurrence should be visible rather than silently discarded, matching
    /// <see cref="LogUnhookWinEventFailed"/>'s own rationale for the WinEvent pump's equivalent
    /// shutdown-time failure. Called from <c>HotkeyRegistrar.UnregisterAll</c> — ordinary managed
    /// pump-thread code.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "UnregisterHotKey failed for id {Id} during shutdown.")]
    public static partial void LogUnregisterHotkeyFailed(this ILogger logger, int id);

    /// <summary>
    /// Logs a dispatched <see cref="HotkeyCommand"/> — the pre-composition-root stand-in for
    /// actually invoking a Reconciler-driven layout command (GitHub issue #10). See
    /// <see cref="LoggingHotkeyDispatchTarget"/>. Called from
    /// <c>LoggingHotkeyDispatchTarget.OnHotkeyInvoked</c> — ordinary managed pump-thread code.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Hotkey invoked: {Command}.")]
    public static partial void LogHotkeyInvoked(this ILogger logger, HotkeyCommand command);

    /// <summary>
    /// Logs an exception thrown by an <see cref="IHotkeyDispatchTarget"/> while handling
    /// <paramref name="command"/>. Contained here rather than left to propagate — see
    /// <see cref="HotkeyDispatch.InvokeSafely"/>'s remarks for why an unhandled exception on the
    /// input pump's raw dedicated thread would otherwise terminate the whole <c>bastiond</c>
    /// process. Called from <c>HotkeyDispatch.InvokeSafely</c>'s catch block, which per its own
    /// remarks runs on the input pump's raw dedicated thread — ordinary managed code, not a native
    /// callback.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Hotkey dispatch fault for command {Command}.")]
    public static partial void LogHotkeyDispatchFault(this ILogger logger, HotkeyCommand command, Exception exception);
}
