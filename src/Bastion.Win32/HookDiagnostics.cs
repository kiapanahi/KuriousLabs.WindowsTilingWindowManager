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
}
