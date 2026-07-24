using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Bastion.Win32;

/// <summary>
/// Last-resort logging for exceptions caught inside an <c>[UnmanagedCallersOnly]</c> callback
/// body, where no <c>ILogger</c> is guaranteed to be reachable/safe to call. See
/// docs/engineering/interop.md §3.3.
/// </summary>
/// <remarks>
/// TODO(DESIGN.md §3.9, docs/engineering/daemon-architecture.md): wire this to the daemon's
/// structured logging pipeline once the composition root exists; until then this is a minimal,
/// allocation-conscious fallback so the catch-all requirement has a real (not stubbed-empty)
/// implementation.
/// </remarks>
internal static class HookDiagnostics
{
    public static void LogCallbackFault(Exception exception) =>
        Console.Error.WriteLine($"[Bastion.Win32] hook callback fault: {exception}");

    /// <summary>
    /// Logs a failed <c>SetWinEventHook</c> registration for one narrow event range. Per
    /// DESIGN.md §1/§3.1, WinEvents are hints, never the sole source of truth — a failed
    /// registration skips that range rather than crashing daemon startup.
    /// </summary>
    public static void LogHookRegistrationFailed(uint eventMin, uint eventMax) =>
        Console.Error.WriteLine(
            $"[Bastion.Win32] SetWinEventHook failed for range 0x{eventMin:X4}-0x{eventMax:X4}; " +
            "that range will not be monitored.");

    /// <summary>
    /// Logs an unexpected <c>GetMessage</c> failure (a -1 return) on the WinEvent pump thread's
    /// message loop. <c>GetMessage</c>'s own docs warn against the naive 0/nonzero check for
    /// exactly this reason; this pump implements the correct 3-way check but always passes a null
    /// <c>hWnd</c> filter, so the documented invalid-window-handle trigger for -1 should not occur
    /// in practice — this sink exists so a genuine occurrence is observable rather than silent.
    /// </summary>
    public static void LogMessageLoopFault() =>
        Console.Error.WriteLine(
            "[Bastion.Win32] WinEvent pump's GetMessage loop returned -1 (error); exiting the pump loop.");

    /// <summary>
    /// Logs a failed <c>PostThreadMessage(WM_QUIT)</c> call from <c>StopAsync</c> — per its own
    /// documented failure modes, the pump thread's message queue may not exist yet, the thread id
    /// may already be stale, a UIPI integrity-level check may have blocked it, or the per-queue
    /// message quota may have been hit. Not immediately fatal on its own: <c>StopAsync</c>'s
    /// bounded <c>Thread.Join</c> still turns a pump that never receives <c>WM_QUIT</c> into an
    /// observable <see cref="TimeoutException"/> rather than a silent hang, but a genuine
    /// occurrence of this specific failure should be visible rather than discarded (the call site
    /// previously ignored this return value outright).
    /// </summary>
    public static void LogPostQuitMessageFailed(uint threadId) =>
        Console.Error.WriteLine(
            $"[Bastion.Win32] PostThreadMessage(WM_QUIT) to thread {threadId} failed; the WinEvent " +
            "pump may not exit until StopAsync's join timeout.");

    /// <summary>
    /// Logs a hook that failed to unregister via <c>UnhookWinEvent</c>. Per
    /// docs/engineering/interop.md §3.2, a failed unhook means the hook may still be registered,
    /// so its shared callback context is deliberately retained rather than freed — see
    /// <see cref="LogHookContextLeakedAfterFailedUnhook"/>.
    /// </summary>
    public static void LogUnhookWinEventFailed(nint hookHandle) =>
        Console.Error.WriteLine(
            $"[Bastion.Win32] UnhookWinEvent failed for hook 0x{hookHandle:X}; it may still be " +
            "registered, so its callback context will not be freed.");

    /// <summary>
    /// Logs that the WinEvent pump's shared <c>GCHandle</c> callback context is being
    /// deliberately leaked because at least one hook failed to unregister (see
    /// <see cref="LogUnhookWinEventFailed"/>). Freeing it anyway risks a still-live native
    /// registration invoking <see cref="WinEventPumpService"/>'s callback with a
    /// <c>GCHandle</c> that no longer resolves to a valid target — an intentional, logged leak on
    /// this abnormal shutdown path is far safer than that use-after-free class of bug, matching
    /// this codebase's general fail-soft/degrade-rather-than-crash posture.
    /// </summary>
    public static void LogHookContextLeakedAfterFailedUnhook() =>
        Console.Error.WriteLine(
            "[Bastion.Win32] at least one WinEvent hook failed to unregister; intentionally " +
            "leaking its shared callback context rather than risking a use-after-free.");

    /// <summary>
    /// Logs a hotkey registration that failed (a bare zero <c>BOOL</c> return from
    /// <c>RegisterHotKey</c>). Per DESIGN.md §7's honesty note, this is treated as a conflict
    /// unconditionally — never gated on <c>GetLastError</c> returning specifically
    /// <c>ERROR_HOTKEY_ALREADY_REGISTERED</c>, since that code is observed behavior for this API,
    /// not a contractual guarantee. <paramref name="errorCode"/> is logged for diagnostics only,
    /// never branched on to decide whether the failure "counts" as a conflict.
    /// </summary>
    public static void LogHotkeyRegistrationConflict(int id, HOT_KEY_MODIFIERS modifiers, uint virtualKeyCode, WIN32_ERROR? errorCode) =>
        Console.Error.WriteLine(
            $"[Bastion.Win32] RegisterHotKey failed for id {id} (modifiers=0x{(uint)modifiers:X}, " +
            $"vk=0x{virtualKeyCode:X}); treating as a conflict regardless of the specific error " +
            $"(GetLastError observed: {errorCode?.ToString() ?? "none"}).");

    /// <summary>
    /// Logs a hotkey that failed to unregister via <c>UnregisterHotKey</c> during shutdown. Not
    /// immediately fatal on its own — the pump thread is exiting regardless — but a genuine
    /// occurrence should be visible rather than silently discarded, matching
    /// <see cref="LogUnhookWinEventFailed"/>'s own rationale for the WinEvent pump's equivalent
    /// shutdown-time failure.
    /// </summary>
    public static void LogUnregisterHotkeyFailed(int id) =>
        Console.Error.WriteLine($"[Bastion.Win32] UnregisterHotKey failed for id {id} during shutdown.");

    /// <summary>
    /// Logs a dispatched <see cref="HotkeyCommand"/> — the pre-composition-root stand-in for
    /// actually invoking a Reconciler-driven layout command (GitHub issue #10). See
    /// <see cref="LoggingHotkeyDispatchTarget"/>.
    /// </summary>
    public static void LogHotkeyInvoked(HotkeyCommand command) =>
        Console.Error.WriteLine($"[Bastion.Win32] hotkey invoked: {command}.");
}
