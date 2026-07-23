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
}
