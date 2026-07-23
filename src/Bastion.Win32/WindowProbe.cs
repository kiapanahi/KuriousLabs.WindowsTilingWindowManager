using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Bastion.Win32;

/// <summary>
/// Minimal, real read-only probe over the current top-level window set. Exercises the starter
/// CsWin32 surface (<c>EnumWindows</c>, <c>IsWindowVisible</c>, <c>GetWindowRect</c>,
/// <c>GetAncestor</c>) requested in <c>NativeMethods.txt</c>.
/// </summary>
/// <remarks>
/// TODO(DESIGN.md §3.1, §3.6): this is a synchronous snapshot probe, not the WinEvent-driven
/// ingest pump / topology service the design commits to — those need the dedicated pump thread,
/// bounded channel, and GCHandle-based hook-context registry owned by
/// docs/engineering/concurrency-performance.md and docs/engineering/interop.md §3.2.
/// </remarks>
/// <remarks>
/// Deliberately <see langword="internal"/>, not <see langword="public"/>: CsWin32 generates
/// <see cref="HWND"/> and <see cref="RECT"/> as internal types by default, and that default is
/// architecturally correct here — DESIGN.md's opaque-<c>WindowId</c> boundary (§3, §10) means no
/// raw HWND may ever cross out of <c>Bastion.Win32</c>. A future adapter in this assembly will
/// expose a <c>WindowId</c>-based surface to <c>Bastion.Daemon</c>; this probe is that adapter's
/// internal implementation detail, not the public contract.
/// </remarks>
internal static class WindowProbe
{
    // EnumWindows re-enters synchronously on the calling thread and completes before returning,
    // so a single thread-static accumulator (rather than the GCHandle-registry pattern
    // interop.md §3.2 requires for long-lived, arbitrarily-reentrant hook callbacks) is
    // sufficient here — there is no possibility of two enumerations racing on one thread.
    // "t_" (not the repo's usual "s_" for private statics) is the BCL's own convention marking a
    // field as [ThreadStatic] rather than a plain shared static — a meaningful distinction here,
    // since misreading this as ordinary shared state would be a real reentrancy bug. editorconfig
    // naming-symbol matching has no way to key off the [ThreadStatic] attribute (only the
    // `static` modifier, which both kinds share), so this one declaration is pragma-scoped rather
    // than expressed as a repo-wide rule. See .editorconfig's private_static_fields_s_prefix rule.
#pragma warning disable IDE1006 // Naming rule violation: BCL-style "t_" thread-static prefix.
    [ThreadStatic]
    private static List<HWND>? t_visibleWindows;
#pragma warning restore IDE1006

    /// <summary>Enumerates currently visible top-level windows.</summary>
    public static IReadOnlyList<HWND> EnumerateVisibleTopLevelWindows()
    {
        t_visibleWindows = [];
        try
        {
            unsafe
            {
                _ = PInvoke.EnumWindows(&OnEnumWindow, default);
            }

            return t_visibleWindows;
        }
        finally
        {
            t_visibleWindows = null;
        }
    }

    /// <summary>Wraps <c>GetWindowRect</c>; returns <see langword="false"/> on failure (e.g. a
    /// window destroyed between enumeration and this call — a routine race, not exceptional).</summary>
    public static bool TryGetBounds(HWND window, out RECT bounds) =>
        PInvoke.GetWindowRect(window, out bounds);

    /// <summary>Wraps <c>GetAncestor(hwnd, GA_ROOT)</c> to normalize a child/owned window to its
    /// root owner, per DESIGN.md §3.1's WinEvent-normalization rule.</summary>
    public static HWND GetRootAncestor(HWND window) =>
        PInvoke.GetAncestor(window, GET_ANCESTOR_FLAGS.GA_ROOT);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Mandatory catch-all: an exception must never escape an " +
            "[UnmanagedCallersOnly] callback across the native boundary. See " +
            "docs/engineering/interop.md §3.3.")]
    private static BOOL OnEnumWindow(HWND hwnd, LPARAM lParam)
    {
        try
        {
            if (PInvoke.IsWindowVisible(hwnd))
            {
                t_visibleWindows?.Add(hwnd);
            }

            return true;
        }
        catch (Exception ex)
        {
            // Mandatory catch-all — an exception must never escape an [UnmanagedCallersOnly]
            // method across the native boundary. See docs/engineering/interop.md §3.3.
            HookDiagnostics.LogCallbackFault(ex);
            return true;
        }
    }
}
