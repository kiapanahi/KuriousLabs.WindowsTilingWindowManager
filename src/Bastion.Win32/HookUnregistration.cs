using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Windows.Win32.UI.Accessibility;

namespace Bastion.Win32;

/// <summary>
/// Applies a single hook's <c>UnhookWinEvent</c> result to the shared per-hook <c>GCHandle</c>
/// context registry (docs/engineering/interop.md §3.2). Extracted out of
/// <see cref="WinEventPumpService"/>'s shutdown path as a small, directly-callable, pure function
/// — a synthetic hook handle, a throwaway dictionary, and an already-known <see langword="bool"/>
/// result are enough to exercise it, no live hook required — so the
/// only-remove-the-registry-entry-on-success rule is independently unit-testable (see
/// <c>HookUnregistrationTests</c>) rather than only reachable via a Tier 3 real-hook integration
/// test.
/// </summary>
internal static class HookUnregistration
{
    /// <summary>
    /// Removes <paramref name="hook"/>'s entry from <paramref name="contexts"/> only when
    /// <paramref name="unhookSucceeded"/> is <see langword="true"/>, then returns
    /// <paramref name="unhookSucceeded"/> unchanged so a caller can fold results across a whole
    /// batch of hooks.
    /// </summary>
    /// <remarks>
    /// A failed <c>UnhookWinEvent</c> means the hook may still be registered and its callback may
    /// still run, so its shared <see cref="GCHandle"/> context must remain valid — interop.md
    /// §3.2 requires freeing the handle only after a successful unhook, and this is the registry
    /// side of that same rule: a failed unhook keeps its <paramref name="contexts"/> entry too,
    /// exactly like the handle itself must stay alive. Removing the entry unconditionally (the
    /// bug this method fixes) would let <see cref="WinEventPumpService"/>'s callback later look up
    /// a hook that failed to unhook, find no context, and silently drop an event it should still
    /// be able to enqueue — or worse, race a handle-free that happens for an unrelated reason.
    /// </remarks>
    public static bool ApplyResult(
        HWINEVENTHOOK hook,
        bool unhookSucceeded,
        ConcurrentDictionary<HWINEVENTHOOK, GCHandle> contexts)
    {
        if (unhookSucceeded)
        {
            contexts.TryRemove(hook, out _);
        }

        return unhookSucceeded;
    }
}
