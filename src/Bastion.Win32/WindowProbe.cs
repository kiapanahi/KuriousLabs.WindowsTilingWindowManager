using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
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

    /// <summary>
    /// Wraps <c>DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)</c>; returns
    /// <see langword="false"/> on a failing <c>HRESULT</c> (e.g. a window destroyed between
    /// whatever produced <paramref name="window"/> and this call — a routine race, not exceptional,
    /// matching this type's other <c>Try*</c> members). Shared by <c>WindowSystemAdapter</c>'s
    /// heartbeat read (GitHub issue #4) and <see cref="PlacementSystemAdapter"/>'s per-move
    /// invisible-border correction (GitHub issue #5, DESIGN.md §3.6c: "never cached per-class" —
    /// this method always re-reads live, never memoizes) rather than each duplicating the same
    /// <c>DwmGetWindowAttribute</c> stackalloc-buffer dance a third time.
    /// </summary>
    /// <remarks>
    /// DOCUMENTED CONTRACT (verified against
    /// https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute and
    /// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getwindowrect):
    /// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> "retrieves the extended frame bounds rectangle in screen
    /// space[, of type] RECT," and <c>GetWindowRect</c>'s own remarks point here explicitly to get
    /// "the visible window bounds, not including the invisible resize borders" — noting the two
    /// readings are <em>not</em> both DPI-virtualized ("unlike the Window Rect, the DWM Extended
    /// Frame Bounds are not adjusted for DPI"), a fact DESIGN.md §8 already accounts for via
    /// PerMonitorV2.
    /// </remarks>
    public static bool TryGetExtendedFrameBounds(HWND window, out RECT frameBounds)
    {
        // RECT is four sequential 4-byte LONG (int32) fields, 16 bytes total, matching
        // DwmCloakStateReader's own "size the stackalloc buffer to the documented pvAttribute
        // type" convention (interop.md §1).
        Span<byte> buffer = stackalloc byte[sizeof(int) * 4];

        HRESULT hr;
        unsafe
        {
            // The Span<byte> overload's generated signature is itself `unsafe` (it takes the
            // address of the span internally) even though nothing in this call site needs a raw
            // pointer of our own — CsWin32 emits it this way (interop.md §1: "confirm by
            // inspecting the generated partial"), matching DwmCloakStateReader's identical call
            // shape for DWMWA_CLOAKED.
            hr = PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, buffer);
        }

        if (hr.Failed)
        {
            frameBounds = default;
            return false;
        }

        frameBounds = MemoryMarshal.Read<RECT>(buffer);
        return true;
    }

    /// <summary>
    /// Wraps <c>GetWindowLongW(GWL_EXSTYLE)</c>. Shared by <c>WindowManageabilityInfoReader</c>
    /// (GitHub issue #3) and <see cref="PlacementSystemAdapter"/>'s <c>WS_EX_TOOLWINDOW</c>
    /// coordinate-space check (GitHub issue #5, DESIGN.md §3.6b) rather than each duplicating the
    /// same read.
    /// </summary>
    /// <remarks>
    /// DOCUMENTED CONTRACT: <c>GetWindowLongW</c>, not <c>GetWindowLongPtrW</c> — <c>GWL_EXSTYLE</c>
    /// is a 32-bit <c>DWORD</c> bitmask, never a pointer/handle value, so
    /// <a href="https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getwindowlongw">GetWindowLongW's
    /// own "superseded by GetWindowLongPtr" note is scoped to "if you are retrieving a pointer or a
    /// handle"</a> and does not apply here; using it also sidesteps <c>GetWindowLongPtrW</c> being
    /// unavailable to CsWin32 on an AnyCPU/no-explicit-RID compilation (interop.md §1.1's
    /// arch-specific-API risk).
    /// </remarks>
    public static WINDOW_EX_STYLE GetExtendedStyle(HWND window)
    {
        int raw = PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        return (WINDOW_EX_STYLE)unchecked((uint)raw);
    }

    /// <summary>
    /// Wraps <c>GetClassName</c>, returning <see cref="string.Empty"/> on failure (e.g. a window
    /// destroyed between whatever produced <paramref name="window"/> and this call — a routine
    /// race, not exceptional) rather than throwing. Window class names are documented (the
    /// <c>WNDCLASSEX</c> family's own <c>lpszClassName</c> remarks) to be at most 256 characters
    /// including the terminator, so the fixed-size buffer below is never truncated.
    /// </summary>
    public static string GetClassName(HWND window)
    {
        const int MaxClassNameLength = 256;
        Span<char> buffer = stackalloc char[MaxClassNameLength];
        int length = PInvoke.GetClassName(window, buffer);
        return length <= 0 ? string.Empty : new string(buffer[..length]);
    }

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
