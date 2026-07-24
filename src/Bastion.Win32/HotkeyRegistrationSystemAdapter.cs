using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Bastion.Win32;

/// <summary>The real <see cref="IHotkeyRegistrationSystem"/>: <c>RegisterHotKey</c>/<c>UnregisterHotKey</c> against the calling thread.</summary>
/// <remarks>
/// <b><c>Marshal.GetLastPInvokeError()</c>, not <c>GetLastWin32Error()</c> — confirmed against the
/// actual generated partial, not assumed from <c>SetWindowPos</c>'s precedent.</b> Inspecting
/// <c>obj/**/Generated/CsWin32/Windows.Win32.NativeMethods.g.cs</c> for this project's CsWin32
/// (0.3.298) output shows both <c>RegisterHotKey</c> and <c>UnregisterHotKey</c> — like
/// <c>SetWindowPos</c>/<c>SetWindowPlacement</c>/<c>BeginDeferWindowPos</c>/<c>DeferWindowPos</c>/
/// <c>EndDeferWindowPos</c> before them (docs/engineering/interop.md §6) — wrap their raw
/// <c>[DllImport]</c> extern in a friendly method that does exactly
/// <c>Marshal.SetLastSystemError(0)</c> immediately before the call and
/// <c>Marshal.SetLastPInvokeError(Marshal.GetLastSystemError())</c> immediately after, which is
/// CsWin32's own hand-rolled substitute for the automatic <c>SetLastError</c> capture that
/// <c>DisableRuntimeMarshallingAttribute</c> disables (interop.md §1.2). Both APIs' own documentation
/// ("To get extended error information, call GetLastError") plus this confirmed bridge make
/// <c>Marshal.GetLastPInvokeError()</c> the correct read, per that API's own documented contract
/// ("functionally equivalent to GetLastWin32Error ... should be preferred").
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and intended to be " +
        "registered as the production IHotkeyRegistrationSystem once Bastion.Daemon's composition " +
        "root is wired (GitHub issue #10) — not yet wired as of this change. Same documented CA1812 " +
        "false-positive shape as PlacementSystemAdapter/WinEventPumpService/Coalescer. [Verified " +
        "against learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1812 while " +
        "implementing this issue: CA1812 is not enabled by default in .NET 10, and is separately " +
        "auto-disabled for any assembly (like this one) that applies InternalsVisibleToAttribute " +
        "unless ignore_internalsvisibleto is set — so this suppression is inert today, kept only " +
        "for consistency with this file's established siblings and forward-compatibility against a " +
        "future analyzer-default or InternalsVisibleTo-handling change.]")]
internal sealed class HotkeyRegistrationSystemAdapter : IHotkeyRegistrationSystem
{
    /// <inheritdoc/>
    public HotkeyCallResult Register(int id, HOT_KEY_MODIFIERS modifiers, uint virtualKeyCode) =>
        PInvoke.RegisterHotKey(HWND.Null, id, modifiers, virtualKeyCode)
            ? HotkeyCallResult.Ok
            : HotkeyCallResult.Fail((WIN32_ERROR)Marshal.GetLastPInvokeError());

    /// <inheritdoc/>
    public bool Unregister(int id) => PInvoke.UnregisterHotKey(HWND.Null, id);
}
