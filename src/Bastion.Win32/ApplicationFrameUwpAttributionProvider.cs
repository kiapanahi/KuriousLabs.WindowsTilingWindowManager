using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// Real <see cref="IUwpAttributionProvider"/>: walks an ApplicationFrameWindow's children for a
/// <c>Windows.UI.Core.CoreWindow</c>, then delegates to <see cref="IProcessAumidReader"/> against
/// that child's owning process — reusing the exact same process-AUMID read
/// <see cref="WindowIdentityResolver"/>'s own chain uses for a window's own process, just retargeted
/// at the child's PID instead.
/// </summary>
/// <remarks>
/// DOCUMENTED CONTRACT for the mechanics (verified against learn.microsoft.com):
/// <c>EnumChildWindows(HWND hWndParent, WNDENUMPROC lpEnumFunc, LPARAM lParam)</c> — "continues
/// until the last child window is enumerated or the callback function returns FALSE"; class-name
/// comparison uses <see cref="WindowProbe.GetClassName"/>. The specific claim that an
/// ApplicationFrameWindow hosts exactly one <c>Windows.UI.Core.CoreWindow</c> child belonging to
/// the real per-app process is <b>observed behavior, not documented</b> — see
/// <see cref="IUwpAttributionProvider"/>'s own remarks for why that is this type's problem to
/// isolate, not the identity chain's.
/// </remarks>
internal sealed class ApplicationFrameUwpAttributionProvider(IProcessAumidReader processAumidReader)
    : IUwpAttributionProvider
{
    private const string ApplicationFrameWindowClassName = "ApplicationFrameWindow";
    private const string CoreWindowClassName = "Windows.UI.Core.CoreWindow";

    // EnumChildWindows re-enters synchronously on the calling thread and completes before
    // returning, so a single thread-static accumulator (rather than the GCHandle-registry pattern
    // interop.md §3.2 requires for long-lived, arbitrarily-reentrant hook callbacks) is sufficient
    // here, mirroring WindowProbe's EnumWindows callback for the identical reason.
    // "t_" (not this repo's usual "s_" for private statics) is the BCL's own convention marking a
    // field as [ThreadStatic]; editorconfig naming rules can't key off the attribute (only the
    // `static` modifier, which both share), so this declaration is pragma-scoped rather than a
    // repo-wide rule — see src/Bastion.Win32/WindowProbe.cs, the established precedent.
#pragma warning disable IDE1006 // Naming rule violation: BCL-style "t_" thread-static prefix.
    [ThreadStatic]
    private static HWND t_foundCoreWindow;
#pragma warning restore IDE1006

    /// <inheritdoc/>
    public string? TryGetAumid(HWND hwnd)
    {
        if (!string.Equals(WindowProbe.GetClassName(hwnd), ApplicationFrameWindowClassName, StringComparison.Ordinal))
        {
            return null;
        }

        HWND coreWindow = FindCoreWindowChild(hwnd);
        if (coreWindow.IsNull)
        {
            return null;
        }

        uint pid = PInvoke.GetWindowThreadProcessId(coreWindow, out uint coreWindowPid) == 0
            ? 0
            : coreWindowPid;
        return pid == 0 ? null : processAumidReader.TryGetAumid(pid);
    }

    private static HWND FindCoreWindowChild(HWND applicationFrameWindow)
    {
        t_foundCoreWindow = default;
        try
        {
            unsafe
            {
                _ = PInvoke.EnumChildWindows(applicationFrameWindow, &OnEnumChildWindow, default);
            }

            return t_foundCoreWindow;
        }
        finally
        {
            t_foundCoreWindow = default;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Mandatory catch-all: an exception must never escape an " +
            "[UnmanagedCallersOnly] callback across the native boundary. See " +
            "docs/engineering/interop.md §3.3.")]
    private static BOOL OnEnumChildWindow(HWND hwnd, LPARAM lParam)
    {
        try
        {
            if (string.Equals(WindowProbe.GetClassName(hwnd), CoreWindowClassName, StringComparison.Ordinal))
            {
                t_foundCoreWindow = hwnd;
                return false; // found it — stop enumeration (EnumChildWindows' documented FALSE contract).
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
